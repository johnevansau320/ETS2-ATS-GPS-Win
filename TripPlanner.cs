using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ets2Gps;

/// <summary>
/// Trip planner: choose a start, stops and a destination, preview the route on the map and save it as a trip, all without the
/// game running (the route does not start from the truck). A saved trip can be driven later from wherever the truck is.
/// </summary>
public sealed partial class MainWindow
{
    sealed class PlanDraft
    {
        public StopPoint? Start, Dest;
        public List<StopPoint> Stops = new();
        public string? EditName;                 // the saved trip being edited, if any
        public float LengthM = -1, TimeS;        // of the previewed route; -1 = not calculated for the current plan
    }

    PlanDraft plan = new();
    bool planning;
    Border planBar = new();
    TextBlock planText = new();

    static StopPoint Copy(StopPoint p) => new() { X = p.X, Z = p.Z, Name = p.Name };

    Border BuildPlanBar()
    {
        planText = Ui.Text("", 24, null, FontWeights.SemiBold);
        planText.TextAlignment = TextAlignment.Center; planText.Margin = new Thickness(0, 0, 0, 8); planText.TextWrapping = TextWrapping.Wrap;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        void Btn(string text, Action fn, Brush? bg = null)
        {
            var b = Ui.Button(text, double.NaN, 56, bg, 24); b.Padding = new Thickness(20, 0, 20, 0); b.Margin = new Thickness(6, 0, 6, 0);
            b.Click += (_, _) => fn();
            row.Children.Add(b);
        }
        Btn("Edit plan", () => { ExitPlanView(); ShowPlanner(); });
        Btn("Save trip", () => { SavePlan(); });
        Btn("Drive this trip", DrivePlan, Ui.Accent);
        Btn("Done", ExitPlanView);
        var box = new StackPanel(); box.Children.Add(planText); box.Children.Add(row);
        planBar = new Border
        {
            Child = box, Background = Ui.PanelDark, CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 14, 18, 14),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 140),
            Visibility = Visibility.Collapsed, MaxWidth = 1100,
        };
        return planBar;
    }

    // ----------------------------------------------------------------------------- planner dialog --

    void ShowPlanner()
    {
        if (map == null || router == null) { Toast("Map not loaded yet"); return; }
        var p = plan;
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, FontSize = 15, MaxHeight = 380, Margin = new Thickness(8, 0, 8, 0) };
        var actions = new List<Action>();
        void Row(string text, Action act)
        {
            actions.Add(act);
            list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8, 8, 8, 8) }, Foreground = Brushes.White });
        }
        Row("Start:  " + (p.Start?.Name ?? "(tap to choose, or leave empty to start from the truck)"), StartOptions);
        for (int i = 0; i < p.Stops.Count; i++) { int k = i; Row($"Stop {i + 1}:  {p.Stops[i].Name}", () => PlanStopOptions(k)); }
        Row("Destination:  " + (p.Dest?.Name ?? "(tap to choose)"), DestOptions);
        list.SelectionChanged += (_, _) =>
        {
            int idx = list.SelectedIndex;
            if (idx < 0) return;
            var w = Window.GetWindow(list);
            if (w != null) { w.Tag = actions[idx]; w.Close(); }
        };
        string summary = p.LengthM >= 0
            ? $"{GameDist(p.LengthM)}  ·  Real {FormatDuration(p.TimeS / 60f)}  ·  Sim {FormatDuration(p.TimeS / 60f * TimeScale)}"
            : "Plan a trip without the game running: choose a start, add stops, choose the destination, then preview it on the map.";
        var head = Ui.Text(summary, 15, Ui.Dim); head.TextWrapping = TextWrapping.Wrap; head.Margin = new Thickness(18, 8, 18, 8);
        var sp = new StackPanel(); sp.Children.Add(head); sp.Children.Add(list);
        Dlg.Custom(this, p.EditName != null ? "Plan trip: " + p.EditName : "Plan a trip", sp, new (string, Action?, bool)[]
        {
            ("Close", null, false),
            ("Clear", () => { plan = new PlanDraft(); ShowPlanner(); }, false),
            ("Add stop", () => { PickPlace("Add a stop", c => { plan.Stops.Add(c); plan.LengthM = -1; }); ShowPlanner(); }, false),
            ("Save trip", () => SavePlan(), false),
            ("Preview on map", PreviewPlan, true),
        }, 760);
    }

    void PickPlace(string title, Action<StopPoint> pick)
    {
        if (map == null) return;
        Dlg.Search(this, title, map.Cities, c => pick(new StopPoint { X = c.X, Z = c.Z, Name = c.Name }));
    }

    bool HaveTruckFix => truck?.Connected == true && (motion.X != 0 || motion.Z != 0);

    StopPoint MyPosition() => new() { X = motion.X, Z = motion.Z, Name = place.Replace("Near ", "") is { Length: > 0 } pl ? pl : "My position" };

    void StartOptions()
    {
        var items = new List<string> { "Search for a city…" };
        if (HaveTruckFix) items.Add("Use my current position");
        if (plan.Start != null) items.Add("Clear the start");
        Dlg.Pick(this, "Start of the trip", items, i =>
        {
            switch (items[i])
            {
                case "Search for a city…": PickPlace("Start of the trip", c => { plan.Start = c; plan.LengthM = -1; }); break;
                case "Use my current position": plan.Start = MyPosition(); plan.LengthM = -1; break;
                default: plan.Start = null; plan.LengthM = -1; break;
            }
        });
        ShowPlanner();
    }

    void DestOptions()
    {
        var items = new List<string> { "Search for a city…" };
        if (plan.Dest != null) items.Add("Clear the destination");
        Dlg.Pick(this, "Destination", items, i =>
        {
            if (i == 0) PickPlace("Destination", c => { plan.Dest = c; plan.LengthM = -1; });
            else { plan.Dest = null; plan.LengthM = -1; }
        });
        ShowPlanner();
    }

    void PlanStopOptions(int i)
    {
        if (i < 0 || i >= plan.Stops.Count) { ShowPlanner(); return; }
        var w = plan.Stops[i];
        Dlg.Pick(this, w.Name, new[] { "Replace it (search)…", "Move earlier", "Move later", "Remove" }, k =>
        {
            switch (k)
            {
                case 0: PickPlace("Replace stop " + (i + 1), c => plan.Stops[i] = c); break;
                case 1: if (i > 0) { plan.Stops.RemoveAt(i); plan.Stops.Insert(i - 1, w); } break;
                case 2: if (i < plan.Stops.Count - 1) { plan.Stops.RemoveAt(i); plan.Stops.Insert(i + 1, w); } break;
                default: plan.Stops.RemoveAt(i); break;
            }
            plan.LengthM = -1;
        });
        ShowPlanner();
    }

    // --------------------------------------------------------------------------------- routing --

    /// <summary>Routes start -> stops -> destination (the start is not the truck) and fills the preview line. Returns an error text or null.</summary>
    string? ComputePlan()
    {
        var r = router; var m = map;
        if (r == null || m == null) return "Map not loaded yet";
        var start = plan.Start ?? (HaveTruckFix ? MyPosition() : null);
        if (start == null) return "Choose a start first (or start the game)";
        if (plan.Dest == null) return "Choose a destination first";
        var pts = new List<StopPoint> { start };
        pts.AddRange(plan.Stops); pts.Add(plan.Dest);
        var edges = new List<int>(); float len = 0, time = 0; int lastDe = -1;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            int b = r.NearestMainNode(pts[i + 1].X, pts[i + 1].Z);
            if (b < 0) return pts[i + 1].Name + " is not reachable by road";
            int a = lastDe < 0 ? r.NearestMainNode(pts[i].X, pts[i].Z) : m.DeTo(lastDe);
            if (a < 0) return pts[i].Name + " is not reachable by road";
            if (a == b) continue;
            RouteResult? best = null;
            int n = r.OutCount(a);
            for (int k = 0; k < n && k < 4; k++)
            {
                int de = r.OutAt(a, k);
                if (lastDe >= 0 && n > 1 && (de >> 1) == (lastDe >> 1)) continue;   // no U-turn at a stop
                if (r.AvoidFerries && m.IsFerry(de >> 1)) continue;
                var res = r.Route(de, m.ELen[de >> 1], b);
                if (res != null && (best == null || res.TimeS < best.TimeS)) best = res;
            }
            if (best == null) return r.AvoidFerries ? "No route without ferries - allow ferries in Settings" : "No route to " + pts[i + 1].Name;
            edges.AddRange(best.Edges); len += best.LengthM; time += best.TimeS; lastDe = best.Edges[^1];
        }
        if (edges.Count == 0) return "The start and the destination are the same place";
        int total = 0;
        foreach (int de in edges) total += m.DeCount(de);
        var line = new float[total * 2]; var lift = new byte[total]; var starts = new float[edges.Count];
        int p = 0;
        for (int i = 0; i < edges.Count; i++)
        {
            int de = edges[i]; starts[i] = p;
            for (int k = 0; k < m.DeCount(de); k++) { line[p * 2] = m.DeX(de, k); line[p * 2 + 1] = m.DeZ(de, k); lift[p] = (byte)(m.DeLift(de, k) * 2); p++; }
        }
        mapView.PlanEdgePt = starts.Select(v => (int)v).ToArray(); MapControl.SmoothJoins(line, mapView.PlanEdgePt);
        mapView.PlanPts = line; mapView.PlanLift = lift;
        plan.LengthM = len; plan.TimeS = time;
        var pins = new List<(float X, float Z, string Label, Color C)> { (start.X, start.Z, "S", Color.FromRgb(30, 150, 80)) };
        for (int i = 0; i < plan.Stops.Count; i++) pins.Add((plan.Stops[i].X, plan.Stops[i].Z, (i + 1).ToString(CultureInfo.InvariantCulture), Color.FromRgb(25, 118, 210)));
        pins.Add((plan.Dest.X, plan.Dest.Z, "D", Color.FromRgb(200, 60, 60)));
        mapView.Pins = pins;
        return null;
    }

    void PreviewPlan()
    {
        Toast("Calculating the route…");
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            string? err;
            try { err = ComputePlan(); } catch (Exception e) { err = "Routing failed: " + e.Message; }
            if (err != null) { Toast(err); ShowPlanner(); return; }
            EnterPlanView();
        }));
    }

    void EnterPlanView()
    {
        var pts = mapView.PlanPts;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i + 1 < pts.Length; i += 2)
        {
            minX = MathF.Min(minX, pts[i]); maxX = MathF.Max(maxX, pts[i]);
            minZ = MathF.Min(minZ, pts[i + 1]); maxZ = MathF.Max(maxZ, pts[i + 1]);
        }
        double w = Math.Max(300, mapView.ActualWidth), h = Math.Max(200, mapView.ActualHeight);
        double spanX = Math.Max(200, maxX - minX), spanZ = Math.Max(200, maxZ - minZ);
        double pxPerM = Math.Min((w - 200) * 0.85 / spanX, h * 0.55 / spanZ);
        float zoom = (float)Math.Clamp(pxPerM / 0.55, 0.004, 4);
        planning = true;
        mapView.ShowPlan((minX + maxX) / 2 - (float)(100 / pxPerM), (minZ + maxZ) / 2 + (float)(h * 0.08 / pxPerM), zoom);   // clear of the left menu
        recentreBtn.Visibility = Visibility.Collapsed;
        UpdatePlanBar();
        planBar.Visibility = Visibility.Visible;
    }

    void UpdatePlanBar()
    {
        string stops = plan.Stops.Count > 0 ? $"  ·  {plan.Stops.Count} stop{(plan.Stops.Count > 1 ? "s" : "")}" : "";
        string from = plan.Start?.Name ?? (HaveTruckFix ? MyPosition().Name : "?");
        planText.Text = $"{from} → {plan.Dest?.Name}{stops}\n{GameDist(plan.LengthM)}  ·  Real {FormatDuration(plan.TimeS / 60f)}  ·  Sim {FormatDuration(plan.TimeS / 60f * TimeScale)}";
    }

    void ExitPlanView()
    {
        if (!planning) return;
        planning = false;
        planBar.Visibility = Visibility.Collapsed;
        mapView.HidePlan();
        recentreBtn.Visibility = !tripOpen && mapView.Free ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Right-click on the map while planning: use that spot as the start, a stop or the destination.</summary>
    void PlanPointMenu(float x, float z)
    {
        var items = new[] { "Set as the start", "Add as a stop", "Set as the destination" };
        Dlg.Pick(this, "This point on the map", items, i =>
        {
            var pt = new StopPoint { X = x, Z = z, Name = "Selected point" };
            if (i == 0) plan.Start = pt; else if (i == 1) plan.Stops.Add(pt); else plan.Dest = pt;
            plan.LengthM = -1;
            PreviewPlan();
        });
    }

    // ------------------------------------------------------------------------------ save / drive --

    void SavePlan()
    {
        if (plan.Dest == null) { Toast("Choose a destination first"); ShowPlanner(); return; }
        if (plan.LengthM < 0)
        {
            string? err;
            try { err = ComputePlan(); } catch (Exception e) { err = "Routing failed: " + e.Message; }
            if (err != null) { Toast(err); ShowPlanner(); return; }
        }
        string def = plan.EditName ?? ((plan.Start?.Name ?? "Trip") + " → " + plan.Dest.Name);
        Dlg.Input(this, "Save this trip as", def, v => SaveTripNow(v.Trim().Length > 0 ? v.Trim() : def), "Save");
        if (planning) UpdatePlanBar();
    }

    void SaveTripNow(string name)
    {
        if (plan.Dest == null) return;
        cfg.GameTrips.RemoveAll(t => t.Name == name || (plan.EditName != null && t.Name == plan.EditName));
        cfg.GameTrips.Insert(0, new SavedTrip
        {
            Name = name, Mode = routeMode.Key, AvoidFerries = cfg.AvoidFerries,
            Start = plan.Start != null ? Copy(plan.Start) : null, Dest = Copy(plan.Dest),
            Stops = plan.Stops.Select(Copy).ToList(), LengthM = plan.LengthM,
        });
        cfg.Save(); plan.EditName = name; Toast("Trip saved: " + name);
    }

    /// <summary>Test hook: --plan "Start|Stop|Destination" builds that plan and shows the preview (--plan-save also saves it).</summary>
    void AutoPlan(string spec, bool save)
    {
        if (map == null) return;
        StopPoint? Find(string n) { var c = map.Cities.FirstOrDefault(x => x.Key == City.Normalize(n.Trim())); return c == null ? null : new StopPoint { X = c.X, Z = c.Z, Name = c.Name }; }
        var names = spec.Split('|');
        plan = new PlanDraft { Start = Find(names[0]), Dest = Find(names[^1]) };
        foreach (var n in names.Skip(1).Take(Math.Max(0, names.Length - 2))) if (Find(n) is { } sp) plan.Stops.Add(sp);
        string? err = ComputePlan();
        if (err != null) { Toast(err); return; }
        if (save) SaveTripNow((plan.Start?.Name ?? "Trip") + " → " + plan.Dest?.Name);
        if (Environment.GetCommandLineArgs().Contains("--plan-dialog")) { ShowPlanner(); return; }
        if (Environment.GetCommandLineArgs().Contains("--trips-dialog")) { ShowTrips(); return; }
        EnterPlanView();
    }

    void DrivePlan()
    {
        if (plan.Dest == null) return;
        var trip = new SavedTrip { Mode = routeMode.Key, AvoidFerries = cfg.AvoidFerries, Start = plan.Start, Dest = plan.Dest, Stops = plan.Stops };
        ExitPlanView();
        StartSavedTrip(trip, true);
    }

    /// <summary>Opens a saved trip in the planner (to look at it on the map, change it and save it again).</summary>
    void EditSavedTrip(SavedTrip t)
    {
        SetRouteMode(RouteMode.ByKey(t.Mode), false);
        SetAvoidFerries(t.AvoidFerries, false);
        plan = new PlanDraft
        {
            Start = t.Start != null ? Copy(t.Start) : null, Dest = Copy(t.Dest), Stops = t.Stops.Select(Copy).ToList(),
            EditName = t.Name, LengthM = -1,
        };
        ShowPlanner();
    }
}
