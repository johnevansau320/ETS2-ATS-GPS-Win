using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ets2Gps;

public sealed class StopPoint { public float X { get; set; } public float Z { get; set; } public string Name { get; set; } = ""; }

public sealed class SavedTrip
{
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "HEAVY";
    public StopPoint? Start { get; set; }          // where a planned trip begins (null = wherever the truck is)
    public StopPoint Dest { get; set; } = new();
    public List<StopPoint> Stops { get; set; } = new();
    public float LengthM { get; set; }
    public bool AvoidFerries { get; set; }
}

/// <summary>Everything remembered between runs (%APPDATA%\ETS2-GPS\windows_app.json).</summary>
public sealed class AppSettings
{
    public string TelemetryHost { get; set; } = "127.0.0.1";
    public bool ServerEnabled { get; set; } = true;        // built-in web server for the tablet, browsers and the lane overlay
    public int ServerPort { get; set; } = 31378;
    public string RouteMode { get; set; } = "HEAVY";
    public bool AvoidFerries { get; set; } = false;        // route planning: never use ferries / car trains
    public float DistScale { get; set; } = 19f;
    /// <summary>true: distances in real metres (map units); false: game (Sim) distances, map metres x DistScale.</summary>
    public bool RealDistances { get; set; } = false;
    public string UnitSystem { get; set; } = "game";   // "game" (the game's own units), "metric" or "imperial"
    /// <summary>While cruise control is on, keep it on the current speed limit (taps the game's cruise faster / slower keys).</summary>
    public bool CruiseAutoLimit { get; set; } = true;
    /// <summary>Show the lane guidance panel on the map (and in the lane overlay window).</summary>
    public bool ShowLanes { get; set; } = true;
    public bool FollowGameGps { get; set; } = true;   // offer to route to the game's GPS destination when there is no job
    public bool AutoQuickSave { get; set; } = true;   // press the game's quick-save key when its GPS gets a new destination, so it is read straight away
    public bool VoiceOn { get; set; } = true;
    /// <summary>Listen on the microphone for spoken commands ("GPS, nearest fuel station"). Off by default; recognised on this PC, nothing is recorded or sent.</summary>
    public bool VoiceControl { get; set; } = false;
    public string VoiceWake { get; set; } = "GPS";
    /// <summary>Push to talk: listen only while <see cref="VoicePttButton"/> is held, so background noise cannot set off commands; the wake word is then optional.</summary>
    public bool VoicePushToTalk { get; set; } = false;
    /// <summary>The push-to-talk button: "key:&lt;virtual key&gt;" or "joy:&lt;button&gt;:&lt;controller name&gt;" (see <see cref="PushToTalk"/>).</summary>
    public string VoicePttButton { get; set; } = "";
    /// <summary>Speech recognizer for voice control: "classic" (the desktop one, reliable with any default microphone) or "modern"
    /// (Windows.Media.SpeechRecognition: better accuracy, but on some PCs it receives no audio inside the app).</summary>
    public string VoiceRecognizer { get; set; } = "classic";
    /// <summary>Look for a newer release on GitHub about once a day (nothing is downloaded; the notice opens the release page).</summary>
    public bool CheckUpdates { get; set; } = true;
    public long LastUpdateCheckUtc { get; set; }
    public string SkippedVersion { get; set; } = "";
    public int VoiceRate { get; set; } = 0;                // -10 .. 10
    public string VoiceName { get; set; } = "";
    public bool Topmost { get; set; } = false;             // keep the window above other apps
    public double Opacity { get; set; } = 1.0;
    public bool OverlayAutoStart { get; set; } = true;     // start the lane overlay window with the app when it is not already running
    public string OverlayPath { get; set; } = "";           // Ets2LaneOverlay.exe (empty = look in the usual places)
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; } = false;
    public List<StopPoint> Avoid { get; set; } = new();
    public List<SavedTrip> Trips { get; set; } = new();
    /// <summary>The game used last ("ets2" or "ats"): its map is loaded at start until the telemetry says which game is running.</summary>
    public string Game { get; set; } = "ets2";
    // American Truck Simulator has its own map, so its avoided roads, saved trips and distance scale are kept apart (the ones above are ETS2's)
    public List<StopPoint> AtsAvoid { get; set; } = new();
    public List<SavedTrip> AtsTrips { get; set; } = new();
    public float AtsDistScale { get; set; } = 20f;

    [JsonIgnore] public List<StopPoint> GameAvoid => Games.Current == Games.Ats ? AtsAvoid : Avoid;
    [JsonIgnore] public List<SavedTrip> GameTrips => Games.Current == Games.Ats ? AtsTrips : Trips;
    [JsonIgnore] public float GameDistScale
    {
        get => Games.Current == Games.Ats ? AtsDistScale : DistScale;
        set { if (Games.Current == Games.Ats) AtsDistScale = value; else DistScale = value; }
    }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ETS2-GPS");
    public static string? PathOverride;   // --config <file> (used for testing)
    public static string FilePath => PathOverride ?? Path.Combine(Dir, "windows_app.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* read-only profile: not fatal */ }
    }
}

