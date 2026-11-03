using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ets2Gps;

/// <summary>Trip / job screen, elevation chart and settings (screens of the main window).</summary>
public sealed partial class MainWindow
{
    static readonly Brush Red = Ui.Frozen(Color.FromRgb(255, 120, 110)), Amber = Ui.Frozen(Color.FromRgb(255, 190, 90)), GreenText = Ui.Frozen(Color.FromRgb(60, 220, 130));

    static Brush DamageBrush(float v) => v < 5 ? Brushes.White : v < 25 ? Amber : Red;
    static string Fmt(double v, string f = "N0") => v.ToString(f, CultureInfo.InvariantCulture);

    Border Card(string title, params (string K, string V, Brush? C)[] rows)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Ui.Frozen(Color.FromRgb(159, 176, 198)), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var (k, v, c) in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = k, FontSize = 15, Foreground = Brushes.White });
            var val = new TextBlock { Text = v, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = c ?? Brushes.White, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 0, 0) };
            Grid.SetColumn(val, 1); g.Children.Add(val);
            sp.Children.Add(g);
        }
        // the rows are kept on the card so the dashboard layout can draw the same card its own way
        return new Border { Child = sp, Tag = (title, rows), Background = Ui.Frozen(Color.FromRgb(24, 33, 45)), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(5) };
    }

    void UpdateTrip()
    {
        var s = truck;
        var page = new StackPanel { Margin = new Thickness(6) };
        page.Children.Add(new TextBlock { Text = "TRIP DETAILS", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        if (s == null || !s.Connected)
        {
            page.Children.Add(Card("WAITING", ("Game", "not connected", null)));
            ShowTripPage(page, null);
            return;
        }
        var j = s.Job; var dt = s.Detail; var n = nav;
        bool routingNow = n?.Active == true && routeRemainingM >= 0f;
        // this screen always counts in game kilometres (planned distance, range and consumption come from the game), whatever the Real / Sim distance setting
        float remGameKm = routingNow ? routeRemainingM * cfg.GameDistScale / 1000f : dt.NavDistanceKm;
        float realMin = routingNow && routeEtaGameMin >= 0f ? routeEtaGameMin / TimeScale : -1f;
        float simMin = routingNow ? routeEtaGameMin : (s.NavEtaMin >= 0f ? s.NavEtaMin : -1f);
        int planned = j?.PlannedKm ?? 0;
        float progress = planned > 0 && remGameKm > 0f ? Math.Clamp(1f - remGameKm / planned, 0f, 1f) : 0f;
        var now = DateTime.Now;

        // header strip: from -> to, progress, clocks
        var head = new Grid();
        head.RowDefinitions.Add(new RowDefinition()); head.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) }); head.RowDefinitions.Add(new RowDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition());
        TextBlock T(string t, HorizontalAlignment h, int row, int col) { var tb = new TextBlock { Text = t, FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = h }; Grid.SetRow(tb, row); Grid.SetColumn(tb, col); head.Children.Add(tb); return tb; }
        T("FROM  " + (j != null ? PlaceName(j.SrcCompany, j.SrcCity) : "-"), HorizontalAlignment.Left, 0, 0);
        T("TO  " + (j != null ? PlaceName(j.DestCompany, j.DestCity) : (hasDest ? destName : "-")), HorizontalAlignment.Right, 0, 1);
        var bar = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new Border { Background = Ui.Frozen(Color.FromRgb(36, 48, 64)), CornerRadius = new CornerRadius(5) });
        var fill = new Border { Background = Ui.Frozen(Color.FromRgb(90, 190, 255)), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left };
        bar.Children.Add(fill);
        bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * progress;
        Grid.SetRow(bar, 1); Grid.SetColumnSpan(bar, 2); head.Children.Add(bar);
        T($"{Units.LongText(remGameKm)} left" + (planned > 0 ? $" of {Units.LongText(planned)} · {progress * 100:0}%" : ""), HorizontalAlignment.Left, 2, 0);
        T("Local " + now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ·  " + Games.Current.Short + " " + ClockText(s.GameTimeMin), HorizontalAlignment.Right, 2, 1);
        page.Children.Add(new Border { Child = head, Background = Ui.Frozen(Color.FromRgb(24, 33, 45)), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(5, 8, 5, 5) });

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        const double cardW = 380;
        void Add(Border c) { c.Width = cardW; wrap.Children.Add(c); }

        if (j != null)
        {
            bool late = j.DueMin >= 0f && simMin >= 0f && simMin > j.DueMin;
            var rows = new List<(string, string, Brush?)>
            {
                ("Cargo", j.Cargo + (j.MassKg > 0 ? "  " + Units.Mass(j.MassKg) : ""), null), ("Units", j.Units.ToString(CultureInfo.InvariantCulture), null),
            };
            if (j.Income > 0) rows.Add(("Income", Units.Money + Fmt(j.Income), null));
            if (j.DueMin >= 0f)
            {
                rows.Add(("Deadline in", FormatDuration(j.DueMin) + " (sim)", late ? Red : null));
                if (simMin >= 0f) { float sp = j.DueMin - simMin; rows.Add((late ? "Running late by" : "Time to spare", FormatDuration(MathF.Abs(sp)) + " (sim)", late ? Red : GreenText)); }
            }
            if (planned > 0) rows.Add(("Planned distance", Units.LongText(planned), null));
            Add(Card("JOB", rows.ToArray()));
        }
        else Add(Card("JOB", ("Status", "no active job", null)));

        var rr = new List<(string, string, Brush?)> { ("Destination", hasDest ? destName : "none", null) };
        if (routingNow && n != null)
        {
            rr.Add(("Remaining", Units.LongText(remGameKm), null)); rr.Add(("Real time left", FormatDuration(realMin), null));
            rr.Add(("Sim time left", FormatDuration(simMin), null)); rr.Add(("Arrive (sim clock)", ClockText(s.GameTimeMin + simMin), null));
            if (lastNextText.Length > 0) rr.Add(("Next", char.ToUpperInvariant(lastNextText[0]) + lastNextText[1..], null));
        }
        else { rr.Add(("Game route left", Units.LongText(remGameKm), null)); if (simMin >= 0f) rr.Add(("Game ETA (sim)", FormatDuration(simMin), null)); }
        rr.Add(("Route type", routeMode.Label, null));
        rr.Add(("Ferries", cfg.AvoidFerries ? "avoided" : "allowed", null));
        Add(Card("ROUTE", rr.ToArray()));

        float fr = dt.FuelCapL > 0f ? dt.FuelL / dt.FuelCapL : 0f;
        Brush? fc = fr < 0.12f ? Red : fr < 0.25f ? Amber : null;
        Add(Card("TRUCK", ("Truck", dt.TruckName.Length > 0 ? dt.TruckName : "-", null),
            ("Fuel", $"{Fmt(Units.Fuel(dt.FuelL))} / {Units.FuelText(dt.FuelCapL)}  ({fr * 100f:0}%)", fc), ("Range", Units.LongText(dt.RangeKm), null),
            ("Consumption", Units.Consumption(dt.ConsumptionLPerKm), null), ("Odometer", Units.LongText(dt.OdometerKm), null),
            ("Speed", Units.SpeedText(s.SpeedKmh) + (s.SpeedLimitKmh > 0 ? $"  (limit {Math.Round(Units.Speed(s.SpeedLimitKmh))})" : ""), null)));

        if (dt.TrailerAttached)
            Add(Card("TRAILER & CARGO", ("Trailer", dt.TrailerName.Length > 0 ? dt.TrailerName : "-", null), ("Plate", dt.TrailerPlate.Length > 0 ? dt.TrailerPlate : "-", null),
                ("Chassis damage", $"{dt.TrailerChassis:0}%", DamageBrush(dt.TrailerChassis)), ("Wheels damage", $"{dt.TrailerWheels:0}%", DamageBrush(dt.TrailerWheels)),
                ("Body damage", $"{dt.TrailerBody:0}%", DamageBrush(dt.TrailerBody)), ("Cargo damage", $"{dt.CargoDamage:0}%", DamageBrush(dt.CargoDamage))));
        else Add(Card("TRAILER & CARGO", ("Trailer", "none attached", null)));

        float drivenM = MathF.Max(0f, motion.Travelled - tripBase), driven = drivenM * cfg.GameDistScale / 1000f;
        float avg = tripDriveMs > 1000 ? drivenM / (tripDriveMs / 1000f) * 3.6f : 0f;
        var tc = new List<(string, string, Brush?)>
        {
            ("Distance driven", Units.LongText(driven), null), ("Driving time (real)", FormatDuration(tripDriveMs / 60000f), null),
            ("Driving time (sim)", FormatDuration(tripDriveMs / 60000f * TimeScale), null), ("Average speed", Units.SpeedText(avg), null), ("Top speed", Units.SpeedText(tripMaxKmh), null),
            ("Climbed / descended", $"{Units.Alt(elev.Ascent()):0} / {Units.AltText(elev.Descent())}", null), ("Fuel used", Units.FuelText(tripFuelUsed), null),
        };
        if (driven > 5f && tripFuelUsed > 0f) tc.Add(("Trip consumption", Units.Consumption(tripFuelUsed / driven), null));
        Add(Card("THIS TRIP", tc.ToArray()));

        var tr = new List<(string, string, Brush?)> { ("Sim clock", ClockText(s.GameTimeMin), null), ("Real time", now.ToString("HH:mm", CultureInfo.InvariantCulture), null) };
        if (s.NextRestMin >= 0f) tr.Add(("Rest needed in", FormatDuration(s.NextRestMin) + " (sim)", s.NextRestMin <= 30f ? Red : s.NextRestMin <= 90f ? Amber : null));
        if (place.Length > 0) tr.Add(("Location", place.Replace("Near ", ""), null));
        Add(Card("TIME & REST", tr.ToArray()));

        // a wide window gets the dashboard (the same design as the tablet version); a tall, narrow one keeps the card grid
        double pw = (tripPage.ActualWidth > 0 ? tripPage.ActualWidth : DesignW) - TripMenuW, ph = tripPage.ActualHeight > 0 ? tripPage.ActualHeight : DesignH;
        if (pw / ph >= 1.45)
        {
            var row2 = new[] { "THIS TRIP", "TRAILER & CARGO", "TRUCK", "ROUTE", "TIME & REST" }
                .Select(t => wrap.Children.OfType<Border>().Select(b => b.Tag).OfType<(string Title, (string K, string V, Brush? C)[] Rows)>().FirstOrDefault(c => c.Title == t))
                .Where(c => c.Title != null).ToList();
            ShowDashboard(s, remGameKm, realMin, simMin, row2);
            return;
        }
        page.Children.Add(wrap);
        ShowTripPage(page, wrap);
    }

    const double TripMenuW = 204;   // the left menu stays visible beside the job info page

    /// <summary>
    /// The job info dashboard for wide windows, a port of the tablet's: header strip, five headline cards, five detail cards,
    /// drawn on a fixed 1000 x 562 (16:9) design canvas that is scaled to fill the window, so it never needs scrolling.
    /// </summary>
    void ShowDashboard(TruckState s, float remGameKm, float realMin, float simMin, List<(string Title, (string K, string V, Brush? C)[] Rows)> row2)
    {
        const double W0 = 1000, H0 = 562;
        var j = s.Job; var dt = s.Detail;
        var inv = CultureInfo.InvariantCulture;
        Brush white = Brushes.White, green = GreenText;
        Brush cardBg = Ui.Frozen(Color.FromRgb(20, 27, 37)), edge = Ui.Frozen(Color.FromRgb(40, 52, 68)), titleB = Ui.Frozen(Color.FromRgb(120, 160, 200)),
            labelB = Ui.Frozen(Color.FromRgb(135, 148, 165)), subB = Ui.Frozen(Color.FromRgb(170, 180, 195)), chipText = Ui.Frozen(Color.FromRgb(8, 12, 18)),
            cyan = Ui.Frozen(Color.FromRgb(40, 190, 230)), simClock = Ui.Frozen(Color.FromRgb(255, 176, 40));
        var cv = new Canvas { Width = W0, Height = H0, Background = Ui.Frozen(Color.FromRgb(10, 13, 19)) };

        // text placed by its baseline, as on the tablet; with maxW it is cut short with an ellipsis, right = aligned to x
        TextBlock Txt(string t, double x, double baseY, double size, Brush b, FontWeight wt, double maxW = 0, bool right = false)
        {
            var tb = new TextBlock { Text = t, FontSize = size, Foreground = b, FontWeight = wt, TextTrimming = TextTrimming.CharacterEllipsis };
            if (maxW > 0) tb.MaxWidth = maxW;
            if (right) { tb.TextAlignment = TextAlignment.Right; tb.Width = maxW > 0 ? maxW : 400; Canvas.SetLeft(tb, x - tb.Width); }
            else Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, baseY - size * 1.02);
            cv.Children.Add(tb);
            return tb;
        }
        void Box(double x, double y, double w, double h, Brush bg, Brush? stroke, double r)
        {
            var b = new Border { Width = w, Height = h, Background = bg, CornerRadius = new CornerRadius(r), BorderBrush = stroke, BorderThickness = new Thickness(stroke != null ? 1 : 0) };
            Canvas.SetLeft(b, x); Canvas.SetTop(b, y); cv.Children.Add(b);
        }
        Border Chip(string text, double right, double cy, Brush bg, double size, Brush? fg = null)
        {
            var b = new Border { Background = bg, CornerRadius = new CornerRadius(size), Padding = new Thickness(size * 0.8, 0, size * 0.8, 0), Height = size * 1.9,
                Child = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.Bold, Foreground = fg ?? chipText, VerticalAlignment = VerticalAlignment.Center } };
            b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(b, right - b.DesiredSize.Width); Canvas.SetTop(b, cy - size * 0.95); cv.Children.Add(b);
            return b;
        }
        // one label / value row across a card
        void Row(string label, string value, Brush? c, double x, double baseY, double w, double labelSize, double valueSize)
        {
            var g = new Grid { Width = w };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = labelSize, Foreground = labelB, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });
            var v = new TextBlock { Text = value, FontSize = valueSize, FontWeight = FontWeights.Bold, Foreground = c ?? white, TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumn(v, 1); g.Children.Add(v);
            Canvas.SetLeft(g, x); Canvas.SetTop(g, baseY - valueSize * 1.02); cv.Children.Add(g);
        }

        static string DurShort(float min)
        {
            int m = (int)MathF.Round(MathF.Abs(min));
            return m >= 1440 ? $"{m / 1440}d {m % 1440 / 60:00}h" : m >= 60 ? $"{m / 60}h {m % 60:00}m" : m + "m";
        }
        float gameNow = s.GameTimeMin;
        float arrAbs = simMin >= 0f && gameNow >= 0f ? gameNow + simMin : -1f;
        int arrDays = arrAbs >= 0f ? (int)(arrAbs / 1440f) - (int)(gameNow / 1440f) : 0;
        string arrBig = arrAbs >= 0f ? ClockText(arrAbs)[4..] : "-";
        string arrSub = arrAbs >= 0f ? ClockText(arrAbs)[..3].ToUpperInvariant() + (arrDays > 0 ? $" +{arrDays}d" : "") : "";
        string etaYours = "-";
        if (realMin >= 0f)
        {
            var at = DateTime.Now.AddMinutes(Math.Round(realMin)); int dd = (at.Date - DateTime.Today).Days;
            etaYours = at.ToString("HH:mm", inv) + (dd > 0 ? $" +{dd}d" : "");
        }
        float dueMin = j?.DueMin ?? -1f;
        bool late = dueMin >= 0f && simMin >= 0f && simMin > dueMin;
        float? spare = dueMin >= 0f && simMin >= 0f ? dueMin - simMin : null;
        float avgNeeded = dueMin > 0f && remGameKm > 0f ? remGameKm / (dueMin / 60f) : -1f;
        float fr = dt.FuelCapL > 0f ? dt.FuelL / dt.FuelCapL : -1f;
        float needL = remGameKm * dt.ConsumptionLPerKm, atArrival = dt.FuelL - needL;
        int planned = j?.PlannedKm ?? 0;
        float progress = planned > 0 && remGameKm > 0f ? Math.Clamp(1f - remGameKm / planned, 0f, 1f) : 0f;
        string market = dt.JobMarket.Replace('_', ' ').ToUpperInvariant(); if (market.Length == 0) market = "-";

        // ---- header strip (y 0..92)
        Box(0, 0, W0, 92, cardBg, edge, 8);
        Txt("TRIP DETAILS", 14, 29, 13, titleB, FontWeights.SemiBold);
        Chip(j != null ? "ON JOB" : "NO JOB", 112, 51, j != null ? cyan : Ui.Frozen(Color.FromRgb(120, 130, 145)), 8);
        var reset = Chip("RESET TRIP", 14, 70, Ui.Frozen(Color.FromRgb(36, 92, 160)), 7.5, white);
        Canvas.SetLeft(reset, 14);
        reset.Cursor = System.Windows.Input.Cursors.Hand;
        reset.MouseLeftButtonUp += (_, _) => { ResetTrip(); UpdateTrip(); };

        Txt("FROM", 150, 45, 8, labelB, FontWeights.Normal);
        Txt(j?.SrcCity is { Length: > 0 } fc ? fc : "-", 182, 45, 24, white, FontWeights.Bold, 170);
        Txt(j?.SrcCompany ?? "", 182, 65, 11, labelB, FontWeights.Normal, 180);

        Txt("ROUTE PROGRESS", 372, 22, 8.5, titleB, FontWeights.SemiBold);
        Box(372, 29, 268, 20, Ui.Frozen(Color.FromRgb(34, 44, 58)), null, 4);
        if (progress > 0.005f) Box(372, 29, 268 * progress, 20, cyan, null, 4);
        Txt(Units.LongText(remGameKm) + " left", 372, 71, 12, white, FontWeights.Bold);
        if (planned > 0) Txt($"of {Units.LongText(planned)}  ·  {progress * 100f:0}%", 640, 71, 10, subB, FontWeights.Normal, 160, right: true);

        Txt("TO", 668, 45, 8, labelB, FontWeights.Normal);
        Txt(j?.DestCity is { Length: > 0 } tc ? tc : (hasDest ? destName : "-"), 686, 45, 24, white, FontWeights.Bold, 165);
        Txt(j?.DestCompany ?? "", 686, 65, 11, labelB, FontWeights.Normal, 165);

        Txt("LOCAL", 860, 31, 8, labelB, FontWeights.Normal);
        Txt(Games.Current.Short, 860, 61, 8, labelB, FontWeights.Normal);
        Txt(DateTime.Now.ToString("HH:mm:ss", inv), 988, 33, 15, white, FontWeights.Bold, 100, right: true);
        Txt(ClockText(gameNow).ToUpperInvariant(), 988, 63, 15, simClock, FontWeights.Bold, 100, right: true);

        // ---- row 1: five headline cards (y 102..332)
        const double gap = 8;
        double cw = (W0 - gap * 4) / 5;
        void DashCard(int i, string title, string big, Brush bigColor, string bigSub, string badge, Brush badgeColor, params (string K, string V, Brush? C)[] rows)
        {
            double x = i * (cw + gap), y = 102;
            Box(x, y, cw, 230, cardBg, edge, 8);
            double badgeW = badge.Length > 0 ? Chip(badge, x + cw - 10, y + 15, badgeColor, 6.5).DesiredSize.Width + 6 : 0;
            Txt(title, x + 12, y + 18, 8.5, titleB, FontWeights.SemiBold, cw - 22 - badgeW);
            double maxBig = cw - 24 - (bigSub.Length > 0 ? 64 : 0), sz = 36;
            var bt = Txt(big, x + 12, y + 64, sz, bigColor, FontWeights.Bold);
            bt.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            while (bt.DesiredSize.Width > maxBig && sz > 14) { sz -= 1; bt.FontSize = sz; bt.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); }
            Canvas.SetTop(bt, y + 64 - sz * 1.02);
            if (bigSub.Length > 0) Txt(bigSub, x + cw - 12, y + 62, 10.5, subB, FontWeights.Normal, 80, right: true);
            double ry = y + 104;
            foreach (var (k, v, c) in rows.Take(4)) { Row(k, v, c, x + 12, ry, cw - 24, 8, 12); ry += 30; }
        }
        float cargoDmg = dt.CargoDamage;
        DashCard(0, "CARGO", j?.Cargo is { Length: > 0 } cg ? cg : "-", white, "", "", green,
            ("Mass", j != null && j.MassKg > 0 ? Units.Mass(j.MassKg) : "-", null),
            ("Units", (j?.Units ?? 0).ToString(inv), null),
            ("Cargo damage", $"{cargoDmg:0}%", cargoDmg < 0.5f ? green : DamageBrush(cargoDmg)),
            ("Trailer", dt.TrailerName.Length > 0 ? dt.TrailerName : "-", null));
        DashCard(1, $"ARRIVAL ({Games.Current.Short} CLOCK)", arrBig, white, arrSub, "", green,
            ("ETA (your clock)", etaYours, null),
            ("Time left (game)", simMin >= 0f ? DurShort(simMin) : "-", null),
            ("Time left (real)", realMin >= 0f ? DurShort(realMin) : "-", null),
            ("Distance left", Units.LongText(remGameKm), null));
        DashCard(2, "TIME REMAINING TO DEADLINE", dueMin >= 0f ? DurShort(dueMin) : "-", white, "",
            dueMin < 0f ? "" : late ? "LATE" : "ON TIME", late ? Red : green,
            ("Deadline", dueMin >= 0f && gameNow >= 0f ? ClockText(gameNow + dueMin).ToUpperInvariant() : "-", null),
            ("ETA", arrAbs >= 0f ? ClockText(arrAbs).ToUpperInvariant() : "-", null),
            ("Margin", spare is float sp ? (sp >= 0f ? "+" : "-") + DurShort(sp) : "-", spare < 0f ? Red : green),
            ("Avg speed needed", avgNeeded >= 0f ? Units.SpeedText(avgNeeded) : "-", null));
        DashCard(3, "FUEL FOR THIS TRIP", fr >= 0f ? $"{fr * 100f:0}%" : "-", fr < 0.12f ? Red : fr < 0.25f ? Amber : green,
            $"{Fmt(Units.Fuel(dt.FuelL))} / {Units.FuelText(dt.FuelCapL)}", remGameKm <= 0f ? "" : atArrival < 0f ? "REFUEL NEEDED" : "ENOUGH FUEL", atArrival < 0f ? Red : green,
            ("Range", Units.LongText(dt.RangeKm), null),
            ("Consumption", Units.Consumption(dt.ConsumptionLPerKm), null),
            ("Needed for trip", Units.FuelText(needL), null),
            ("Fuel at arrival", Units.FuelText(atArrival), atArrival < 0f ? Red : null));
        DashCard(4, "JOB & INCOME", j != null && j.Income > 0 ? Units.Money + " " + Fmt(j.Income) : "-", white, "", "", green,
            ("Planned distance", planned > 0 ? Units.LongText(planned) : "-", null),
            ("Income rate", j != null && planned > 0 && j.Income > 0 ? Units.Money + " " + Fmt((float)j.Income / Units.Long(planned), "0.00") + "/" + Units.DistUnit : "-", null),
            ("Job market", market, null),
            ("Special job", market.Contains("SPECIAL") ? "YES" : "NO", null));

        // ---- row 2: detail cards (y 342..562)
        int n2 = Math.Clamp(row2.Count, 1, 5);
        double cw2 = (W0 - gap * (n2 - 1)) / n2;
        for (int i = 0; i < Math.Min(5, row2.Count); i++)
        {
            var (title, rows) = row2[i];
            double x = i * (cw2 + gap), y = 342;
            Box(x, y, cw2, 220, cardBg, edge, 8);
            Txt(title, x + 12, y + 17, 8.5, titleB, FontWeights.SemiBold, cw2 - 20);
            var rs = rows.Take(7).ToArray();
            double step = Math.Min(27, (220 - 40) / (double)Math.Max(1, rs.Length)), ry = y + 42;
            foreach (var (k, v, c) in rs) { Row(k, v, c, x + 12, ry, cw2 - 24, 8, 11); ry += step; }
        }

        tripPage.Child = new Viewbox
        {
            Child = cv, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(TripMenuW + 6, 6, 6, 6),
        };
    }

    /// <summary>
    /// The job info page always fills the window without scrolling, as large as possible: the cards are laid out in the number of columns
    /// (1 to 4) that lets the whole page be drawn biggest in the space there is, and the page is then scaled to fit it exactly.
    /// </summary>
    void ShowTripPage(StackPanel page, WrapPanel? wrap)
    {
        const double menuW = TripMenuW;
        double availW = Math.Max(200, (tripPage.ActualWidth > 0 ? tripPage.ActualWidth : DesignW) - menuW - 12);
        double availH = Math.Max(150, (tripPage.ActualHeight > 0 ? tripPage.ActualHeight : DesignH) - 12);
        if (wrap != null)
        {
            int best = 1; double bestScale = 0;
            for (int cols = 1; cols <= 4; cols++)
            {
                wrap.Width = cols * (380 + 10);
                wrap.InvalidateMeasure(); page.InvalidateMeasure();   // a direct Measure would otherwise reuse the first result
                page.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var d = page.DesiredSize;
                double sc = Math.Min(availW / d.Width, availH / d.Height);
                if (sc > bestScale + 0.001) { bestScale = sc; best = cols; }
            }
            wrap.Width = best * (380 + 10);
        }
        tripPage.Child = new Viewbox
        {
            Child = page, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(menuW + 6, 6, 6, 6),
        };
    }

    void ShowElevation()
    {
        int n = elev.Alt.Count;
        string summary = n < 2 ? "No data yet" : $"Now {Units.AltText(truck?.Y ?? 0)}  ·  climbed {Units.AltText(elev.Ascent())}  ·  descended {Units.AltText(elev.Descent())}  ·  range {Units.Alt(elev.Alt.Min()):0} to {Units.AltText(elev.Alt.Max())}";
        var sp = new StackPanel { Margin = new Thickness(18, 6, 18, 0) };
        sp.Children.Add(new TextBlock { Text = summary, FontSize = 14, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new ElevationChart(elev, distScale));
        Dlg.Custom(this, "Trip elevation", sp, new (string, Action?, bool)[] { ("Reset trip", () => elev.Clear(), false), ("Close", null, true) }, 640);
    }

    void ShowSettings()
    {
        var sp = new StackPanel { Margin = new Thickness(18, 4, 18, 0) };
        TextBlock H(string t) => new() { Text = t, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 12, 0, 4) };
        TextBox Box(string v) => new() { Text = v, FontSize = 14, Padding = new Thickness(5), Background = Ui.Frozen(Color.FromRgb(16, 21, 28)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = Brushes.Gray };

        sp.Children.Add(H("Route type"));
        var modeButtons = new List<(RadioButton, RouteMode)>();
        foreach (var md in RouteMode.All)
        {
            var rb = new RadioButton { Content = new TextBlock { Text = md.Help, TextWrapping = TextWrapping.Wrap }, IsChecked = md == routeMode, Foreground = Brushes.White, Margin = new Thickness(0, 3, 0, 3), GroupName = "mode" };
            modeButtons.Add((rb, md)); sp.Children.Add(rb);
        }
        var ferries = new CheckBox { Content = new TextBlock { Text = "Avoid ferries and car trains (routes never cross by ferry; some places may become unreachable)", TextWrapping = TextWrapping.Wrap }, IsChecked = cfg.AvoidFerries, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 4) };
        sp.Children.Add(ferries);
        var clear = Ui.Button($"Clear avoided roads ({cfg.GameAvoid.Count})", double.NaN, 32); clear.Padding = new Thickness(12, 0, 12, 0); clear.HorizontalAlignment = HorizontalAlignment.Left;
        clear.Click += (_, _) => { ClearAvoid(); clear.Content = "Clear avoided roads (0)"; };
        sp.Children.Add(clear);

        sp.Children.Add(H("Window"));
        var pin = new CheckBox { Content = "Keep this window on top of other apps", IsChecked = Topmost, Foreground = Brushes.White };
        sp.Children.Add(pin);
        var opLabel = new TextBlock { Text = "Opacity " + Math.Round(Opacity * 100) + "%", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) };
        var op = new Slider { Minimum = 30, Maximum = 100, Value = Math.Round(Opacity * 100), Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        op.ValueChanged += (_, e) => { opLabel.Text = "Opacity " + (int)e.NewValue + "%"; Opacity = e.NewValue / 100.0; };
        sp.Children.Add(opLabel); sp.Children.Add(op);

        sp.Children.Add(H("Lane guidance"));
        var showLanes = new CheckBox { Content = new TextBlock { Text = "Show lane guidance (the lane panel on the map and in the lane overlay window)", TextWrapping = TextWrapping.Wrap }, IsChecked = cfg.ShowLanes, Foreground = Brushes.White };
        sp.Children.Add(showLanes);
        sp.Children.Add(H("Game GPS destination"));
        var gameGps = new CheckBox { Content = new TextBlock { Text = "When there is no job, offer to navigate to where the game's own GPS is set (read from your newest save after the game autosaves, or after a quick save). Saves are only read, never changed.", TextWrapping = TextWrapping.Wrap }, IsChecked = cfg.FollowGameGps, Foreground = Brushes.White };
        sp.Children.Add(gameGps);
        var autoSave = new CheckBox { Content = new TextBlock { Text = "Quick-save automatically when the game's GPS gets a new destination and no job has started (for example a job with a pickup elsewhere), so the app sees it within seconds. " +
            "It presses your quick-save key once, only while the game is in front; the game's quick-save slot is replaced.", TextWrapping = TextWrapping.Wrap }, IsChecked = cfg.AutoQuickSave, Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0) };
        sp.Children.Add(autoSave);
        sp.Children.Add(H("Lane overlay window"));
        var ovAuto = new CheckBox { Content = new TextBlock { Text = "Start the lane overlay when this app starts (if it is not already running)", TextWrapping = TextWrapping.Wrap }, IsChecked = cfg.OverlayAutoStart, Foreground = Brushes.White };
        sp.Children.Add(ovAuto);
        sp.Children.Add(new TextBlock { Text = "Path to Ets2LaneOverlay.exe (empty = look in the usual places)", Foreground = Ui.Dim, Margin = new Thickness(0, 6, 0, 0) });
        var ovPath = Box(cfg.OverlayPath); sp.Children.Add(ovPath);
        var ovNow = Ui.Button("Start overlay now", double.NaN, 32); ovNow.Padding = new Thickness(12, 0, 12, 0); ovNow.HorizontalAlignment = HorizontalAlignment.Left; ovNow.Margin = new Thickness(0, 8, 0, 0);
        ovNow.Click += (_, _) =>
        {
            var keep = cfg.OverlayPath; cfg.OverlayPath = ovPath.Text.Trim();
            Toast(OverlayLauncher.EnsureRunning(cfg) ?? "Lane overlay is already running");
            if (OverlayLauncher.Find(cfg) == null) cfg.OverlayPath = keep;
        };
        sp.Children.Add(ovNow);

        sp.Children.Add(H("Voice (this PC's speakers / headset)"));
        var voiceOn = new CheckBox { Content = "Voice guidance", IsChecked = voice.Enabled, Foreground = Brushes.White };
        sp.Children.Add(voiceOn);
        var voices = voice.Voices().ToList();
        if (cfg.VoiceName.Length > 0 && !voices.Contains(cfg.VoiceName)) voices.Insert(0, cfg.VoiceName);   // e.g. a Voicebox voice while Voicebox is closed
        var combo = new ComboBox { Margin = new Thickness(0, 6, 0, 0), MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        combo.Items.Add("(default voice)");
        foreach (var v in voices) combo.Items.Add(v);
        combo.SelectedIndex = Math.Max(0, voices.IndexOf(cfg.VoiceName) + 1);
        sp.Children.Add(combo);
        var rateLabel = new TextBlock { Text = "Speed " + cfg.VoiceRate, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) };
        var rate = new Slider { Minimum = -5, Maximum = 5, Value = cfg.VoiceRate, Width = 260, TickFrequency = 1, IsSnapToTickEnabled = true, HorizontalAlignment = HorizontalAlignment.Left };
        rate.ValueChanged += (_, e) => rateLabel.Text = "Speed " + (int)e.NewValue;
        sp.Children.Add(rateLabel); sp.Children.Add(rate);
        var test = Ui.Button("Test voice", double.NaN, 32); test.Padding = new Thickness(12, 0, 12, 0); test.HorizontalAlignment = HorizontalAlignment.Left; test.Margin = new Thickness(0, 8, 0, 0);
        test.Click += (_, _) =>
        {
            bool was = voice.Enabled; voice.Enabled = true;
            voice.Configure((int)rate.Value, combo.SelectedIndex > 0 ? voices[combo.SelectedIndex - 1] : "");
            voice.Say("Voice guidance is working."); voice.Enabled = was;
        };
        sp.Children.Add(test);

        sp.Children.Add(H("Voice control (microphone)"));
        var vcOn = new CheckBox { Content = new TextBlock { Text = "Listen for spoken commands while the game has focus. Say the wake word, then a command, e.g. \"GPS, nearest fuel station\", \"GPS, take the job\", \"GPS, help\". It uses this PC's default microphone; recognition happens on this PC and nothing is recorded or sent.", TextWrapping = TextWrapping.Wrap }, IsChecked = cfg.VoiceControl, Foreground = Brushes.White };
        sp.Children.Add(vcOn);
        sp.Children.Add(new TextBlock { Text = "Wake word (one or two simple words, default GPS)", Foreground = Ui.Dim, Margin = new Thickness(0, 6, 0, 0) });
        var vcWake = Box(cfg.VoiceWake); vcWake.Width = 160; vcWake.HorizontalAlignment = HorizontalAlignment.Left; sp.Children.Add(vcWake);
        sp.Children.Add(new TextBlock { Text = "Commands: nearest fuel station / rest area / service centre / weigh station / garage / truck dealer, take the job (or not now when a new job is offered), follow the game route, show job info, show the map, cancel the route, back to my route, how far, when do I arrive, what is next, speed limit, fuel level, lane guidance / cruise assist / voice guidance on or off.", Foreground = Ui.Dim, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });

        sp.Children.Add(H("Updates"));
        var updAuto = new CheckBox { Content = new TextBlock { Text = "Look for new versions automatically (about once a day; nothing is downloaded or installed)", TextWrapping = TextWrapping.Wrap }, IsChecked = cfg.CheckUpdates, Foreground = Brushes.White };
        sp.Children.Add(updAuto);
        var updText = new TextBlock { Text = UpdateStatusText(), Foreground = Ui.Dim, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        sp.Children.Add(updText);
        var updRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var updNow = Ui.Button("Check for updates now", double.NaN, 32); updNow.Padding = new Thickness(12, 0, 12, 0);
        var updOpen = Ui.Button("Open the download page", double.NaN, 32); updOpen.Padding = new Thickness(12, 0, 12, 0); updOpen.Margin = new Thickness(8, 0, 0, 0);
        var updSkip = Ui.Button("Skip this version", double.NaN, 32); updSkip.Padding = new Thickness(12, 0, 12, 0); updSkip.Margin = new Thickness(8, 0, 0, 0);
        updRow.Children.Add(updNow); updRow.Children.Add(updOpen); updRow.Children.Add(updSkip);
        sp.Children.Add(updRow);
        void ShowUpdate() { updText.Text = UpdateStatusText(); var vis = update != null ? Visibility.Visible : Visibility.Collapsed; updOpen.Visibility = vis; updSkip.Visibility = vis; }
        ShowUpdate();
        updNow.Click += async (_, _) =>
        {
            updNow.IsEnabled = false; updText.Text = "Checking...";
            try
            {
                var u = await UpdateCheck.CheckAsync();
                cfg.LastUpdateCheckUtc = DateTime.UtcNow.Ticks; cfg.SkippedVersion = ""; cfg.Save();
                SetUpdate(u, announce: false);
                ShowUpdate();
            }
            catch (Exception e) { updText.Text = "Could not reach GitHub: " + e.Message; }
            updNow.IsEnabled = true;
        };
        updOpen.Click += (_, _) => { if (update != null) UpdateCheck.OpenPage(update.PageUrl); };
        updSkip.Click += (_, _) => { if (update != null) { cfg.SkippedVersion = update.Latest.ToString(3); cfg.Save(); SetUpdate(update, announce: false); ShowUpdate(); } };

        sp.Children.Add(H("Connections"));
        sp.Children.Add(new TextBlock { Text = "Telemetry server (TruckSim GPS) address", Foreground = Ui.Dim });
        var tele = Box(cfg.TelemetryHost); sp.Children.Add(tele);
        var srv = new CheckBox { Content = new TextBlock { Text = "Built-in web server (lets the tablet app, a browser and the lane overlay window use this PC)", TextWrapping = TextWrapping.Wrap }, IsChecked = cfg.ServerEnabled, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) };
        sp.Children.Add(srv);
        sp.Children.Add(new TextBlock { Text = "Server port (changes apply after restarting the app)", Foreground = Ui.Dim, Margin = new Thickness(0, 6, 0, 0) });
        var port = Box(cfg.ServerPort.ToString(CultureInfo.InvariantCulture)); port.Width = 120; port.HorizontalAlignment = HorizontalAlignment.Left; sp.Children.Add(port);
        sp.Children.Add(H("Distances"));
        var realD = new CheckBox { Content = new TextBlock { Text = "Real distances (turns, lane guidance, route length). Off = Sim distances like the game shows", TextWrapping = TextWrapping.Wrap }, IsChecked = cfg.RealDistances, Foreground = Brushes.White };
        sp.Children.Add(realD);
        sp.Children.Add(new TextBlock { Text = "Sim scale (the game shows map metres x19)", Foreground = Ui.Dim, Margin = new Thickness(0, 6, 0, 0) });
        var scale = Box(cfg.GameDistScale.ToString(CultureInfo.InvariantCulture)); scale.Width = 120; scale.HorizontalAlignment = HorizontalAlignment.Left; sp.Children.Add(scale);

        void Save(bool reloadMap)
        {
            var md = modeButtons.FirstOrDefault(b => b.Item1.IsChecked == true).Item2;
            bool ferryChanged = (ferries.IsChecked == true) != cfg.AvoidFerries;
            if (ferryChanged) SetAvoidFerries(ferries.IsChecked == true, md == null || md == routeMode);
            if (md != null && md != routeMode) SetRouteMode(md);
            Topmost = pin.IsChecked == true; cfg.Topmost = Topmost; UpdatePinButton();
            cfg.Opacity = Opacity;
            cfg.CheckUpdates = updAuto.IsChecked == true;
            bool vcChanged = (vcOn.IsChecked == true) != cfg.VoiceControl || vcWake.Text.Trim() != cfg.VoiceWake;
            cfg.VoiceControl = vcOn.IsChecked == true; cfg.VoiceWake = vcWake.Text.Trim().Length > 0 ? vcWake.Text.Trim() : "GPS";
            cfg.ShowLanes = showLanes.IsChecked == true; if (!cfg.ShowLanes) SetLaneUi(null);
            cfg.FollowGameGps = gameGps.IsChecked == true;
            cfg.AutoQuickSave = autoSave.IsChecked == true;
            cfg.OverlayAutoStart = ovAuto.IsChecked == true; cfg.OverlayPath = ovPath.Text.Trim();
            voice.Enabled = voiceOn.IsChecked == true; cfg.VoiceOn = voice.Enabled; UpdateVoiceButton();
            cfg.VoiceName = combo.SelectedIndex > 0 ? voices[combo.SelectedIndex - 1] : ""; cfg.VoiceRate = (int)rate.Value;
            voice.Configure(cfg.VoiceRate, cfg.VoiceName);
            cfg.TelemetryHost = tele.Text.Trim();
            cfg.ServerEnabled = srv.IsChecked == true;
            if (int.TryParse(port.Text, out var pt) && pt > 1023 && pt < 65536) cfg.ServerPort = pt;
            if (float.TryParse(scale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) && sc > 0) cfg.GameDistScale = sc;
            cfg.RealDistances = realD.IsChecked == true;
            distScale = cfg.RealDistances ? 1f : cfg.GameDistScale; if (nav != null) nav.DistScale = distScale;
            cfg.Save();
            if (vcChanged) ApplyVoiceControl(announce: true);
            if (reloadMap) { try { File.Delete(MapBuilder.MapService.MetaPath); } catch { /* ignore */ } _ = LoadMapAsync(); }
        }
        double opBefore = Opacity;
        Dlg.Custom(this, "Settings", new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(300, ActualHeight - 160) },
            new (string, Action?, bool)[] { ("Cancel", () => Opacity = opBefore, false), ("Save + rebuild map", () => Save(true), false), ("Save", () => Save(false), true) }, 600);
    }
}
