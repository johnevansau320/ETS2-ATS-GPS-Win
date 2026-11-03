using System.Globalization;
using System.Speech.Recognition;

namespace Ets2Gps;

/// <summary>
/// Listens on the PC's default microphone for spoken commands ("GPS, nearest fuel station") with the speech recognizer that comes with Windows: the modern
/// one (Windows.Media.SpeechRecognition, much better with headset and wireless microphones), or the classic desktop one when the modern one cannot start.
/// Everything is recognised on this PC: no audio is recorded, stored or sent anywhere. Every command starts with the wake word, so ordinary speech and the
/// game's sound do not trigger anything, and recognition pauses while the app itself is speaking.
/// </summary>
public sealed class VoiceControl : IDisposable
{
    // Measured with a real voice through a desk microphone, the recognizer gets the words right but its confidence jumps about: short words
    // ("how", "is", "it") often score near 0 and the overall score of correct commands ranged 0.4 - 0.75. A result is therefore judged by its strongest
    // words: the wake word must really have been heard (one that the recognizer invents to fit ordinary speech into a command scores about 0) and at least
    // one word of the command must be clear.
    /// <summary>Recognitions below this overall confidence are ignored.</summary>
    public const float MinConfidence = 0.35f;
    /// <summary>The best-heard word of the wake word must reach this.</summary>
    public const float MinWakeConfidence = 0.3f;
    /// <summary>The best-heard word of the command must reach this.</summary>
    public const float MinCommandConfidence = 0.6f;
    /// <summary>The modern recognizer's raw confidence must reach this (correct commands measured 0.27 - 0.5 through a wireless microphone).</summary>
    public const double MinModernConfidence = 0.15;

    SpeechRecognitionEngine? engine;
    CommsMic? mic;
    Windows.Media.SpeechRecognition.SpeechRecognizer? modern;
    int modernRun;   // changes on every Start / Stop, so a session ending late cannot restart a stopped listener
    readonly Func<bool> muted;

    /// <summary>Raised on a background thread: command key (for example "poi:1", "job", "far"), the words heard, the confidence.</summary>
    public event Action<string, string, float>? Command;

    public string? Problem { get; private set; }
    public bool Active => engine != null || modern != null;
    /// <summary>Which recognizer is listening: "modern" or "classic".</summary>
    public string EngineName => modern != null ? "modern" : "classic";
    public string Wake { get; private set; } = "GPS";

    public VoiceControl(Func<bool> muted) { this.muted = muted; }

    /// <summary>Points of interest that can be asked for: key, the words that name it.</summary>
    public static readonly (string Key, string[] Words)[] PoiWords =
    {
        ("poi:1", new[] { "fuel station", "petrol station", "gas station", "filling station", "fuel" }),
        ("poi:3", new[] { "rest area", "rest stop", "parking area", "parking", "place to rest" }),
        ("poi:2", new[] { "service centre", "service center", "service station" }),
        ("poi:4", new[] { "weigh station", "weighing station", "weight station", "weighbridge" }),
        ("poi:6", new[] { "garage" }),
        ("poi:5", new[] { "truck dealer", "dealer", "dealership" }),
        ("poi:9", new[] { "ferry", "ferry terminal" }),
    };

    /// <summary>Everything that can be said after the wake word (key, phrases), apart from the "find the nearest ..." family.</summary>
    public static readonly (string Key, string[] Phrases)[] Commands =
    {
        ("job", new[] { "take the job", "take this job", "start the job", "navigate to the job", "route to the job", "go to the job", "take me to the delivery",
            "accept the job", "yes navigate", "navigate", "yes please" }),
        ("gamegps", new[] { "follow the game route", "follow the game navigation", "go to the game destination", "use the game destination", "use the game route" }),
        ("dismiss", new[] { "not now", "not right now", "not this time", "no not now", "maybe later", "no thanks", "no thank you", "no", "dismiss", "decline",
            "decline the job", "ignore the job", "skip the job", "don't navigate", "cancel", "close", "close the dialog" }),
        ("jobinfo", new[] { "show job info", "show the job info", "show job information", "open job info", "open the job info", "job info" }),
        ("map", new[] { "show the map", "back to the map", "return to the map", "show navigation", "back to navigation", "show the navigation screen", "close job info" }),
        ("cancel", new[] { "cancel the route", "cancel route", "cancel navigation", "stop navigation", "clear the route" }),
        ("resume", new[] { "back to my route", "back to the route", "resume my route", "resume the route", "return to my route" }),
        ("far", new[] { "how far is it", "how far to go", "how far is the destination", "distance remaining" }),
        ("eta", new[] { "when do I arrive", "what time do I arrive", "what is the estimated arrival time", "estimated arrival time" }),
        ("next", new[] { "what is next", "what's next", "what is the next turn", "repeat that", "repeat the instruction", "say that again" }),
        ("limit", new[] { "what is the speed limit", "what's the speed limit", "speed limit" }),
        ("fuel", new[] { "how much fuel do I have", "how much fuel is left", "what is my fuel level", "fuel level", "what is my range" }),
        ("lanes:on", new[] { "turn lane guidance on", "lane guidance on", "show lane guidance" }),
        ("lanes:off", new[] { "turn lane guidance off", "lane guidance off", "hide lane guidance" }),
        ("cruise:on", new[] { "turn cruise assist on", "cruise assist on", "automatic cruise on" }),
        ("cruise:off", new[] { "turn cruise assist off", "cruise assist off", "automatic cruise off" }),
        ("voice:on", new[] { "turn voice guidance on", "voice guidance on", "unmute voice guidance" }),
        ("voice:off", new[] { "turn voice guidance off", "voice guidance off", "mute voice guidance" }),
        ("help", new[] { "help", "what can I say", "voice commands", "list the voice commands" }),
    };

