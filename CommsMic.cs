using System.Runtime.InteropServices;
using Windows.Media.Audio;
using Windows.Media.Capture;

namespace Ets2Gps;

/// <summary>
/// Captures the default communications microphone the way calls do (Windows' communications processing: noise suppression, automatic gain, and
/// enhancements such as Voice Clarity) and hands it on as 16 kHz mono 16-bit PCM in a stream the speech recognizer reads. A wireless or noisy microphone
/// that is too quiet or full of static in its raw form is much clearer this way.
/// </summary>
public sealed class CommsMic : IDisposable
{
    public const int Rate = 16000;
    AudioGraph? graph;
    AudioDeviceInputNode? input;
    AudioFrameOutputNode? output;
    readonly PcmPipe pipe = new();
    int inRate, channels;
    double step, phase, acc; int accN;

    /// <summary>The audio for the recognizer (blocks until there is some; ends when this is disposed).</summary>
    public Stream Stream => pipe;
    public string Device { get; private set; } = "";
    /// <summary>Seconds of audio captured so far.</summary>
    public double Seconds => Interlocked.Read(ref samplesOut) / (double)Rate;
    long samplesOut;
    // automatic gain: speech peaks are brought towards Target, never amplified more than MaxGain; the gain falls fast (no clipping) and rises slowly,
    // starting at 1 so a strong microphone is not overdriven before it settles
    const double Target = 0.3, MaxGain = 30;
    double gain = 1, blockPeak, noise = 1; int blockN;
    /// <summary>The first error while reading the audio, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Opens the microphone; null on success, otherwise why not.</summary>
    public async Task<string?> StartAsync()
    {
        var gr = await AudioGraph.CreateAsync(new AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.Communications));
        if (gr.Status != AudioGraphCreationStatus.Success) return "audio: " + gr.Status;
        graph = gr.Graph;
        var ir = await graph.CreateDeviceInputNodeAsync(MediaCategory.Communications);
        if (ir.Status != AudioDeviceNodeCreationStatus.Success) return "microphone: " + ir.Status;
        input = ir.DeviceInputNode;
        Device = input.Device?.Name ?? "";
        output = graph.CreateFrameOutputNode();
        input.AddOutgoingConnection(output);
        inRate = (int)graph.EncodingProperties.SampleRate;
        channels = Math.Max(1, (int)graph.EncodingProperties.ChannelCount);
        step = (double)inRate / Rate;
        graph.QuantumStarted += OnQuantum;
        graph.Start();
        return null;
    }

    // IMemoryBufferByteAccess (a classic COM interface the .NET projection cannot cast to): queried and called through its vtable
    static Guid byteAccessIid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBufferFn(IntPtr self, out IntPtr buffer, out uint capacity);
    static GetBufferFn? getBuffer;

    static IntPtr BufferPointer(Windows.Foundation.IMemoryBufferReference reference)
    {
        IntPtr unk = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref byteAccessIid, out IntPtr ba));
        try
        {
            getBuffer ??= Marshal.GetDelegateForFunctionPointer<GetBufferFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(ba), 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(getBuffer(ba, out IntPtr data, out _));
            return data;
        }
        finally { Marshal.Release(ba); }
    }

    float[] floats = Array.Empty<float>();

    void OnQuantum(AudioGraph sender, object args)
    {
        try
        {
            using var frame = output!.GetFrame();
            using var buf = frame.LockBuffer(Windows.Media.AudioBufferAccessMode.Read);
            using var reference = buf.CreateReference();
            IntPtr data = BufferPointer(reference);
            int n = (int)(buf.Length / 4);
            if (n == 0) return;
            if (floats.Length < n) floats = new float[n];
            Marshal.Copy(data, floats, 0, n);
            // down-mix to mono and down-sample to 16 kHz by averaging the input samples of each output sample (enough for speech)
            var pcm = new byte[(int)(n / channels / step + 2) * 2];
            int o = 0;
            for (int i = 0; i + channels <= n; i += channels)
            {
                float m = 0; for (int c = 0; c < channels; c++) m += floats[i + c];
                acc += m / channels; accN++;
                phase += 1;
                if (phase >= step)
                {
                    phase -= step;
                    double x = acc / accN;
                    acc = 0; accN = 0;
                    blockPeak = Math.Max(blockPeak, Math.Abs(x));
                    if (++blockN >= Rate / 10)   // every 0.1 s
                    {
                        // the background level falls at once and rises slowly; louder than three times that is speech
                        noise = blockPeak < noise ? blockPeak : noise * 1.01;
                        if (blockPeak * gain > 0.6) gain = Math.Max(1, 0.4 / Math.Max(blockPeak, 1e-6));          // too loud: down at once
                        else if (blockPeak > noise * 3 && blockPeak > 0.001)
                        {
                            double want = Math.Clamp(Target / blockPeak, 1, MaxGain);
                            gain = want > gain ? Math.Min(want, gain * 1.03) : Math.Max(want, gain * 0.8);
                        }
                        blockPeak = 0; blockN = 0;
                    }
                    // peaks above 0.7 are rounded off rather than clipped, so a sudden loud word stays recognisable
                    double y = x * gain, ay = Math.Abs(y);
                    if (ay > 0.7) y = Math.Sign(y) * (0.7 + 0.3 * Math.Tanh((ay - 0.7) / 0.3));
                    int v = (int)Math.Round(y * 32767);
                    Interlocked.Increment(ref samplesOut);
                    if (o + 2 <= pcm.Length) { pcm[o++] = (byte)v; pcm[o++] = (byte)(v >> 8); }
                }
            }
            if (o > 0) pipe.Push(pcm, 0, o);
        }
        catch (Exception ex) { Error ??= ex.GetType().Name + ": " + ex.Message; /* one bad frame must not stop the microphone */ }
    }

    public void Dispose()
    {
        try { graph?.Stop(); } catch { }
        try { graph?.Dispose(); } catch { }
        graph = null;
        pipe.Close();
    }

    /// <summary>A stream the capture writes to and the recognizer reads from; reads wait for audio rather than reporting the end.</summary>
    sealed class PcmPipe : Stream
    {
        readonly Queue<byte[]> chunks = new();
        byte[]? cur; int curPos; bool closed; long queued, read;

        public void Push(byte[] b, int off, int len)
        {
            var c = new byte[len]; Buffer.BlockCopy(b, off, c, 0, len);
            lock (chunks)
            {
                if (closed) return;
                if (queued > Rate * 2 * 10) { chunks.Clear(); queued = 0; }   // the recognizer stalled: drop over 10 s of backlog
                chunks.Enqueue(c); queued += len;
                Monitor.PulseAll(chunks);
            }
        }

        /// <summary>Fills the whole request, waiting for the audio as needed: the recognizer takes a shorter read as the end of the audio.</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            int done = 0;
            lock (chunks)
            {
                while (done < count)
                {
                    if (cur == null || curPos >= cur.Length)
                    {
                        if (chunks.Count > 0) { cur = chunks.Dequeue(); curPos = 0; queued -= cur.Length; continue; }
                        if (closed) break;
                        Monitor.Wait(chunks, 1000);
                        continue;
                    }
                    int k = Math.Min(count - done, cur.Length - curPos);
                    Buffer.BlockCopy(cur, curPos, buffer, offset + done, k);
                    curPos += k; done += k;
                }
                read += done;
            }
            return done;
        }

        public override void Close() { lock (chunks) { closed = true; Monitor.PulseAll(chunks); } base.Close(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        // the recognizer asks for these although the stream is live: a stream far longer than any drive (the recognizer stops at the length it is given), positioned where it has read up to
        public override long Length => long.MaxValue / 2;
        public override long Position { get { lock (chunks) return read; } set { } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { lock (chunks) return read; }
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
