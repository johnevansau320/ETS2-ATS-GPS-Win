using System.Windows;

namespace Ets2Gps;

public static class Program
{
    // Ets2Gps.exe [--topmost] [--config file.json]   (--topmost forces "stay on top" for this run)
    [STAThread]
    public static int Main(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--config") AppSettings.PathOverride = args[i + 1];
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--game") Games.Current = Games.ById(args[i + 1]);   // test: ets2 or ats
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--build-map") return MapBuilder.MapBuilderCli.Run(args[i + 1]);   // test: build a map file and exit
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--check-map")   // test: load a map file and write a summary next to it
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string res;
                try
                {
                    var mm = MapData.Load(args[i + 1]);
                    int lifted = 0; foreach (var b in mm.PtLift) if (b > 0) lifted++;
                    res = $"v{mm.Version} edges {mm.EdgeCount} lifted points {lifted} of {mm.PtX.Length} liftedEdges {mm.EHasLift.Count(x => x)} in {sw.ElapsedMilliseconds} ms; borders " + string.Join(" ", mm.Borders.Where(x => x.Kind == 1 && (x.Cc == "de" || x.Cc == "nl")).Take(8).Select(x => $"{x.Cc}@{x.X:0},{x.Z:0}"));
                }
                catch (Exception e) { res = "FAILED " + e; }
                File.WriteAllText(args[i + 1] + ".check.txt", res);
                return 0;
            }
        if (args.Contains("--ptt-capture-test"))   // test: wait for a key or controller button and write its push-to-talk binding to ptt_test.txt next to --config
        {
            string dir = AppSettings.PathOverride is { } po ? Path.GetDirectoryName(Path.GetFullPath(po))! : AppSettings.Dir;
            if (args.Contains("list")) { File.WriteAllText(Path.Combine(dir, "ptt_test.txt"), PushToTalk.ListControllers()); return 0; }
            var b = PushToTalk.CaptureAsync(8000).GetAwaiter().GetResult();
            using var p = new PushToTalk { Binding = b ?? "" };
            var seen = new List<string>(); p.Changed += d => { lock (seen) seen.Add(d ? "down" : "up"); };
            Thread.Sleep(4000);
            File.WriteAllText(Path.Combine(dir, "ptt_test.txt"), $"{b} = {PushToTalk.Describe(b ?? "")}; then {string.Join(",", seen)}");
            return 0;
        }
        if (args.Contains("--voice-start-test"))   // test: start voice control as the app does and write which recognizer is listening to voice_test.txt
        {
            using var vc = new VoiceControl(() => false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool classic = args.Contains("classic");
            bool ok = vc.Start("Computer", classic, args.Contains("ptt") ? () => true : null);
            File.WriteAllText(Path.Combine(AppSettings.Dir, "voice_test.txt"), $"started={ok} engine={vc.EngineName} in {sw.ElapsedMilliseconds} ms {vc.Problem} phrases={VoiceControl.PhraseLists("Computer").Sum(l => l.Phrases.Count)}");
            Thread.Sleep(classic ? 12000 : 2000);   // the classic one: long enough for its microphone level reports in the log
            return 0;
        }
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--voice-test")   // test: feed a typed phrase to voice control (as if spoken) and write what came out to voice_test.txt
            {                                // --ptt held|released|idle: push to talk with the button held, let go a moment ago, or not pressed
                string dir = AppSettings.PathOverride is { } po ? Path.GetDirectoryName(Path.GetFullPath(po))! : AppSettings.Dir;
                VoiceControl.LogDir = dir;
                int pi = Array.IndexOf(args, "--ptt"); string? ptt = pi >= 0 && pi + 1 < args.Length ? args[pi + 1] : null;
                long since = Environment.TickCount64;
                Func<bool>? gate = ptt switch { "held" => () => true, "released" => () => Environment.TickCount64 - since < 1500 + PushToTalk.GraceMs, "idle" => () => false, _ => null };
                var heard = new List<string>();
                using var vc = new VoiceControl(() => false);
                vc.Command += (k, t, c) => { lock (heard) heard.Add($"{k} \"{t}\" {c:0.00}"); };
                bool ok = vc.Start("Computer", classicOnly: true, gate);
                since = Environment.TickCount64;
                vc.Emulate(args[i + 1]);
                Thread.Sleep(3000);
                File.WriteAllText(Path.Combine(dir, "voice_test.txt"), $"started={ok} {vc.Problem}" + Environment.NewLine + string.Join(Environment.NewLine, heard));
                return 0;
            }
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--check-game-gps")   // test: read the game's GPS from the newest save and write a summary to the file
            {
                string res;
                try
                {
                    var s = GameGps.NewestSave();
                    var st = s is { } n ? GameGps.Read(n.File, n.Utc) : null;
                    res = s == null ? "no save found" : $"{s.Value.File} {s.Value.Utc:u} onJob={st!.OnJob} ahead=" + string.Join(" ", st.Ahead.Select(p => $"{p.X:0},{p.Z:0}"));
                }
                catch (Exception e) { res = "FAILED " + e; }
                File.WriteAllText(args[i + 1], res);
                return 0;
            }
        var cfg = AppSettings.Load();
        if (!args.Contains("--game")) Games.Current = Games.ById(cfg.Game);
        Units.Choice = cfg.UnitSystem;
        if (args.Contains("--topmost")) cfg.Topmost = true;
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.ToString(), "ETS2-ATS GPS - unexpected error");
            e.Handled = true;
        };
        return app.Run(new MainWindow(cfg));
    }
}