    /// <summary>Is the recognition sure enough to act on? (overall confidence, and the best-heard words of the wake word and of the command)</summary>
    public static bool Accept(RecognitionResult r, string wake, out float commandConfidence) => Accept(r, wake, out commandConfidence, out _);

    /// <summary>As above, also giving the confidence of the best-heard wake word.</summary>
    public static bool Accept(RecognitionResult r, string wake, out float commandConfidence, out float wakeConfidence)
    {
        commandConfidence = 0f; wakeConfidence = 0f;
        if (r.Words.Count == 0) return false;
        // the wake word part: the longest wake variant the recognized text starts with
        int skip = 0;
        foreach (var v in WakeVariants(wake))
            if (r.Text.StartsWith(v + " ", StringComparison.OrdinalIgnoreCase)) skip = Math.Max(skip, v.Split(' ').Length);
        if (skip == 0 || skip >= r.Words.Count) return false;
        for (int i = 0; i < skip; i++) wakeConfidence = Math.Max(wakeConfidence, r.Words[i].Confidence);
        for (int i = skip; i < r.Words.Count; i++) commandConfidence = Math.Max(commandConfidence, r.Words[i].Confidence);
        return r.Confidence >= MinConfidence && wakeConfidence >= MinWakeConfidence && commandConfidence >= MinCommandConfidence;
    }

    /// <summary>What the wake word may sound like to the recognizer ("GPS" is also tried spelled out).</summary>
    static string[] WakeVariants(string wake)
    {
        wake = wake.Trim();
        var list = new List<string> { wake, "hey " + wake, "ok " + wake };
        if (wake.Length is >= 2 and <= 4 && wake.All(char.IsLetter)) { string spelled = string.Join(" ", wake.ToUpperInvariant().ToCharArray()); list.Add(spelled); list.Add("hey " + spelled); }
        return list.Distinct().ToArray();
    }

    public static Grammar BuildGrammar(CultureInfo culture, string wake)
    {
        GrammarBuilder G(params string[] words) { var gb = new GrammarBuilder(new Choices(words)) { Culture = culture }; return gb; }
        var all = new Choices();
        var lead = new GrammarBuilder(G("where is", "find", "find me", "take me to", "navigate to", "go to", "show me"), 0, 1) { Culture = culture };
        var the = new GrammarBuilder(G("the"), 0, 1) { Culture = culture };
        foreach (var (key, words) in PoiWords)
        {
            var gb = new GrammarBuilder { Culture = culture };
            gb.Append(lead); gb.Append(the); gb.Append(G("nearest", "closest")); gb.Append(G(words));
            all.Add(new SemanticResultValue(gb, key));
        }
        foreach (var (key, phrases) in Commands) all.Add(new SemanticResultValue(G(phrases), key));
        var root = new GrammarBuilder { Culture = culture };
        root.Append(G(WakeVariants(wake)));
        root.Append(new SemanticResultKey("cmd", all));
        return new Grammar(root) { Name = "ets2gps-commands" };
    }

    public static RecognizerInfo? FindRecognizer() =>
        SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.Name.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
        ?? SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault();

