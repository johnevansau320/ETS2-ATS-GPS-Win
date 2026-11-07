using System.Globalization;
using System.Windows;

namespace Ets2Gps;

/// <summary>Voice control of the main window: what each spoken command does (the listening itself is in <see cref="VoiceControl"/>).</summary>
public sealed partial class MainWindow
{
    VoiceControl? vctl;
    long lastVoiceCmdMs; string lastVoiceKey = "";
    Action? askAccept;   // a "navigate there?" question is on screen (a new job, or the game's GPS destination): what accepting it does

    /// <summary>Starts or stops listening to match the setting.</summary>
    void ApplyVoiceControl(bool announce)
    {
        if (!cfg.VoiceControl) { vctl?.Stop(); return; }
        if (vctl == null) { vctl = new VoiceControl(() => voice.Busy); vctl.Command += OnVoiceCommand; }
        var v = vctl; string wake = cfg.VoiceWake; bool classic = cfg.VoiceRecognizer != "modern";
        // starting the recognizer takes a few seconds (it compiles the command list): off the UI thread
        Task.Run(() =>
        {
            bool ok = v.Start(wake, classic);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!cfg.VoiceControl) { v.Stop(); return; }   // switched off meanwhile
                if (ok) { if (announce) Toast($"Voice control on - say \"{v.Wake}\", then a command, for example \"{v.Wake}, help\""); }
                else Toast(v.Problem ?? "Voice control could not start");
            }));
        });
    }

    void OnVoiceCommand(string key, string heard, float confidence) => Dispatcher.BeginInvoke(new Action(() => RunVoiceCommand(key, heard)));

    static string SpokenPlace(int type) => type switch { Poi.Parking => "rest area", Poi.Fuel => "fuel station", Poi.Service => "service centre", Poi.Weigh => "weigh station",
        Poi.Garage => "garage", Poi.Dealer => "truck dealer", Poi.Ferry => "ferry terminal", _ => Poi.Label(type).ToLowerInvariant() };

    static string Words(string text) => text.Replace(" km", " kilometres").Replace(" m ", " metres ").Replace(" m,", " metres,").Replace(" m.", " metres.")
        .Replace(" mi", " miles").Replace(" ft", " feet");

    static string SpokenDuration(float minutes)
    {
        int m = Math.Max(0, (int)MathF.Round(minutes));
        if (m >= 1440) return $"{m / 1440} days {(m % 1440) / 60} hours";
        if (m >= 60) return $"{m / 60} hour{(m / 60 == 1 ? "" : "s")} {m % 60} minutes";
        return $"{m} minutes";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    const int SW_SHOWNOACTIVATE = 4;

    /// <summary>Brings the minimized window back at its last size and place without activating it, so the game keeps the keyboard.</summary>
    void RestoreWindow()
    {
        if (WindowState != WindowState.Minimized) return;
        ShowWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, SW_SHOWNOACTIVATE);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;   // the call did not take: restore the usual way
    }

    void RunVoiceCommand(string key, string heard)
    {
        long now = clock.ElapsedMilliseconds;
        if (key == lastVoiceKey && now - lastVoiceCmdMs < 2500) return;   // the same command twice in a row is an echo
        lastVoiceKey = key; lastVoiceCmdMs = now;
        Toast("Heard: " + heard);
        void Reply(string text) => voice.SayAlways(text);   // answers are spoken even when the route prompts are switched off
        var t = truck;
        bool inGame = t != null && t.Connected;

        if (key.StartsWith("poi:"))
        {
            int type = int.Parse(key[4..], CultureInfo.InvariantCulture);
            if (!inGame || map == null) { Reply("The game is not running yet."); return; }
            var hit = NearestPois(type, 1).FirstOrDefault();
            if (hit == null) { Reply($"I could not find a {SpokenPlace(type)} nearby."); return; }
            Reply($"The nearest {SpokenPlace(type)} is {Words(GameDist(hit.Dist))} {Direction(hit)}. Navigating there.");
            ComputeRoute(hit.Poi.X, hit.Poi.Z, Poi.Label(type), announce: false, isPoi: true);
            return;
        }
        switch (key)
        {
            case "job":
                // answers the question on screen if there is one (the route announcement is spoken by the navigation itself)
                if (askAccept is { } accept && Dlg.CloseOpen(accept)) break;
                if (!inGame || map == null) { Reply("The game is not running yet."); return; }
                if (t!.DestCityId.Length == 0 && t.DestCity.Length == 0) { Reply("The game reports no active job."); return; }
                NavigateToJob(t);
                break;
            case "gamegps":
                if (!inGame || map == null) { Reply("The game is not running yet."); return; }
                CheckGameGps(force: true);
                break;
            case "dismiss":
                if (askAccept != null) { Dlg.CloseOpen(); Reply("OK, not navigating there."); }
                else if (Dlg.CloseOpen()) Reply("Closed.");
                else Reply("There is nothing to close.");
                break;
            case "jobinfo":
                if (!tripOpen) ToggleTrip();
                Reply("Job info.");
                break;
            case "map":
                if (tripOpen) ToggleTrip();
                if (WindowState == WindowState.Minimized) RestoreWindow();
                Reply("Map.");
                break;
            case "cancel":
                if (askAccept != null) { Dlg.CloseOpen(); Reply("OK, not navigating there."); return; }
                if (!hasDest) { Reply("There is no route to cancel."); return; }
                CancelRoute(); Reply("Route cancelled.");
                break;
            case "resume":
                if (resume == null) { Reply("There is no route to go back to."); return; }
                { var r = resume.Value; resume = null; nav?.Clear(); Reply("Back on route to " + r.Name); waypoints = r.Stops.ToList(); SyncStops(); ComputeRoute(r.X, r.Z, r.Name, keepStops: true); }
                break;
            case "far":
                if (!hasDest || routeRemainingM < 0 || nav == null) { Reply("There is no active route."); return; }
                Reply(nav.SpokenDistance(routeRemainingM) + " to go.");
                break;
            case "eta":
                if (!hasDest || routeEtaGameMin < 0) { Reply("There is no active route."); return; }
                { string clk = EtaClock(routeEtaGameMin); Reply((clk.Length > 0 ? "You arrive " + clk + ", in " : "You arrive in ") + SpokenDuration(routeEtaGameMin) + "."); }
                break;
            case "next":
                Reply(lastNextText.Length > 0 ? Words(lastNextText) : "There is no instruction yet.");
                break;
            case "limit":
                Reply(inGame && t!.SpeedLimitKmh > 0 ? $"The speed limit is {Math.Round(Units.Speed(t.SpeedLimitKmh))}" + (Units.Imperial ? " miles per hour." : ".") : "I have no speed limit for this road.");
                break;
            case "fuel":
                if (!inGame || t!.FuelFraction < 0) { Reply("I have no fuel reading."); return; }
                { int pct = (int)MathF.Round(t.FuelFraction * 100f); float range = t.Detail?.RangeKm ?? 0f;
                  Reply($"Fuel is at {pct} percent" + (range > 1f ? $", range about {(int)MathF.Round(Units.Long(range))} " + (Units.Imperial ? "miles." : "kilometres.") : ".")); }
                break;
            case "lanes:on": case "lanes:off":
                { bool on = key.EndsWith("on"); cfg.ShowLanes = on; cfg.Save(); if (!on) SetLaneUi(null); Reply("Lane guidance " + (on ? "on." : "off.")); }
                break;
            case "cruise:on": case "cruise:off":
                { bool on = key.EndsWith("on"); SetCruiseFollows(on, false); Reply("Cruise assist " + (on ? "on." : "off.")); }
                break;
            case "voice:on":
                voice.Enabled = true; cfg.VoiceOn = true; cfg.Save(); UpdateVoiceButton(); Reply("Voice guidance on.");
                break;
            case "voice:off":
                Reply("Voice guidance off."); voice.Enabled = false; cfg.VoiceOn = false; cfg.Save(); UpdateVoiceButton();
                break;
            case "window:min":
                WindowState = WindowState.Minimized;
                Reply("Minimized.");
                break;
            case "window:restore":
                RestoreWindow();
                Reply("Restored.");
                break;
            case "help":
                Reply($"Say {vctl?.Wake ?? "GPS"}, then: nearest fuel station, rest area, service centre, weigh station, garage or truck dealer. Take the job, or not now when the new job question is on screen. Follow the game route, to go where the game's GPS is set. Show job info, or show the map, or back to navigation. Cancel the route, or back to my route. How far, when do I arrive, what is next, speed limit, fuel level. Lane guidance, cruise assist or voice guidance on or off. Minimize or restore the window.");
                Toast("Voice commands: nearest fuel station / rest area / service centre / weigh station / garage / truck dealer · take the job / not now · follow the game route · show job info · show the map · cancel the route · back to my route · how far · when do I arrive · what is next · speed limit · fuel level · lane guidance, cruise assist, voice guidance on/off · minimize / restore the window");
                break;
        }
    }
}
