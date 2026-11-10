using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Ets2Gps.MapBuilder;
using System.Windows.Threading;

namespace Ets2Gps;

/// <summary>Routing, guidance, waypoints, saved trips, POIs and alerts (logic part of the main window).</summary>
public sealed partial class MainWindow
{
    // route state
    bool hasDest, destIsPoi, routing;
    float destX, destZ; string destName = "";
    (float X, float Z, string Name, List<StopPoint> Stops)? resume;
    List<StopPoint> waypoints = new();
    float afterM, afterS;
    string startLabel = "Start";
    float routeRemainingM = -1f, routeEtaGameMin = -1f, paceRatio = 1f;
    long lastPaceMs, lastReroute;
    const float NearDestM = 120f;   // map metres: this close to the destination the app stops recalculating
    const float AtFlagM = 20f;      // map metres: this close to the drop-off flag counts as arrived
    (float X, float Z)? jobFlag;    // the drop-off spot in the company yard (where the game shows its delivery flag), when the map knows it
    bool offAnnounced, destIsJob, endHinted; long stillSinceMs;
    string lastNextText = "";
    // trip stats and alerts
    float tripBase, tripMaxKmh, tripFuelUsed, tripLastFuel = -1f; long tripDriveMs, lastTripMs;
    float descStart = -1f, climbStart = -1f; long lastDescWarn, lastClimbWarn, lastPlaceCheck;
    bool fuelAlerted, rest30, rest10; string lastJobKey = "";
    BuiltInServer? server;

    // ------------------------------------------------------------------------------ map --