    /// <summary>Every full sentence the modern recognizer listens for, per command key (it takes plain phrase lists, not a grammar).</summary>
    public static List<(string Key, List<string> Phrases)> PhraseLists(string wake)
    {
        var wakes = WakeVariants(wake);
        var lists = new List<(string, List<string>)>();
        string[] leads = { "", "where is ", "find ", "find me ", "take me to ", "navigate to ", "go to ", "show me " };
        foreach (var (key, words) in PoiWords)
        {
            var l = new List<string>();
            foreach (var w in wakes) foreach (var lead in leads) foreach (var the in new[] { "", "the " }) foreach (var near in new[] { "nearest ", "closest " }) foreach (var word in words)
                l.Add($"{w} {lead}{the}{near}{word}");
            lists.Add((key, l));
        }
        foreach (var (key, phrases) in Commands)
            lists.Add((key, wakes.SelectMany(w => phrases.Select(ph => $"{w} {ph}")).ToList()));
        return lists;
    }

    /// <summary>Starts listening. Returns false (with <see cref="Problem"/> set) when there is no recognizer or microphone.</summary>
    public bool Start(string wake, bool classicOnly = false)
    {
        lock (gate) return StartLocked(wake, classicOnly);
    }

    readonly object gate = new();   // Start runs on a worker thread; Start and Stop never overlap

    bool StartLocked(string wake, bool classicOnly)
    {
        Stop();
        Wake = string.IsNullOrWhiteSpace(wake) ? "GPS" : wake.Trim();
        if (!classicOnly)
        {
            string? why;
            try { why = Task.Run(() => StartModern(Wake, Interlocked.Increment(ref modernRun))).Wait(15000) ? modernProblem : "it took too long to start"; }
            catch (Exception ex) { why = (ex.InnerException ?? ex).Message; }
            if (why == null && modern != null)
            {
                Problem = null; Log("listening with the modern recognizer (" + modern.CurrentLanguage.LanguageTag + ")");
                return true;
            }
            StopModern();
            Log("modern recognizer not available (" + why + "): using the classic one");
        }
        try
        {
            var rec = FindRecognizer();
            if (rec == null) { Problem = "No speech recognizer is installed in Windows (Settings > Time & language > Speech)."; return false; }
            var e = new SpeechRecognitionEngine(rec);
            e.LoadGrammar(BuildGrammar(rec.Culture, Wake));
            // the microphone as calls hear it (noise suppression, automatic gain); the plain default device when that cannot be opened
            string? micWhy;
            var cm = new CommsMic();
            try { var t = Task.Run(cm.StartAsync); micWhy = t.Wait(10000) ? t.Result : "it took too long"; }
            catch (Exception ex) { micWhy = (ex.InnerException ?? ex).Message; }
            if (micWhy == null)
            {
                e.SetInputToAudioStream(cm.Stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(CommsMic.Rate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                mic = cm;
                Log("microphone: " + (cm.Device.Length > 0 ? cm.Device : "default communications device") + " (communications processing)");
                // checks every 10 s that the microphone's audio still arrives
                _ = Task.Run(async () =>
                {
                    double last = -1; bool errorLogged = false;
                    while (mic == cm)
                    {
                        await Task.Delay(10000);
                        if (mic != cm) break;
                        if (cm.Error != null && !errorLogged) { Log("microphone error: " + cm.Error); errorLogged = true; }
                        if (cm.Seconds == last)
                        {
                            // no audio for 10 s: the microphone was reset (a setting changed, the device was unplugged); open it again
                            Log("microphone stopped: opening it again");
                            string w = Wake;
                            _ = Task.Run(() => Start(w, classicOnly: true));
                            break;
                        }
                        last = cm.Seconds;
                    }
                });
            }
            else
            {
                cm.Dispose();
                e.SetInputToDefaultAudioDevice();
                Log("communications microphone not available (" + micWhy + "): using the default device directly");
            }
            e.SpeechRecognized += OnRecognized;
            e.SpeechRecognitionRejected += OnRejected;
            e.RecognizeCompleted += (s, a) => { if (!a.Cancelled && ReferenceEquals(s, engine)) Log($"listening stopped: {(a.Error != null ? a.Error.GetType().Name + ": " + a.Error.Message : a.InputStreamEnded ? "the audio ended" : "done")}"); };
            e.RecognizeAsync(RecognizeMode.Multiple);
            engine = e; Problem = null;
            Log("listening with the classic recognizer");
            return true;
        }
        catch (Exception ex) { Problem = "Voice control could not start: " + ex.Message; Stop(); return false; }
    }

    string? modernProblem;

    async Task StartModern(string wake, int run)
    {
        modernProblem = null;
        var r = new Windows.Media.SpeechRecognition.SpeechRecognizer();
        foreach (var (key, phrases) in PhraseLists(wake))
            r.Constraints.Add(new Windows.Media.SpeechRecognition.SpeechRecognitionListConstraint(phrases, key));
        var comp = await r.CompileConstraintsAsync();
        if (comp.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success) { r.Dispose(); modernProblem = "commands: " + comp.Status; return; }
        r.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromDays(1);
        r.ContinuousRecognitionSession.ResultGenerated += (s, e) => OnModernResult(e.Result);
        r.ContinuousRecognitionSession.Completed += (s, e) => OnModernCompleted(run, e.Status);
        modern = r;
        try { await r.ContinuousRecognitionSession.StartAsync(); }
        catch (Exception ex)
        {
            modern = null; r.Dispose();
            // 0x80045509: Windows' "Online speech recognition" privacy switch is off
            modernProblem = (uint)ex.HResult == 0x80045509 ? "the speech privacy setting is off" : ex.Message;
        }
    }

    void OnModernResult(Windows.Media.SpeechRecognition.SpeechRecognitionResult r)
    {
        try
        {
            if (r.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success || r.Text.Length == 0) { Log("SPEECH NOT UNDERSTOOD"); return; }
            bool m = muted();
            // "low" is accepted too: a correct command through a wireless microphone often scores low, and only a whole command phrase (wake word
            // included) can match at all, so ordinary speech does not get this far
            bool ok = r.Confidence != Windows.Media.SpeechRecognition.SpeechRecognitionConfidence.Rejected && r.RawConfidence >= MinModernConfidence;
            Log($"{(m ? "IGNORED (app speaking)" : ok ? "ACCEPTED" : "TOO UNSURE")}: \"{r.Text}\" {r.Confidence.ToString().ToLowerInvariant()} ({r.RawConfidence:0.00})");
            if (m || !ok || r.Constraint?.Tag is not { Length: > 0 } key) return;
            Command?.Invoke(key, r.Text, (float)r.RawConfidence);
        }
        catch { /* a bad result must never stop the listener */ }
    }

    void OnModernCompleted(int run, Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus status)
    {
        // the session ends after errors, long silence or a microphone change: start it again while voice control is on
        if (run != Volatile.Read(ref modernRun) || modern is not { } r) return;
        Log("listening stopped (" + status + "): restarting");
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            if (run != Volatile.Read(ref modernRun)) return;
            try { await r.ContinuousRecognitionSession.StartAsync(); }
            catch (Exception ex) { Log("could not restart listening: " + ex.Message); Problem = "Voice control stopped: " + ex.Message; }
        });
    }

