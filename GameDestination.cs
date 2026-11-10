using System.Windows;

namespace Ets2Gps;

/// <summary>
/// Following the game's own GPS when there is no job (driving to a pickup, or a destination set on the game's map): the destination is read from the
/// newest save, so it is seen after the game's next autosave or quick save. See <see cref="GameGps"/>.
/// </summary>
public sealed partial class MainWindow
{
    const float GameGpsMaxAgeMin = 15f;   // older saves are not offered (the game has probably moved on since)
    DateTime gameGpsSeenUtc = DateTime.MinValue;   // the newest save already looked at
    string gameGpsOffered = "";                    // the destination already offered (not asked again until it changes)
    long gameGpsCheckMs = -100000;
    bool gameGpsBusy;
    float gameNavM;                   // the game's own route length last seen (game metres; 0 = no route)
    long quickSaveWantMs = -1;        // the game's route got a new destination at this time and the app has not seen it yet
    long quickSaveDoneMs = -100000;
    DateTime quickSaveWantUtc;

    /// <summary>
    /// A new destination on the game's GPS while no job has started (a job with a pickup elsewhere, or a point set on the game's map) only reaches the save at
    /// the game's next autosave, minutes later. Like the TruckSim GPS server, press the player's quick-save key once (game in front, not paused) so it is
    /// written now; the save is then read as usual.
    /// </summary>
    void CheckQuickSave(TruckState t, long now)
    {
        float navM = (t.Detail?.NavDistanceKm ?? 0f) * 1000f;
        bool noJob = t.DestCityId.Length == 0 && t.Selected == null;   // a selected job is routed from the telemetry: no save needed
        // a new destination: the route appears, or its length jumps (more than 1 km and 10 %) rather than shrinking as you drive
        if (noJob && navM > 0f && (gameNavM <= 0f || (MathF.Abs(navM - gameNavM) > 1000f && MathF.Abs(navM - gameNavM) > 0.1f * gameNavM)))
        { quickSaveWantMs = now; quickSaveWantUtc = DateTime.UtcNow; }
        gameNavM = navM;
        if (!noJob || navM <= 0f || !cfg.FollowGameGps || !cfg.AutoQuickSave) { quickSaveWantMs = -1; return; }
        if (quickSaveWantMs < 0 || now - quickSaveWantMs < 2000 || now - quickSaveDoneMs < 60000 || t.Paused || !CruiseAssist.GameInFront()) return;
        if (GameGps.NewestSave() is { } s && s.Utc > quickSaveWantUtc) { quickSaveWantMs = -1; return; }   // the game has saved since: nothing to do
        if (GameControls.QuickSaveKey() is not { } key) { quickSaveWantMs = -1; return; }   // no keyboard key for quick save in this profile
        quickSaveWantMs = -1; quickSaveDoneMs = now;
        CruiseAssist.Tap(key, "quicksave");
        Toast($"Quick save ({key.Name}) so the app can read the game's new destination");
    }