    /// <summary>The other game is running: drop the route, switch the units, settings and map to that game, and load (or build) its map.</summary>
    void SwitchGame(GameInfo g)
    {
        if (switchingGame) return;
        switchingGame = true;
        CancelRoute();
        Games.Current = g; cfg.Game = g.Id; cfg.Save();
        Title = AppTitle;
        distScale = cfg.RealDistances ? 1f : cfg.GameDistScale;
        map = null; router = null; nav = null; mapView.Map = null; mapView.Assets = null;
        speedUnit.Text = Units.SpeedUnit; StyleLimitSign();
        Toast(g.Name + " is running: switching to its map");
        _ = LoadMapAsync().ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => switchingGame = false)));
    }
    bool switchingGame;

    async Task LoadMapAsync()
    {
        try
        {
            var progress = new Progress<string>(t => SetBannerMessage(t));
            var m = await MapService.LoadAsync(cfg, progress);
            var rt = new Router(m) { Mode = routeMode, AvoidFerries = cfg.AvoidFerries };
            rt.SetAvoid(cfg.GameAvoid.Select(a => (a.X, a.Z)));
            map = m; router = rt; nav = new Navigator(m, rt) { DistScale = distScale };
            mapView.Map = m;
            mapView.Assets = await Task.Run(() => MapAssets.Load(MapService.AssetsPath));
            SetBannerMessage(null); ClearManeuver();
            if (autoGoto != null || autoTrip || autoPlan != null || ArgAfter("--open") != null || autoTripsExport != null || autoTripsImport != null) _ = AutoStartAsync();
            Toast($"Map loaded: {m.Cities.Count} cities, {m.EdgeCount:N0} road segments" + (m.LaneN.Length > 0 ? ", with lane data" : " - no lane data: rebuild the map with the updated companion"));
        }
        catch (Exception e)
        {
            SetBannerMessage("Could not load the map: " + e.Message);
        }
    }

    async Task AutoStartAsync()
    {
        if (autoPlan != null) { await Task.Delay(500); AutoPlan(autoPlan, autoPlanSave); }
        if (autoTripsExport != null || autoTripsImport != null) { await Task.Delay(500); AutoTripFiles(autoTripsExport, autoTripsImport); }
        for (int i = 0; i < 100 && (truck == null || !truck.Connected); i++) await Task.Delay(100);
        if (autoGoto != null && map != null)
        {
            var c = map.Cities.FirstOrDefault(x => x.Key == City.Normalize(autoGoto));
            if (c != null) ComputeRoute(c.X, c.Z, c.Name, announce: true);
        }
        if (autoTrip) { await Task.Delay(1500); if (!tripOpen) ToggleTrip(); }
        // test hook: --open search | stops | trips | plan | poi | settings | job opens that screen (job: routes to the job) (used to take pictures for the manual)
        switch (ArgAfter("--open"))
        {
            case "search": await Task.Delay(2500); ShowSearch(false); break;
            case "stops": await Task.Delay(2500); ShowStops(); break;
            case "trips": await Task.Delay(2500); ShowTrips(); break;
            case "plan": await Task.Delay(2500); ShowPlanner(); break;
            case "poi": await Task.Delay(2500); ShowPoiPicker(); break;
            case "settings": await Task.Delay(2500); ShowSettings(); break;
            case "job": await Task.Delay(2500); Dlg.CloseOpen(); RouteToJob(); break;
        }
    }

    // ------------------------------------------------------------------------- telemetry --

    void OnTruck(TruckState s)
    {
        if (s.Connected && Games.FromTelemetryName(s.GameName) is { } g && g != Games.Current) { SwitchGame(g); return; }
        truck = s;
        long now = clock.ElapsedMilliseconds;
        motion.Update(s, now);
        mapView.HasFix = s.Connected && (s.X != 0 || s.Z != 0);
        mapView.SpeedKmh = s.SpeedKmh;
        cruise.Update(s, now);
        statusText.Text = !s.Connected ? "Telemetry connected - waiting for the game" : s.Paused ? "Game paused" : place;
        speedText.Text = Math.Round(Math.Max(0, Units.Speed(s.SpeedKmh))).ToString(CultureInfo.InvariantCulture);
        speedUnit.Text = Units.SpeedUnit;
        if (s.SpeedLimitKmh > 0) { limitText.Text = Math.Round(Units.Speed(s.SpeedLimitKmh)).ToString(CultureInfo.InvariantCulture); limitBox.Visibility = Visibility.Visible; } else limitBox.Visibility = Visibility.Hidden;
        TrackTrip(s, now);
        UpdateBannerJob();
        if (s.Connected) { UpdateAltitude(s); CheckHill(s, now); CheckNewJob(s); CheckJobEvents(s); CheckAlerts(s); UpdatePlace(now); NavTick(); CheckGameGps(); }
    }

    // ------------------------------------------------------------------------ guidance --

    void NavTick()
    {
        var n = nav;
        if (n == null || !n.Active) return;
        var st = n.Update(motion.X, motion.Z, motion.SpeedMps);
        mapView.RouteIdx = n.CurrentIndex; mapView.NextManeuver = st.Next;
        if (st.Speech != null) voice.Say(st.Speech);
        if (st.Arrived && waypoints.Count > 0)
        {
            var wp = waypoints[0]; waypoints.RemoveAt(0);
            n.Clear(); SyncStops();
            voice.Say("Waypoint reached. Continuing to " + (waypoints.Count > 0 ? waypoints[0].Name : destName));
            Toast("Waypoint reached: " + wp.Name);
            ComputeRoute(destX, destZ, destName, reroute: true);
            return;
        }
        if (st.Arrived && resume != null)
        {
            var r = resume.Value; resume = null; n.Clear();
            voice.Say("Back on route to " + r.Name);
            waypoints = r.Stops.ToList(); SyncStops();
            ComputeRoute(r.X, r.Z, r.Name, keepStops: true);
            return;
        }
        if (st.Arrived && destIsJob && HoldAtRouteEnd(n))
            return;
        if (st.Arrived)
        {
            if (destIsJob) voice.Say(jobFlag != null ? "You have arrived at the drop off point" : "You have arrived at " + destName);
            mapView.FlagOn = false;
            infoText.Text = "Arrived at " + destName;
            SetBannerMessage("You have arrived"); ClearManeuver();
            n.Clear(); mapView.RoutePts = Array.Empty<float>(); mapView.RouteEdgePt = Array.Empty<int>(); mapView.NextManeuver = null;
            hasDest = false; SetLaneUi(null);
            return;
        }
        var next = st.Next;
        lastNextText = next != null ? n.Instruction(next) + " in " + n.ShortDistance(st.DistToNextM) + (next.Towards != null ? " towards " + next.Towards : "") : "";
        float totM = st.RemainingM + afterM, totS = st.RemainingS + afterS;
        routeRemainingM = totM;
        if (next != null)
        {
            string what = n.Instruction(next);
            SetManeuver(next, n.ShortDistance(st.DistToNextM), char.ToUpperInvariant(what[0]) + what[1..] + (next.Towards != null ? " towards " + next.Towards : ""), st.Following);
        }
        if (!tripOpen && st.Lanes != null && st.LanesDistM * cfg.GameDistScale < LaneShowM) SetLaneUi(st.Lanes, n.ShortDistance(st.LanesDistM)); else SetLaneUi(null);
        UpdatePace(n);
        float realMin = totS / paceRatio / 60f, gameMin = realMin * TimeScale;
        routeEtaGameMin = gameMin;
        string clockStr = EtaClock(gameMin);
        string label = waypoints.Count > 0 ? $"{destName} (via {waypoints.Count} stop{(waypoints.Count > 1 ? "s" : "")}, next: {waypoints[0].Name})" : destName;
        SetInfo(label, n.ShortDistance(totM) + " · Real " + FormatDuration(realMin) + " · Sim " + FormatDuration(gameMin) + (clockStr.Length > 0 ? " · Arrive " + clockStr : ""));
        if (st.OnRoute) offAnnounced = false;   // said again only after the truck has really been back on a route
        if (st.OffRoute) RerouteIfNeeded();
    }

    /// <summary>
    /// The mapped road network often ends well short of a company's gate (the game's own route goes on to the gate). While the game still reports a long way
    /// to go for the job, keep the route and the flag on the map and tell the driver to carry on, instead of declaring the arrival at the end of the mapped route.
    /// </summary>
    bool HoldAtRouteEnd(Navigator n)
    {
        var t = truck;
        if (t == null || t.DestCityId.Length == 0) return false;   // job delivered or cancelled: arrived
        if (jobFlag is { } f)
        {
            // the drop-off spot is known: carry on to the flag (a straight line across the yard) and arrive there
            float d = M.Hypot(motion.X - f.X, motion.Z - f.Z);
            if (d < AtFlagM) return false;
            if (!endHinted) { endHinted = true; voice.Say("The mapped route ends here. Follow the flag to the drop off point."); }
            SetBannerMessage("Mapped route ends here - follow the flag");
            ClearManeuver(); SetLaneUi(null);
            SetInfo(destName, "Drop-off point: " + n.ShortDistance(d) + " in a straight line");
            return true;
        }
        float navKm = t.Detail?.NavDistanceKm ?? 0f;
        float gameM = navKm > 0.001f ? navKm * 1000f : MathF.Sqrt((motion.X - destX) * (motion.X - destX) + (motion.Z - destZ) * (motion.Z - destZ)) * cfg.GameDistScale;
        long now = clock.ElapsedMilliseconds;
        if (motion.SpeedMps < 0.5f) { if (stillSinceMs == 0) stillSinceMs = now; } else stillSinceMs = 0;
        if (gameM < 150f || (stillSinceMs != 0 && now - stillSinceMs > 25000)) return false;
        if (!endHinted) { endHinted = true; voice.Say("The mapped route ends here. Follow the flag to the drop off point."); }
        SetBannerMessage("Mapped route ends here - follow the flag");
        ClearManeuver(); SetLaneUi(null);
        SetInfo(destName, "To the destination: about " + n.ShortDistance(gameM / MathF.Max(1f, cfg.GameDistScale)) + " (the game's route)");
        return true;
    }

    /// <summary>Learns how fast the truck really travels compared with the router's speeds, so the ETA follows reality.</summary>
    void UpdatePace(Navigator n)
    {
        long now = clock.ElapsedMilliseconds;
        float dt = (now - lastPaceMs) / 1000f;
        lastPaceMs = now;
        float model = n.ModelSpeedMps, actual = motion.SpeedMps;
        if (dt <= 0 || dt > 5 || model < 5 || actual < 3) return;
        float ratio = Math.Clamp(actual / model, 0.35f, 1.3f);
        float a = 1f - MathF.Exp(-dt / 90f);
        paceRatio = Math.Clamp(paceRatio + (ratio - paceRatio) * a, 0.4f, 1.2f);
    }

    static readonly Brush InfoYellow = Ui.Frozen(Color.FromRgb(255, 214, 51));

    /// <summary>The route line at the bottom: the destination in white, the distance and times under it in yellow.</summary>
    void SetInfo(string name, string detail)
    {
        infoText.Inlines.Clear();
        infoText.Inlines.Add(new System.Windows.Documents.Run(name));
        infoText.Inlines.Add(new System.Windows.Documents.LineBreak());
        infoText.Inlines.Add(new System.Windows.Documents.Run(detail) { Foreground = InfoYellow });
    }

    void RerouteIfNeeded()
    {
        long now = clock.ElapsedMilliseconds;
        if (!hasDest || routing || now - lastReroute < 4000) return;
        // inside the destination's yard or compound there are no mapped roads: do not recalculate (or keep saying so) there
        if (MathF.Sqrt((motion.X - destX) * (motion.X - destX) + (motion.Z - destZ) * (motion.Z - destZ)) < NearDestM) return;
        if (jobFlag is { } f && M.Hypot(motion.X - f.X, motion.Z - f.Z) < NearDestM) return;
        // only recalculate once the truck is back on a mapped road (not while manoeuvring in a yard or compound)
        if (router != null && router.SnapTo(motion.X, motion.Z, motion.Bearing, 35f) == null) return;
        lastReroute = now;
        if (!offAnnounced) { offAnnounced = true; voice.Say("Recalculating"); }   // once per time off the route, not every few seconds
        ComputeRoute(destX, destZ, destName, reroute: true);
    }

    void SetAvoidFerries(bool on, bool reroute = true)
    {
        cfg.AvoidFerries = on; cfg.Save();
        if (router != null) router.AvoidFerries = on;
        if (reroute) RerouteForModeChange();
    }

    void RerouteForModeChange() { if (hasDest && nav?.Active == true && !routing) ComputeRoute(destX, destZ, destName, reroute: true); }

    void CancelRoute()
    {
        resume = null; destIsPoi = false; destIsJob = false; jobFlag = null; mapView.FlagOn = false; waypoints.Clear(); afterM = 0; afterS = 0; SyncStops();
        nav?.Clear();
        routeEtaGameMin = -1; routeRemainingM = -1; lastNextText = ""; paceRatio = 1; hasDest = false;
        mapView.RoutePts = Array.Empty<float>(); mapView.RouteEdgePt = Array.Empty<int>(); mapView.NextManeuver = null;
        SetBannerMessage(null); ClearManeuver(); SetLaneUi(null);
        infoText.Text = "No route";
    }

    void ComputeRoute(float x, float z, string name, bool announce = false, bool isPoi = false, bool reroute = false, bool keepStops = false, bool isJob = false,
        (float X, float Z)? flag = null)
    {
        var r = router; var n = nav; var m = map;
        if (r == null || n == null || m == null) { Toast("Map not loaded yet"); return; }
        if (truck == null || !truck.Connected) { Toast("Start the game first - no truck position yet"); return; }
        if (routing) return;
        routing = true;
        if (!reroute)
        {
            if (isPoi)
            {
                if (hasDest && !destIsPoi) resume = (destX, destZ, destName, waypoints.ToList());
                waypoints = new();
            }
            else { resume = null; if (!keepStops) waypoints = new(); }
            destIsPoi = isPoi; destIsJob = isJob; endHinted = false; stillSinceMs = 0;
            jobFlag = isJob ? flag : null;
        }
        destX = x; destZ = z; destName = name; hasDest = true;
        if (!reroute && !isPoi && !keepStops) startLabel = place.Replace("Near ", "") is { Length: > 0 } pl ? pl : "Start";
        SyncStops();
        var leg = waypoints.Count > 0 ? waypoints[0] : null;
        float tx = leg?.X ?? x, tz = leg?.Z ?? z; string tname = leg?.Name ?? name;
        var chain = waypoints.Skip(1).Select(w => (w.X, w.Z)).Append((x, z)).ToList();
        bool legIsStop = leg != null;
        float sx = motion.X, sz = motion.Z, brg = motion.Bearing;
        if (announce) SetBannerMessage("Calculating route to " + tname + "…");
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => // let the "calculating" message paint first
        {
            try
            {
                var snap = r.SnapTo(sx, sz, brg, 60f) ?? r.SnapTo(sx, sz, null, 200f);
                if (snap == null) { SetBannerMessage("You are not on a known road"); return; }
                int node = r.NearestMainNode(tx, tz);
                if (node < 0) { SetBannerMessage("Destination is not reachable by road"); return; }
                float remaining = MathF.Max(1f, m.ELen[snap.De >> 1] - snap.Along);
                var res = r.Route(snap.De, remaining, node);
                if (res == null) { SetBannerMessage(r.AvoidFerries ? "No route without ferries - allow ferries in Settings" : "No route found"); return; }
                float am = 0, asec = 0; int prev = node;
                if (legIsStop)
                    foreach (var (cx, cz) in chain)
                    {
                        int nn = r.NearestMainNode(cx, cz);
                        if (nn < 0 || nn == prev || r.OutCount(prev) == 0) continue;
                        int ide = r.OutAt(prev, 0);
                        var rr = r.Route(ide, m.ELen[ide >> 1], nn);
                        if (rr != null) { am += rr.LengthM; asec += rr.TimeS; }
                        prev = nn;
                    }
                int total = 0;
                foreach (int de in res.Edges) total += m.DeCount(de);
                var pts = new float[total * 2]; var lift = new byte[total]; var starts = new int[res.Edges.Length];
                int p = 0;
                for (int i = 0; i < res.Edges.Length; i++)
                {
                    int de = res.Edges[i]; starts[i] = p;
                    for (int k = 0; k < m.DeCount(de); k++) { pts[p * 2] = m.DeX(de, k); pts[p * 2 + 1] = m.DeZ(de, k); lift[p] = (byte)(m.DeLift(de, k) * 2); p++; }
                }
                MapControl.SmoothJoins(pts, starts);
                n.DistScale = distScale;
                n.ArriveWord = legIsStop ? "your waypoint" : destIsJob ? "the destination area" : "your destination";
                mapView.FlagOn = true; mapView.FlagLink = jobFlag != null;
                (mapView.FlagX, mapView.FlagZ) = jobFlag ?? (x, z);
                afterM = am; afterS = asec;
                n.SetRoute(res, remaining, reroute);
                mapView.RoutePts = pts; mapView.RouteLift = lift; mapView.RouteEdgePt = starts; mapView.RouteIdx = 0;
                SetBannerMessage(null);
                if (announce)
                    voice.Say(legIsStop
                        ? $"Route to {tname}, the first of {waypoints.Count + 1} stops. {n.SpokenDistance(res.LengthM)}. {routeMode.Label} routing."
                        : $"Route to {name}. {n.SpokenDistance(res.LengthM)}. {routeMode.Label} routing.");
                NavTick();
            }
            catch (Exception e) { SetBannerMessage("Routing failed: " + e.Message); }
            finally { routing = false; }
        }));
    }

    void RouteToJob()
    {
        if (map == null) { Toast("Map not loaded yet"); return; }
        var t = truck;
        if (t == null || !t.Connected) { Toast("Start the game first - no telemetry yet"); return; }
        if (t.DestCityId.Length == 0 && t.DestCity.Length == 0)
        {
            if (t.Selected is { } sel) { NavigateToPickup(sel); return; }
            Toast("The game reports no active job"); return;
        }
        NavigateToJob(t);
    }

    string pickupDest = "";   // destName of the route to a selected job's pickup ("" = none): replaced by the delivery route when the trailer is hooked up

    /// <summary>A selected job's pickup: the company yard where the trailer waits.</summary>
    void NavigateToPickup(SelectedJob sel)
    {
        var m = map;
        if (m == null) { Toast("Map not loaded yet"); return; }
        string name = "Pickup: " + PlaceName(CompanyLabel(sel.SrcCompanyId), CityLabel(sel.SrcCityId));
        var co = m.FindCompany(sel.SrcCityId, sel.SrcCompanyId);
        if (co != null)
        {
            var yard = m.FindDrops(sel.SrcCityId, sel.SrcCompanyId);
            ComputeRoute(yard != null ? yard.Gate[0] : co.X, yard != null ? yard.Gate[1] : co.Z, name, announce: true);
        }
        else if (m.FindCity(sel.SrcCityId) is { } c) ComputeRoute(c.X, c.Z, name, announce: true);
        else { Toast($"The pickup '{sel.SrcCityId}' is not on the loaded map"); return; }
        pickupDest = name;
    }

    /// <summary>Names for a selected job's ids, from the map (the telemetry gives ids only).</summary>
    string CityLabel(string id) => map?.FindCity(id)?.Name ?? Pretty(id);
    string CompanyLabel(string id) => map != null && map.CompanyNames.TryGetValue(id, out var n) && n.Length > 0 ? n : Pretty(id);
    static string Pretty(string id) => id.Length == 0 ? "" : char.ToUpperInvariant(id[0]) + id[1..].Replace('_', ' ');

    void NavigateToJob(TruckState t)
    {
        var m = map!;
        var co = m.FindCompany(t.DestCityId, t.DestCompanyId);
        if (co != null)
        {
            // the route leads to the yard entrance (where the game's own route reaches the company); the flag stands at the drop-off spot inside
            var yard = m.FindDrops(t.DestCityId, t.DestCompanyId);
            var spot = yard?.Easy ?? yard?.Medium ?? yard?.Hard;
            float tx = yard != null ? yard.Gate[0] : co.X, tz = yard != null ? yard.Gate[1] : co.Z;
            ComputeRoute(tx, tz, t.DestCompany.Length > 0 ? t.DestCompany + ", " + t.DestCity : t.DestCity, announce: true, isJob: true,
                flag: spot != null ? (spot[0], spot[1]) : null);
            return;
        }
        var c = m.FindCity(t.DestCityId) ?? m.Cities.FirstOrDefault(c => c.Key == City.Normalize(t.DestCity));
        if (c == null) { Toast($"Job destination '{t.DestCity}' is not on the loaded map"); return; }
        ComputeRoute(c.X, c.Z, c.Name, announce: true, isJob: true);
    }

    // ---- the job ends: delivered (arrived) or cancelled (route no longer wanted)
    bool evInit, evConn, pDelivered, pCancelled, pFinished, pOnJob;

    void CheckJobEvents(TruckState t)
    {
        bool delivered = t.JobDelivered && !pDelivered, cancelled = t.JobCancelled && !pCancelled, finished = t.JobFinished && !pFinished;
        bool jobEnded = evConn && pOnJob && !t.OnJob;   // the job vanished without an event we saw
        bool first = !evInit; evInit = true;
        pDelivered = t.JobDelivered; pCancelled = t.JobCancelled; pFinished = t.JobFinished; pOnJob = t.OnJob; evConn = t.Connected;
        if (first || !hasDest || !destIsJob) return;
        if (cancelled)
        {
            voice.Say("The job was cancelled. Route cleared.");
            Toast("Job cancelled - route cleared");
            CancelRoute();
        }
        else if (delivered || finished || jobEnded) FinishRoute("Job complete");
    }

    /// <summary>The destination is reached (the route is over): same as arriving at the end of the route.</summary>
    void FinishRoute(string say)
    {
        var n = nav; if (n == null) return;
        if (say.Length > 0) voice.Say(say);
        mapView.FlagOn = false;
        infoText.Text = "Arrived at " + destName;
        SetBannerMessage("You have arrived"); ClearManeuver();
        n.Clear(); mapView.RoutePts = Array.Empty<float>(); mapView.RouteEdgePt = Array.Empty<int>(); mapView.NextManeuver = null;
        hasDest = false; destIsJob = false; jobFlag = null; SetLaneUi(null);
    }

    void CheckNewJob(TruckState t)
    {
        if (map == null) return;
        string key = t.DestCityId.Length > 0 ? t.DestCityId + "/" + t.DestCompanyId : t.Selected is { } sj ? "sel:" + sj.Key : "";
        if (key == lastJobKey) return;
        lastJobKey = key;
        if (key.Length == 0) return;
        if (t.Selected is { } sel)
        {
            // a job was taken in the job market: offer the drive to the pickup straight away
            string pickup = PlaceName(CompanyLabel(sel.SrcCompanyId), CityLabel(sel.SrcCityId));
            string text = $"{sel.CargoText}: {CityLabel(sel.SrcCityId)} to {CityLabel(sel.DestCityId)}" + (sel.PlannedKm > 0 ? $" ({Units.LongText(sel.PlannedKm)})" : "") +
                $".\n\nNavigate to the pickup at {pickup}?";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                askAccept = () => NavigateToPickup(sel);
                try
                {
                    Dlg.Message(this, "New job", text + (vctl?.Active == true ? $"\n\nSay \"{vctl.Wake}, take the job\" or \"{vctl.Wake}, not now\"." : ""),
                        ("Not now", null, false), ("Navigate", () => NavigateToPickup(sel), true));
                }
                finally { askAccept = null; }
            }));
            return;
        }
        ResetTrip();
        if (hasDest && pickupDest.Length > 0 && destName == pickupDest)
        {
            // the trailer is hooked up at the pickup we routed to: carry on to the delivery without asking
            pickupDest = "";
            Dispatcher.BeginInvoke(new Action(() => NavigateToJob(t)));
            return;
        }
        string name = PlaceName(t.DestCompany, t.DestCity);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // while it is open, voice control answers it: "take the job" navigates, "not now" closes it
            askAccept = () => NavigateToJob(t);
            try
            {
                Dlg.Message(this, "New job", $"Navigate to {name}?" + (vctl?.Active == true ? $"\n\nSay \"{vctl.Wake}, take the job\" or \"{vctl.Wake}, not now\"." : ""),
                    ("Not now", null, false), ("Navigate", () => NavigateToJob(t), true));
            }
            finally { askAccept = null; }
        }));
    }

    // ----------------------------------------------------------------------------- lanes --

    /// <summary>Hands the lane panel to the built-in server, so the separate lane overlay window (and a browser) can show it too.</summary>
    void PushLanes(List<Lane>? lanes, string caption)
    {
        if (server == null) return;
        var sb = new StringBuilder("{\"lanes\":");
        if (lanes == null) sb.Append("null");
        else
        {
            sb.Append('[');
            for (int i = 0; i < lanes.Count; i++)
            {
                var l = lanes[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"a\":").Append(l.Angle is double a ? Math.Round(a, 1).ToString(CultureInfo.InvariantCulture) : "null")
                  .Append(",\"p\":").Append(l.Preferred ? "true" : "false")
                  .Append(",\"alt\":").Append(l.Alt is double b ? Math.Round(b, 1).ToString(CultureInfo.InvariantCulture) : "null").Append('}');
            }
            sb.Append(']');
        }
        sb.Append(",\"caption\":").Append(JsonSerializer.Serialize(caption)).Append('}');
        server.SetLanes(sb.ToString());
    }

    // -------------------------------------------------------------------------- waypoints --

    void SyncStops() { mapView.Stops = waypoints.Select(w => (w.X, w.Z)).ToList(); mapView.InvalidateVisual(); }

    void AddWaypoint(float x, float z, string name)
    {
        if (!hasDest) { Toast("Create a route first, then add waypoints"); return; }
        if (routing) { Toast("Still planning - try again in a moment"); return; }
        waypoints.Add(new StopPoint { X = x, Z = z, Name = name }); SyncStops();
        Toast($"Waypoint {waypoints.Count} added: {name}");
        voice.Say("Waypoint added: " + name);
        RerouteForModeChange();
    }

    void ShowStops()
    {
        if (!hasDest) { Toast("Create a route with Search first, then add waypoints here"); return; }
        var names = waypoints.Select((w, i) => $"{i + 1}. {w.Name}").Append("Destination: " + destName).ToList();
        Dlg.Pick(this, "Route stops", names, i => { if (i < waypoints.Count) StopOptions(i); }, null,
            ("Add waypoint", () => ShowSearch(true)),
            ("Clear waypoints", () => { waypoints.Clear(); SyncStops(); RerouteForModeChange(); }));
    }

    void StopOptions(int i)
    {
        if (i >= waypoints.Count) return;
        var w = waypoints[i];
        Dlg.Pick(this, w.Name, new[] { "Remove", "Move earlier", "Move later" }, k =>
        {
            if (k == 0) waypoints.RemoveAt(i);
            else if (k == 1 && i > 0) { waypoints.RemoveAt(i); waypoints.Insert(i - 1, w); }
            else if (k == 2 && i < waypoints.Count - 1) { waypoints.RemoveAt(i); waypoints.Insert(i + 1, w); }
            SyncStops(); RerouteForModeChange();
        });
    }

    // ------------------------------------------------------------------------ saved trips --

    void SaveCurrentTrip()
    {
        if (!hasDest || nav?.Active != true) { Toast("Plan a route first, then save it"); return; }
        if (destIsPoi) { Toast("Finish the POI detour first, then save the trip"); return; }
        string def = startLabel + " → " + destName;
        Dlg.Input(this, "Save this trip as", def, v =>
        {
            string name = v.Trim().Length > 0 ? v.Trim() : def;
            cfg.GameTrips.RemoveAll(t => t.Name == name);
            cfg.GameTrips.Insert(0, new SavedTrip
            {
                Name = name, Mode = routeMode.Key, AvoidFerries = cfg.AvoidFerries, Dest = new StopPoint { X = destX, Z = destZ, Name = destName },
                Stops = waypoints.Select(w => new StopPoint { X = w.X, Z = w.Z, Name = w.Name }).ToList(), LengthM = MathF.Max(0, routeRemainingM),
            });
            cfg.Save(); Toast("Trip saved: " + name);
        }, "Save");
    }

    void ShowTrips()
    {
        var rows = cfg.GameTrips.Select(t =>
        {
            float km = t.LengthM * distScale / 1000f;
            return t.Name + "\n" + (km >= 1 ? $"{km:0} km · " : "") + $"{t.Stops.Count} waypoint{(t.Stops.Count == 1 ? "" : "s")} · " + RouteMode.ByKey(t.Mode).Label;
        }).ToList();
        Dlg.Pick(this, "Saved trips", rows, TripOptions, rows.Count == 0 ? "No saved trips yet. Use Plan a trip (no game needed), or plan a route while driving and tap Save current trip." : null,
            ("Plan a trip…", () => { plan = new PlanDraft(); ShowPlanner(); }), ("Save current trip", SaveCurrentTrip), ("Import / export…", TripFileMenu));
    }

    void TripOptions(int i)
    {
        if (i >= cfg.GameTrips.Count) return;
        var t = cfg.GameTrips[i];
        var items = new List<string>();
        if (t.Start != null) items.Add("Drive this trip (to its start first)");
        items.Add(t.Start != null ? "Drive from here (skip its start)" : "Start this trip from here");
        items.Add("View / edit the plan"); items.Add("Export this trip…"); items.Add("Rename"); items.Add("Delete");
        Dlg.Pick(this, t.Name, items, k =>
        {
            string what = items[k];
            if (what.StartsWith("Drive this trip")) StartSavedTrip(t, true);
            else if (what.StartsWith("Drive from here") || what.StartsWith("Start this trip")) StartSavedTrip(t);
            else if (what.StartsWith("View")) EditSavedTrip(t);
            else if (what.StartsWith("Export")) ExportTripsDialog(new List<SavedTrip> { t }, t.Name);
            else if (what == "Rename")
                Dlg.Input(this, "Rename trip", t.Name, v =>
                {
                    string name = v.Trim();
                    if (name.Length == 0 || name == t.Name) return;
                    cfg.GameTrips.RemoveAll(x => x != t && x.Name == name);
                    t.Name = name; cfg.Save(); Toast("Renamed to: " + name);
                }, "Rename");
            else { cfg.GameTrips.Remove(t); cfg.Save(); Toast("Deleted: " + t.Name); }
        });
    }

    void StartSavedTrip(SavedTrip t, bool viaStart = false)
    {
        if (map == null) { Toast("Map not loaded yet"); return; }
        if (truck == null || !truck.Connected) { Toast("Start the game first - no truck position yet"); return; }
        if (routing) { Toast("Still planning - try again in a moment"); return; }
        SetRouteMode(RouteMode.ByKey(t.Mode), false);
        SetAvoidFerries(t.AvoidFerries, false);
        waypoints = t.Stops.Select(w => new StopPoint { X = w.X, Z = w.Z, Name = w.Name }).ToList(); resume = null;
        // a planned trip begins at its own start: drive there first unless the truck is already close to it
        if (viaStart && t.Start != null && M.Hypot(t.Start.X - motion.X, t.Start.Z - motion.Z) > 1500f)
            waypoints.Insert(0, new StopPoint { X = t.Start.X, Z = t.Start.Z, Name = t.Start.Name });
        ComputeRoute(t.Dest.X, t.Dest.Z, t.Dest.Name, announce: true, keepStops: true);
    }

    // ---------------------------------------------------------------------- avoid / point menu --

    void AvoidRoadAt(float x, float z)
    {
        cfg.GameAvoid.Add(new StopPoint { X = x, Z = z }); cfg.Save();
        router?.SetAvoid(cfg.GameAvoid.Select(a => (a.X, a.Z)));
        Toast($"Avoiding the road here ({cfg.GameAvoid.Count} avoided in total)");
        voice.Say("Avoiding that road"); RerouteForModeChange();
    }

    void ClearAvoid()
    {
        cfg.GameAvoid.Clear(); cfg.Save(); router?.SetAvoid(Array.Empty<(float, float)>());
        Toast("Avoided roads cleared"); RerouteForModeChange();
    }

    void OnPointRequested(float x, float z)
    {
        if (planning) { PlanPointMenu(x, z); return; }
        bool canStop = hasDest && nav?.Active == true;
        var items = canStop ? new[] { "Navigate here", "Add as waypoint", "Avoid road here" } : new[] { "Navigate here", "Avoid road here" };
        Dlg.Pick(this, "This point on the map", items, i =>
        {
            switch (items[i])
            {
                case "Navigate here": ComputeRoute(x, z, "Selected point", announce: true); break;
                case "Add as waypoint": AddWaypoint(x, z, "Selected point"); break;
                default: AvoidRoadAt(x, z); break;
            }
        });
    }

    void ShowSearch(bool asWaypoint)
    {
        if (map == null) { Toast("Map not loaded yet"); return; }
        Dlg.Search(this, asWaypoint ? "Add waypoint" : "Navigate to", map.Cities, c =>
        {
            if (asWaypoint) AddWaypoint(c.X, c.Z, c.Name);
            else if (hasDest && nav?.Active == true)
                Dlg.Pick(this, c.Name, new[] { "Add as waypoint", "New destination (replace route)" }, k =>
                {
                    if (k == 0) AddWaypoint(c.X, c.Z, c.Name); else ComputeRoute(c.X, c.Z, c.Name, announce: true);
                });
            else ComputeRoute(c.X, c.Z, c.Name, announce: true);
        });
    }

    // ------------------------------------------------------------------------------- POI --

    record PoiHit(Poi Poi, float Dist, float Ahead, float Right);

    List<PoiHit> NearestPois(int type, int limit)
    {
        if (map == null) return new();
        float x = motion.X, z = motion.Z, b = motion.Bearing;
        float fx = MathF.Sin(b), fz = -MathF.Cos(b), rx = MathF.Cos(b), rz = MathF.Sin(b);
        var hits = new List<PoiHit>();
        foreach (var p in map.Pois)
        {
            if (p.Type != type) continue;
            float dx = p.X - x, dz = p.Z - z;
            if (dx > 15000 || dx < -15000 || dz > 15000 || dz < -15000) continue;
            float d = M.Hypot(dx, dz);
            hits.Add(new PoiHit(p, d, (dx * fx + dz * fz) / MathF.Max(d, 1f), (dx * rx + dz * rz) / MathF.Max(d, 1f)));
        }
        return hits.OrderBy(h => h.Ahead < -0.3f ? h.Dist * 2f : h.Dist).Take(limit).ToList(); // things behind you count double
    }

    string GameDist(float worldM)
    {
        return Units.Short(worldM * distScale);
    }

    static string Direction(PoiHit h) => h.Ahead > 0.5f ? "ahead" : h.Ahead < -0.5f ? "behind" : h.Right > 0 ? "to the right" : "to the left";

    void ShowPoiPicker()
    {
        if (map == null) { Toast("Map not loaded yet"); return; }
        int[] types = { Poi.Fuel, Poi.Parking, Poi.Service, Poi.Weigh, Poi.Garage, Poi.Dealer };
        Dlg.Pick(this, "Find nearby", types.Select(Poi.Label).ToList(), i => ShowPoiList(types[i]));
    }

    void ShowPoiList(int type)
    {
        if (truck == null || !truck.Connected) { Toast("Start the game first - no truck position yet"); return; }
        var hits = NearestPois(type, 15);
        if (hits.Count == 0) { Toast($"No {Poi.Label(type).ToLowerInvariant()} within range"); return; }
        Dlg.Pick(this, Poi.Label(type) + "s nearby", hits.Select(h => $"{Poi.Label(type)}  –  {GameDist(h.Dist)} {Direction(h)}").ToList(),
            i => ComputeRoute(hits[i].Poi.X, hits[i].Poi.Z, Poi.Label(type), announce: true, isPoi: true));
    }

    // -------------------------------------------------------------- altitude / hills / alerts --

    void UpdateAltitude(TruckState s)
    {
        elev.Add(motion.Travelled, s.Y);
        float g = motion.GradientPct;
        string arrow = !motion.GradientValid || MathF.Abs(g) < 0.5f ? "▬" : g > 0 ? "▲" : "▼";
        string gs = motion.GradientValid ? arrow + " " + MathF.Abs(g).ToString("0.0", CultureInfo.InvariantCulture) + "%" : arrow;
        altText.Text = gs + "\n" + Units.AltText(s.Y);
        altText.Foreground = motion.GradientValid && g <= -5f ? new SolidColorBrush(Color.FromRgb(255, 170, 90))
            : motion.GradientValid && g >= 6f ? new SolidColorBrush(Color.FromRgb(255, 120, 120)) : Brushes.White;
    }

    void CheckHill(TruckState s, long now)
    {
        if (!motion.GradientValid || s.SpeedKmh < 25f) { descStart = -1f; climbStart = -1f; return; }
        float g = motion.GradientPct, t = motion.Travelled;
        if (g <= -5f)
        {
            if (descStart < 0f) descStart = t;
            if (t - descStart >= 120f && now - lastDescWarn > 90_000) { lastDescWarn = now; voice.Say("Steep descent. Use the engine brake."); Toast("Steep descent - engine brake"); }
        }
        else if (g > -3f) descStart = -1f;
        if (g >= 6f)
        {
            if (climbStart < 0f) climbStart = t;
            if (t - climbStart >= 150f && now - lastClimbWarn > 120_000) { lastClimbWarn = now; voice.Say("Steep climb. Select a lower gear."); }
        }
        else if (g < 4f) climbStart = -1f;
    }

    void CheckAlerts(TruckState s)
    {
        if (map == null) return;
        bool low = s.FuelWarning || (s.FuelFraction >= 0 && s.FuelFraction <= 0.12f);
        if (low && !fuelAlerted)
        {
            fuelAlerted = true;
            var h = NearestPois(Poi.Fuel, 1).FirstOrDefault();
            voice.Say("Low fuel." + (h != null && nav != null ? $" Nearest fuel station is {nav.SpokenDistance(h.Dist)} {Direction(h)}." : ""));
            Toast("Low fuel - click POI, then Fuel station");
        }
        if (!low && s.FuelFraction > 0.25f) fuelAlerted = false;
        float r = s.NextRestMin;
        if (r >= 0)
        {
            if (r > 60) { rest30 = false; rest10 = false; }
            int mins = r <= 10 ? 10 : 30;
            if ((r <= 10 && !rest10) || (r <= 30 && !rest30))
            {
                if (r <= 10) rest10 = true;
                rest30 = true;
                var h = NearestPois(Poi.Parking, 1).FirstOrDefault();
                voice.Say($"You will need a rest in about {mins} minutes." + (h != null && nav != null ? $" Nearest rest area is {nav.SpokenDistance(h.Dist)} {Direction(h)}." : ""));
            }
        }
    }

    void UpdatePlace(long now)
    {
        if (now - lastPlaceCheck < 2000 || map == null) return;
        lastPlaceCheck = now;
        City? best = null; float bd = 4000f;
        foreach (var c in map.Cities)
        {
            float dx = c.X - motion.X, dz = c.Z - motion.Z;
            if (dx > bd || dx < -bd || dz > bd || dz < -bd) continue;
            float d = M.Hypot(dx, dz);
            if (d < bd) { bd = d; best = c; }
        }
        place = best != null ? "Near " + best.Name : "";
    }

    // -------------------------------------------------------------------------- trip stats --

    void ResetTrip() { tripBase = motion.Travelled; tripDriveMs = 0; tripMaxKmh = 0; tripFuelUsed = 0; tripLastFuel = -1f; elev.Clear(); }

    void TrackTrip(TruckState s, long now)
    {
        long dt = Math.Clamp(now - lastTripMs, 0, 2000); lastTripMs = now;
        if (!s.Connected) return;
        if (s.SpeedKmh > 3f) tripDriveMs += dt;
        tripMaxKmh = MathF.Max(tripMaxKmh, s.SpeedKmh);
        float f = s.Detail.FuelL;
        if (f > 0f) { if (tripLastFuel > 0f && f < tripLastFuel) tripFuelUsed += tripLastFuel - f; tripLastFuel = f; }
    }
}