    void StopModern()
    {
        Interlocked.Increment(ref modernRun);
        var r = modern; modern = null;
        if (r == null) return;
        try { _ = r.ContinuousRecognitionSession.CancelAsync(); } catch { }
        try { r.Dispose(); } catch { }
    }

    void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        try
        {
            bool m = muted(), ok = Accept(e.Result, Wake, out float cc, out float wc);
            Log($"{(m ? "IGNORED (app speaking)" : ok ? "ACCEPTED" : "TOO UNSURE")}: \"{e.Result.Text}\" overall {e.Result.Confidence:0.00}, wake {wc:0.00}, command {cc:0.00}");
            if (m || !ok) return;
            if (e.Result.Semantics.ContainsKey("cmd") && e.Result.Semantics["cmd"].Value is string key) Command?.Invoke(key, e.Result.Text, e.Result.Confidence);
        }
        catch { /* a bad result must never stop the listener */ }
    }

    void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        // speech with no command phrase in it (talk, the radio) is not logged; only a near-miss on a command is
        try { if (e.Result.Text.Length > 0 && !muted()) Log($"NOT A COMMAND: \"{e.Result.Text}\" overall {e.Result.Confidence:0.00}"); }
        catch { }
    }

    /// <summary>What was heard and why it was or was not acted on, in voice_control.log next to the settings (no audio is kept).</summary>
    static void Log(string line)
    {
        try
        {
            string p = Path.Combine(AppSettings.Dir, "voice_control.log");
            if (File.Exists(p) && new FileInfo(p).Length > 200_000) File.Delete(p);
            File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>Test: handles the text as if it had been spoken.</summary>
    public void Emulate(string text) { if (engine == null) return; engine.RecognizeAsyncCancel(); Thread.Sleep(500); engine.EmulateRecognize(text); }

    public void Stop()
    {
        lock (gate) StopLocked();
    }

    void StopLocked()
    {
        StopModern();
        var e = engine; engine = null;
        var cm = mic; mic = null;
        cm?.Dispose();   // ends the audio stream first, so the recognizer is not left waiting for audio
        if (e == null) return;
        try { e.SpeechRecognized -= OnRecognized; e.SpeechRecognitionRejected -= OnRejected; e.RecognizeAsyncCancel(); } catch { /* already stopped */ }
        try { e.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose() => Stop();
}