/// <summary>
/// Spoken guidance through the PC's default audio device (headset). Uses the modern Windows voices (Windows.Media, e.g. the male
/// "Microsoft George") when one is chosen, and the classic desktop voices (SAPI) otherwise: the classic engine cannot see the modern voices.
/// A "Voicebox: ..." voice is generated by the Voicebox app running on this PC (its local API on port 17493); when Voicebox does not answer
/// in time the prompt is spoken with Microsoft George instead, so no direction is ever lost.
/// </summary>
public sealed class Voice : IDisposable
{
    SpeechSynthesizer? tts;
    Windows.Media.SpeechSynthesis.SpeechSynthesizer? modern;
    Windows.Media.Playback.MediaPlayer? player;
    int seq;
    double playRate = 1.0;

    public const string VoiceboxPrefix = "Voicebox: ";
    const string VoiceboxUrl = "http://127.0.0.1:17493";
    static readonly HttpClient VbHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    string? vbName, vbId, vbEngine;
    readonly Dictionary<string, byte[]> vbCache = new();   // prompts repeat a lot ("Recalculating", "Turn left"): generated once
    public bool Enabled { get; set; } = true;
    public string? Problem { get; private set; }

    int pending; long endedAt, startedAt;
    /// <summary>True while a prompt is being spoken (and for a moment after): voice control must not listen to the app's own voice. Never longer than
    /// 20 s after a prompt starts, so a lost "playback ended" signal cannot leave voice control deaf.</summary>
    public bool Busy => (Volatile.Read(ref pending) > 0 && Environment.TickCount64 - Interlocked.Read(ref startedAt) < 20_000)
                        || Environment.TickCount64 - Interlocked.Read(ref endedAt) < 700;

    static IReadOnlyList<Windows.Media.SpeechSynthesis.VoiceInformation> ModernVoices()
    {
        try { return Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.ToList(); }
        catch { return Array.Empty<Windows.Media.SpeechSynthesis.VoiceInformation>(); }
    }