    /// <summary>Looks for a newer save every few seconds; force (voice "follow the game route") reads the newest save now and navigates without asking.</summary>
    void CheckGameGps(bool force = false)
    {
        long now = clock.ElapsedMilliseconds;
        if (map == null || gameGpsBusy || (!force && (!cfg.FollowGameGps || now - gameGpsCheckMs < 3000))) return;
        gameGpsCheckMs = now;
        var t = truck;
        if (!force && t != null) CheckQuickSave(t, now);
        if (!force && (t == null || t.DestCityId.Length > 0 || t.Selected != null)) return;   // on a job, or one selected: the job / pickup route is used
        gameGpsBusy = true;
        Task.Run(() =>
        {
            GameGpsState? state = null; string? problem = null;
            try
            {
                var newest = GameGps.NewestSave();
                if (newest is { } s && (force || s.Utc > gameGpsSeenUtc) && DateTime.UtcNow - s.Utc > TimeSpan.FromSeconds(1.5))   // let the game finish writing
                    state = GameGps.Read(s.File, s.Utc);
                else if (force) problem = newest == null ? "I could not find the game's saves." : "The game is still writing its save. Try again in a moment.";
            }
            catch (Exception e) { problem = "I could not read the game's save: " + e.Message; }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                gameGpsBusy = false;
                if (state != null) gameGpsSeenUtc = state.SavedUtc;
                if (force) FollowGameGpsNow(state, problem);
                else if (state != null) OfferGameGps(state);
            }));
        });
    }

    void OfferGameGps(GameGpsState s)
    {
        var t = truck;
        if (s.Ahead.Count == 0 || s.OnJob || t == null || t.DestCityId.Length > 0) return;
        if ((DateTime.UtcNow - s.SavedUtc).TotalMinutes > GameGpsMaxAgeMin) return;
        var (x, z) = s.Ahead[^1];
        string key = $"{Math.Round(x / 20)}/{Math.Round(z / 20)}";
        if (key == gameGpsOffered) return;
        gameGpsOffered = key;
        if (M.Hypot(motion.X - x, motion.Z - z) < 150f) return;                 // already there
        if (hasDest && M.Hypot(destX - x, destZ - z) < 80f) return;             // already going there
        string name = DescribePoint(x, z);
        string say = vctl?.Active == true ? $"\n\nSay \"{vctl.Wake}, take the job\" or \"{vctl.Wake}, not now\"." : "";
        askAccept = () => RouteToGameGps(s, name);
        try
        {
            Dlg.Message(this, "Game GPS", $"The game's GPS is set to {name}, {GameDist(M.Hypot(motion.X - x, motion.Z - z))} away" +
                (s.Ahead.Count > 1 ? $" (via {s.Ahead.Count - 1} waypoint{(s.Ahead.Count > 2 ? "s" : "")})" : "") + ". Navigate there?" + say,
                ("Not now", null, false), ("Navigate", () => RouteToGameGps(s, name), true));
        }
        finally { askAccept = null; }
    }

    void FollowGameGpsNow(GameGpsState? s, string? problem)
    {
        if (s == null) { voice.SayAlways(problem ?? "The latest save is already in use."); return; }
        if (s.Ahead.Count == 0) { voice.SayAlways("The game's GPS has no destination in the latest save. Quick save in the game, then ask again."); return; }
        var (x, z) = s.Ahead[^1];
        gameGpsOffered = $"{Math.Round(x / 20)}/{Math.Round(z / 20)}";
        RouteToGameGps(s, DescribePoint(x, z));
    }

    /// <summary>Routes to the game's destination, through its waypoints in order.</summary>
    void RouteToGameGps(GameGpsState s, string name)
    {
        var (x, z) = s.Ahead[^1];
        waypoints = s.Ahead.Take(s.Ahead.Count - 1).Select((p, i) => new StopPoint { X = p.X, Z = p.Z, Name = "Game waypoint " + (i + 1) }).ToList();
        ComputeRoute(x, z, name, announce: true, keepStops: true);
    }

    /// <summary>A name for a point: the company or ferry terminal there, else the nearest town.</summary>
    string DescribePoint(float x, float z)
    {
        var m = map!;
        var co = m.Companies.Where(c => M.Hypot(c.X - x, c.Z - z) < 300f).OrderBy(c => M.Hypot(c.X - x, c.Z - z)).FirstOrDefault();
        var city = m.Cities.OrderBy(c => M.Hypot(c.X - x, c.Z - z)).FirstOrDefault();
        string cityName = co != null ? m.FindCity(co.CityId)?.Name ?? city?.Name ?? "" : city?.Name ?? "";
        var port = m.Pois.Where(p => (p.Type == Poi.Ferry || p.Type == Poi.Train) && M.Hypot(p.X - x, p.Z - z) < 250f).OrderBy(p => M.Hypot(p.X - x, p.Z - z)).FirstOrDefault();
        // a port often has company yards next to it: name whichever of the two the point is nearer to
        if (port != null && (co == null || M.Hypot(port.X - x, port.Z - z) < M.Hypot(co.X - x, co.Z - z)))
            return (port.Type == Poi.Train ? "the car train terminal" : "the ferry terminal") + (cityName.Length > 0 ? ", " + cityName : "");
        if (co != null) return PlaceName(m.CompanyNames.TryGetValue(co.Id, out var shown) ? shown : co.Id, cityName);
        return city != null && M.Hypot(city.X - x, city.Z - z) < 4000f ? "a point near " + city.Name : "the game's destination";
    }
}