    /// <summary>Voicebox voices (when Voicebox is running), modern voices (by their display name), then classic ones that have no modern twin ("Microsoft Zira Desktop").</summary>
    public IEnumerable<string> Voices()
    {
        var list = new List<string>();
        try
        {
            using var cts = new CancellationTokenSource(1000);
            var json = VbHttp.GetStringAsync(VoiceboxUrl + "/profiles", cts.Token).GetAwaiter().GetResult();
            foreach (var p in JsonDocument.Parse(json).RootElement.EnumerateArray())
                if (p.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) list.Add(VoiceboxPrefix + name);
        }
        catch { /* Voicebox is not running */ }
        list.AddRange(ModernVoices().Select(v => v.DisplayName));
        try
        {
            using var s = new SpeechSynthesizer();
            foreach (var v in s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name))
                if (!list.Contains(v) && !list.Contains(v.Replace(" Desktop", ""))) list.Add(v);
        }
        catch { /* no classic speech */ }
        return list;
    }

    public void Configure(int rate, string voiceName)
    {
        Stop();
        playRate = Math.Clamp(Math.Pow(3, rate / 10.0), 0.5, 3.0);
        if (voiceName.StartsWith(VoiceboxPrefix, StringComparison.Ordinal))
        {
            if (vbName != voiceName[VoiceboxPrefix.Length..]) { vbName = voiceName[VoiceboxPrefix.Length..]; vbId = null; lock (vbCache) vbCache.Clear(); }
            // the stand-in while Voicebox is busy or closed: George if installed, else the default voice
            voiceName = ModernVoices().Any(v => v.DisplayName == "Microsoft George") ? "Microsoft George" : "";
            EnsurePlayer();
            _ = VoiceboxAudio("Ready.");   // loads the voice now: its first prompt takes seconds, later ones a fraction of a second
        }
        else { vbName = null; vbId = null; lock (vbCache) vbCache.Clear(); }
        var mv = string.IsNullOrEmpty(voiceName) ? null : ModernVoices().FirstOrDefault(v => v.DisplayName == voiceName);
        if (mv != null)
        {
            try
            {
                modern ??= new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
                modern.Voice = mv;
                modern.Options.SpeakingRate = Math.Clamp(Math.Pow(3, rate / 10.0), 0.5, 6.0);   // same feel as the classic -10..10 scale
                EnsurePlayer();
                Problem = null;
                return;
            }
            catch (Exception e) { modern = null; Problem = "Speech is not available: " + e.Message; }
        }
        modern = null;
        try
        {
            if (tts == null)
            {
                tts = new SpeechSynthesizer();
                // A cut utterance reports late: only its own turn may end, never a newer one's.
                tts.SpeakCompleted += (_, e) => Ended(classicSeqs.TryRemove(e.Prompt, out var s) ? s : -1, !e.Cancelled);
            }
            tts.SetOutputToDefaultAudioDevice();
            tts.Rate = Math.Clamp(rate, -10, 10);
            if (!string.IsNullOrEmpty(voiceName)) { try { tts.SelectVoice(voiceName); } catch { /* voice not installed */ } }
            Problem = null;
        }
        catch (Exception e) { Problem = "Speech is not available: " + e.Message; }
    }

    void EnsurePlayer()
    {
        if (player != null) return;
        player = new Windows.Media.Playback.MediaPlayer();
        // The player says nothing about which source ended: these belong to the utterance last started on it.
        player.MediaEnded += (_, _) => Ended(Volatile.Read(ref playerSeq));
        player.MediaFailed += (_, _) => Ended(Volatile.Read(ref playerSeq));
    }

    /// <summary>The seq of the utterance last started on the media player.</summary>
    int playerSeq = -1;
    /// <summary>The seq of each classic (System.Speech) prompt still speaking.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<Prompt, int> classicSeqs = new();

    /// <summary>Utterance <paramref name="mySeq"/> ended or failed: only its own speaking turn is handed back.</summary>
    void Ended(int mySeq, bool endTurn = true)
    {
        if (Interlocked.Decrement(ref pending) < 0) Interlocked.Exchange(ref pending, 0);
        Interlocked.Exchange(ref endedAt, Environment.TickCount64);
        if (endTurn) EndTurn(mySeq);
    }

    /// <summary>One utterance's speaking turn with the dispatcher app (shared semaphore). Each utterance has its own handle, so a late or cut
    /// utterance can only ever release its own turn, never a newer one's.</summary>
    sealed class Turn
    {
        public readonly int Seq;
        readonly SpeakingTurn? t;
        readonly CancellationTokenSource cts = new();
        Task? acquiring;
        volatile bool settled;

        public Turn(int seq)
        {
            Seq = seq;
            try { t = new SpeakingTurn(); } catch { t = null; }   // e.g. access denied: speak without a turn
        }

        /// <summary>Waits for the turn, at most 8 s; on timeout or failure the caller speaks anyway. Safe to await more than once.</summary>
        public Task AcquireAsync() => acquiring ??= DoAcquire();

        async Task DoAcquire()
        {
            if (t == null) { settled = true; return; }
            try { await t.AcquireAsync(TimeSpan.FromSeconds(8), cts.Token).ConfigureAwait(false); }
            catch { /* speak without the turn */ }
            finally
            {
                settled = true;
                if (cts.IsCancellationRequested) Finish();   // cut while waiting: give back a turn that arrived too late
            }
        }

        /// <summary>The utterance ended, or failed: hand the turn back.</summary>
        public void Finish()
        {
            try { t?.Dispose(); } catch { }
        }

        /// <summary>Cut by a newer prompt.</summary>
        public void Cancel()
        {
            try { cts.Cancel(); } catch { }
            try { t?.Release(); } catch { }
            if (settled) Finish();
        }
    }

    Turn? curTurn;

    /// <summary>Ends the current turn if it belongs to utterance <paramref name="seq"/>; any other seq does nothing.</summary>
    void EndTurn(int seq)
    {
        var u = Volatile.Read(ref curTurn);
        if (u == null || u.Seq != seq) return;
        if (Interlocked.CompareExchange(ref curTurn, null, u) == u) u.Finish();
    }

    // The turn is taken only once the audio is ready, so the dispatcher is never kept waiting during synthesis.
    async Task<bool> TakeTurn(Turn u, int mySeq)
    {
        await u.AcquireAsync();
        if (mySeq == Volatile.Read(ref seq)) return true;
        u.Cancel();   // a newer prompt arrived while waiting
        return false;
    }

    /// <summary>Speaks even when voice guidance is switched off here (prompts the tablet sends to this PC).</summary>
    public void SayAlways(string text) => Speak(text);

    /// <summary>Speaks the text; newer prompts replace anything still being spoken (stale directions are worse than none).</summary>
    public void Say(string text)
    {
        if (!Enabled) return;
        Speak(text);
    }

    void Stop()
    {
        Interlocked.Increment(ref seq);
        try { Interlocked.Exchange(ref curTurn, null)?.Cancel(); } catch { }
        try { tts?.SpeakAsyncCancelAll(); } catch { }
        try { if (player != null) { player.Pause(); player.Source = null; } } catch { }
        Interlocked.Exchange(ref pending, 0);
    }

    void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        int mySeq = -1;
        try
        {
            if (tts == null && modern == null && vbName == null) Configure(0, "");
            Stop();
            Interlocked.Increment(ref pending);
            Interlocked.Exchange(ref startedAt, Environment.TickCount64);
            mySeq = Volatile.Read(ref seq);
            var turn = new Turn(mySeq);
            Volatile.Write(ref curTurn, turn);
            if (vbName != null) _ = SpeakVoicebox(text, mySeq, turn);
            else if (modern != null) _ = SpeakModern(text, mySeq, turn);
            else if (tts != null) _ = SpeakClassic(text, mySeq, turn);
            else Ended(mySeq);
        }
        catch (Exception e) { Ended(mySeq); Problem = "Speech failed: " + e.Message; }
    }

    async Task SpeakClassic(string text, int mySeq, Turn turn)
    {
        try
        {
            if (!await TakeTurn(turn, mySeq)) return;
            var prompt = new Prompt(text);
            classicSeqs[prompt] = mySeq;
            tts!.SpeakAsync(prompt);
        }
        catch (Exception e) { Ended(mySeq); Problem = "Speech failed: " + e.Message; }
    }

    async Task SpeakModern(string text, int mySeq, Turn turn)
    {
        try
        {
            var stream = await modern!.SynthesizeTextToStreamAsync(text);
            if (mySeq != Volatile.Read(ref seq)) { stream.Dispose(); return; }   // a newer prompt arrived meanwhile
            if (!await TakeTurn(turn, mySeq)) { stream.Dispose(); return; }
            Volatile.Write(ref playerSeq, mySeq);
            player!.Source = Windows.Media.Core.MediaSource.CreateFromStream(stream, stream.ContentType);
            player.Play();
        }
        catch (Exception e) { Ended(mySeq); Problem = "Speech failed: " + e.Message; }
    }

    async Task SpeakVoicebox(string text, int mySeq, Turn turn)
    {
        var wav = await VoiceboxAudio(text, 3000);
        if (mySeq != Volatile.Read(ref seq)) return;   // a newer prompt arrived meanwhile
        if (wav == null)
        {
            Problem = "Voicebox is not answering: using the Windows voice";
            if (modern != null) await SpeakModern(text, mySeq, turn);
            else if (tts != null) await SpeakClassic(text, mySeq, turn);
            else Ended(mySeq);
            return;
        }
        try
        {
            if (!await TakeTurn(turn, mySeq)) return;
            Problem = null;
            Volatile.Write(ref playerSeq, mySeq);
            player!.Source = Windows.Media.Core.MediaSource.CreateFromStream(new MemoryStream(wav).AsRandomAccessStream(), "audio/wav");
            player.PlaybackSession.PlaybackRate = playRate;
            player.Play();
        }
        catch (Exception e) { Ended(mySeq); Problem = "Speech failed: " + e.Message; }
    }

    /// <summary>The prompt as WAV audio from Voicebox, or null when it does not answer within the time allowed.</summary>
    async Task<byte[]?> VoiceboxAudio(string text, int timeoutMs = 30000)
    {
        string? name = vbName;
        if (name == null) return null;
        lock (vbCache) if (vbCache.TryGetValue(text, out var hit)) return hit;
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            if (vbId == null)
            {
                var json = await VbHttp.GetStringAsync(VoiceboxUrl + "/profiles", cts.Token);
                foreach (var p in JsonDocument.Parse(json).RootElement.EnumerateArray())
                    if (p.GetProperty("name").GetString() == name)
                    {
                        vbEngine = p.TryGetProperty("default_engine", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                        vbId = p.GetProperty("id").GetString();
                    }
                if (vbId == null) return null;
            }
            var body = JsonSerializer.Serialize(new Dictionary<string, object?> { ["profile_id"] = vbId, ["text"] = text, ["engine"] = vbEngine ?? "kokoro" });
            using var res = await VbHttp.PostAsync(VoiceboxUrl + "/generate/stream", new StringContent(body, Encoding.UTF8, "application/json"), cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            var wav = await res.Content.ReadAsByteArrayAsync(cts.Token);
            if (wav.Length < 100) return null;
            lock (vbCache) { if (vbCache.Count > 400) vbCache.Clear(); vbCache[text] = wav; }
            return wav;
        }
        catch { return null; }
    }

    public void Dispose() { try { Interlocked.Exchange(ref curTurn, null)?.Cancel(); } catch { } tts?.Dispose(); modern?.Dispose(); player?.Dispose(); }
}
