using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Ets2Gps;

/// <summary>
/// Heading-up road map: roads, route, waypoints, POIs, next-maneuver marker and the truck.
/// Drag to look around (Recentre returns), wheel to zoom, right-click a point for the point menu.
/// </summary>
public sealed class MapControl : FrameworkElement
{
    public MapData? Map;
    public float TruckX, TruckZ, Bearing;       // Bearing: radians, clockwise from north
    public bool HasFix;
    public bool FlagOn; public float FlagX, FlagZ;   // the finishing flag at the destination
    public bool FlagLink;   // the flag stands beyond the end of the mapped roads (a drop-off spot in a yard): a dashed line joins them
    static readonly Pen FlagLinkPen = MakeFlagLinkPen();
    static Pen MakeFlagLinkPen() { var p = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 3) { DashStyle = new DashStyle(new double[] { 2, 1.5 }, 0), DashCap = PenLineCap.Round }; p.Freeze(); return p; }
    public float[] RoutePts = Array.Empty<float>();   // x,z pairs
    public int[] RouteEdgePt = Array.Empty<int>();    // pair index where each route edge starts
    public byte[] RouteLift = Array.Empty<byte>();    // per route point: height above the road it crosses (0.5 m units)
    public int RouteIdx;
    public List<(float X, float Z)> Stops = new();
    public Maneuver? NextManeuver;
    public HashSet<int> ShowPoiTypes = new() { Poi.Fuel, Poi.Parking, Poi.Service, Poi.Weigh, Poi.Garage, Poi.Dealer, Poi.Ferry, Poi.Train };
    /// <summary>The zoom the map opens at and returns to on a double-click: four + button steps (x1.3 each) in from the old default of 1.</summary>
    public const float DefaultZoom = 2.8561f;
    public float UserZoom = DefaultZoom;
    public bool Free { get; private set; }
    public event Action<bool>? FreeChanged;
    public event Action<float, float>? PointRequested;

    float freeCx, freeCz, freeBearing;
    public float ViewBearing => Free ? freeBearing : Bearing;

    static readonly float[] WidthsM = { 7f, 11f, 18f, 8f };
    // road atlas look: steel-blue roads with a darker outline on a slate background
    static readonly Brush[] ClassBrush =
    {
        F(Color.FromRgb(70, 96, 128)), F(Color.FromRgb(84, 114, 150)), F(Color.FromRgb(236, 190, 90)), F(Color.FromRgb(64, 88, 118)),
    };
    static readonly Brush[] CasingBrushes =
    {
        F(Color.FromRgb(26, 32, 44)), F(Color.FromRgb(26, 32, 44)), F(Color.FromRgb(120, 84, 24)), F(Color.FromRgb(26, 32, 44)),
    };
    static readonly Brush[] AreaBrush =
    {
        F(Color.FromRgb(52, 64, 84)), F(Color.FromRgb(226, 190, 136)), F(Color.FromRgb(212, 160, 92)), F(Color.FromRgb(112, 160, 104)),
    };
    static readonly Brush ShieldRed = F(Color.FromRgb(190, 30, 45)), ShieldDark = F(Color.FromRgb(20, 20, 20));
    static readonly Brush ShieldGreen = F(Color.FromRgb(0, 128, 72)), ShieldBlue = F(Color.FromRgb(0, 64, 176)), PillBrush = F(Color.FromArgb(235, 250, 250, 252)), PillText = F(Color.FromRgb(30, 36, 48));
    static readonly Brush Bg = F(Color.FromRgb(34, 40, 54));
    static readonly Brush RouteBrush = F(Color.FromRgb(36, 132, 255)), CasingBrush = F(Color.FromArgb(235, 255, 255, 255));
    static readonly Brush CityBrush = F(Color.FromArgb(230, 220, 226, 236));
    static readonly Brush MsgBrush = F(Color.FromArgb(220, 220, 226, 236));
    // point-of-interest badges: a coloured rounded square with a white pictogram (see PoiIcons)
    static readonly Dictionary<int, Geometry> PoiGeo = PoiIcons.Paths.ToDictionary(kv => kv.Key, kv => { var g = Geometry.Parse(kv.Value); g.Freeze(); return g; });
    static readonly Dictionary<int, Brush> PoiFill = PoiIcons.Colors.ToDictionary(kv => kv.Key, kv => F(Color.FromRgb(kv.Value.R, kv.Value.G, kv.Value.B)));
    // ferry and car-train crossings: a dashed sea line, not a road
    static readonly Pen FerryPen = MakeFerryPen();
    static Pen MakeFerryPen() { var p = new Pen(new SolidColorBrush(Color.FromArgb(170, 90, 160, 255)), 2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }; p.Freeze(); return p; }
    readonly List<int> ferryEdges = new();
    static readonly Pen PoiEdge = new(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 2.2);
    static readonly Brush PoiShadow = F(Color.FromArgb(90, 0, 0, 0));

    void DrawPoi(DrawingContext dc, int type, double x, double y, double r)
    {
        var box = new Rect(x - r, y - r, 2 * r, 2 * r);
        dc.DrawRoundedRectangle(PoiShadow, null, new Rect(box.X, box.Y + r * 0.14, box.Width, box.Height), r * 0.42, r * 0.42);
        dc.DrawRoundedRectangle(PoiFill.GetValueOrDefault(type, Brushes.Gray), PoiEdge, box, r * 0.42, r * 0.42);
        if (!PoiGeo.TryGetValue(type, out var geo)) return;
        double k = 2 * r * 0.66 / 24;
        dc.PushTransform(new TranslateTransform(x - 12 * k, y - 12 * k));
        dc.PushTransform(new ScaleTransform(k, k));
        dc.DrawGeometry(Brushes.White, null, geo);
        dc.Pop(); dc.Pop();
    }
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    static readonly Typeface FaceNormal = new("Segoe UI");
    static Brush F(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>
    /// The game's road pieces do not meet exactly: a road ends on its centre line while the junction piece after it starts on the lane, a few metres to the side. Drawn as is, the route steps sideways at every join. Both sides of a join are eased towards its middle over a short stretch (only points move, so indices into the line stay valid).
    /// </summary>
    public static void SmoothJoins(float[] pts, int[] starts)
    {
        var o = (float[])pts.Clone();
        int n = pts.Length / 2;
        for (int i = 1; i < starts.Length; i++)
        {
            int s = starts[i], e = s - 1, a0 = starts[i - 1], b1 = i + 1 < starts.Length ? starts[i + 1] - 1 : n - 1;
            if (e < a0 || s > b1) continue;
            float gx = o[s * 2] - o[e * 2], gz = o[s * 2 + 1] - o[e * 2 + 1], g = MathF.Sqrt(gx * gx + gz * gz);
            if (g < 0.3f || g > 25f) continue;   // joined already, or a real gap (ferry)
            float len = Math.Clamp(g * 4f, 8f, 40f);
            void Ease(int from, int to, int step, float sign)
            {
                float total = 0f;
                for (int k = from; k != to; k += step) total += Seg(k, k + step);
                float l = MathF.Min(len, total / 2f), d = 0f;
                for (int k = from; ; k += step)
                {
                    float t = 1f - d / MathF.Max(l, 0.01f);
                    if (t <= 0f) break;
                    pts[k * 2] += sign * gx / 2f * t; pts[k * 2 + 1] += sign * gz / 2f * t;
                    if (k == to) break;
                    d += Seg(k, k + step);
                }
            }
            float Seg(int p, int q) => MathF.Sqrt((o[q * 2] - o[p * 2]) * (o[q * 2] - o[p * 2]) + (o[q * 2 + 1] - o[p * 2 + 1]) * (o[q * 2 + 1] - o[p * 2 + 1]));
            Ease(e, a0, -1, 1f);    // the end of the piece before moves half way towards the next
            Ease(s, b1, 1, -1f);    // the start of the next piece moves half way back
        }
        SmoothLine(pts); SmoothLine(pts);
    }

    /// <summary>
    /// Inside junction pieces the route still hops from lane to lane in short sideways steps. Each point is replaced by the average of the line
    /// around it (a triangle-weighted window along the line, up to 60 m each way; done twice). Where the road really bends the window shrinks, so a bend
    /// is never cut by more than about half a metre; the ends of the line and real gaps (ferries) stay where they are.
    /// </summary>
    static void SmoothLine(float[] pts)
    {
        int n = pts.Length / 2;
        var o = (float[])pts.Clone();
        var s = new double[n];
        for (int i = 1; i < n; i++) s[i] = s[i - 1] + MathF.Sqrt((o[i * 2] - o[i * 2 - 2]) * (o[i * 2] - o[i * 2 - 2]) + (o[i * 2 + 1] - o[i * 2 - 1]) * (o[i * 2 + 1] - o[i * 2 - 1]));
        for (int a = 0; a < n;)
        {
            int b = a;   // one run of the line without a gap
            while (b + 1 < n && s[b + 1] - s[b] <= 25) b++;
            (double X, double Z) At(double t)   // the point at distance t along the run
            {
                t = Math.Clamp(t, s[a], s[b]);
                int lo = a, hi = b;
                while (hi - lo > 1) { int m = (lo + hi) >> 1; if (s[m] <= t) lo = m; else hi = m; }
                double f = s[hi] > s[lo] ? (t - s[lo]) / (s[hi] - s[lo]) : 0;
                return (o[lo * 2] + (o[hi * 2] - o[lo * 2]) * f, o[lo * 2 + 1] + (o[hi * 2 + 1] - o[lo * 2 + 1]) * f);
            }
            for (int i = a + 1; i < b; i++)
            {
                double t = s[i];
                // how sharply the road bends here: heading 15-60 m before against 15-60 m after
                var (p0, p1, p2, p3) = (At(t - 60), At(t - 15), At(t + 15), At(t + 60));
                double turn = Math.Abs(Math.Atan2(p3.Z - p2.Z, p3.X - p2.X) - Math.Atan2(p1.Z - p0.Z, p1.X - p0.X));
                if (turn > Math.PI) turn = 2 * Math.PI - turn;
                double radius = 75 / Math.Max(turn, 1e-3);
                double w = Math.Min(Math.Min(60, Math.Sqrt(12 * radius * 0.5)), Math.Min(t - s[a], s[b] - t));   // a bend of radius r is cut by about w*w/(12r)
                if (w < 1) continue;
                double sx = 0, sz = 0, sw = 0;
                for (int k = -10; k <= 10; k++)
                {
                    var q = At(t + w * k / 10); double wt = 11 - Math.Abs(k);
                    sx += q.X * wt; sz += q.Z * wt; sw += wt;
                }
                pts[i * 2] = (float)(sx / sw); pts[i * 2 + 1] = (float)(sz / sw);
            }
            a = b + 1;
        }
    }

    readonly List<int>[] buckets = { new(), new(), new(), new() };
    readonly List<int>[] elevBuckets = { new(), new(), new(), new() };
    static readonly Brush DeckShadow = F(Color.FromArgb(105, 6, 8, 14)), RouteGhost = F(Color.FromArgb(120, 36, 132, 255));
    readonly List<Point> pts = new();
    readonly Dictionary<string, FormattedText> textCache = new();

    public MapControl() { ClipToBounds = true; Focusable = false; }

    // ------------------------------------------------------------------ projection / view --

    double PxPerM => UserZoom * 0.55;
    double PivotX => ActualWidth * 0.5;
    double PivotY => ActualWidth > ActualHeight ? ActualHeight * 0.68 : ActualHeight * 0.72;

    public void ZoomBy(float f) { UserZoom = Math.Clamp(UserZoom * f, PlanMode ? 0.004f : 0.35f, 10f); InvalidateVisual(); }

    void EnterFree()
    {
        double s = PxPerM, ahead = -(ActualHeight / 2 - PivotY) / s;
        freeBearing = Bearing;
        freeCx = TruckX + (float)(ahead * Math.Sin(Bearing));
        freeCz = TruckZ - (float)(ahead * Math.Cos(Bearing));
        Free = true;
        FreeChanged?.Invoke(true);
    }

    public void Recentre()
    {
        if (!Free) return;
        Free = false;
        FreeChanged?.Invoke(false);
        InvalidateVisual();
    }

    (float X, float Z)? ToWorld(Point p)
    {
        if (!HasFix && !PlanMode) return null;
        double s = PxPerM, px = Free ? ActualWidth / 2 : PivotX, py = Free ? ActualHeight / 2 : PivotY;
        p = Unwarp(p, px, py, Ecam(ActualHeight, py));
        if (double.IsNaN(p.X)) return null;
        double b = ViewBearing, u = (p.X - px) / s, v = -(p.Y - py) / s;
        float cx = Free ? freeCx : TruckX, cz = Free ? freeCz : TruckZ;
        return ((float)(cx + u * Math.Cos(b) + v * Math.Sin(b)), (float)(cz + u * Math.Sin(b) - v * Math.Cos(b)));
    }

    // ------------------------------------------------------------------------------ mouse --

    bool dragging; Point dragLast, downPos; bool moved;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2 && !PlanMode) { UserZoom = DefaultZoom; InvalidateVisual(); return; }
        dragging = true; moved = false; downPos = dragLast = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging) return;
        var p = e.GetPosition(this);
        if (!moved && (p - downPos).Length < 6) return;
        moved = true;
        if ((!HasFix && !PlanMode) || Map == null) return;
        if (!Free) EnterFree();
        double s = PxPerM, b = freeBearing, u = -(p.X - dragLast.X) / s, v = (p.Y - dragLast.Y) / s;
        freeCx += (float)(u * Math.Cos(b) + v * Math.Sin(b));
        freeCz += (float)(u * Math.Sin(b) - v * Math.Cos(b));
        dragLast = p;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        dragging = false; ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var w = ToWorld(e.GetPosition(this));
        if (w != null) PointRequested?.Invoke(w.Value.X, w.Value.Z);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomBy(e.Delta > 0 ? 1.15f : 1f / 1.15f);
    }

    // ---------------------------------------------------------------------------- 3D tilt --

    const double TiltMax = 52 * Math.PI / 180;
    static readonly double[] BandFactor = { 1.0, 0.72, 0.55, 0.42, 0.3 };
    static int Band(double sc) => sc >= 0.8 ? 0 : sc >= 0.62 ? 1 : sc >= 0.48 ? 2 : sc >= 0.36 ? 3 : 4;
    double tilt; bool driving; long lastTick;

    /// <summary>Camera distance (px) that puts the horizon a little above the top edge when fully tilted.</summary>
    double Ecam(double H, double py) => (py + 0.12 * H) * Math.Tan(TiltMax);

    (double X, double Y, double Sc) Warp(double fx, double fy, double px, double py, double E)
    {
        if (tilt < 0.001) return (fx, fy, 1);
        double x0 = fx - px, y0 = py - fy, sT = Math.Sin(tilt), cT = Math.Cos(tilt);
        double lim = -E / sT * 0.8;
        if (y0 < lim) y0 = lim;
        double sc = E / (E + y0 * sT);
        return (px + x0 * sc, py - y0 * cT * sc, sc);
    }

    /// <summary>Screen point -> flat (untilted) screen point.</summary>
    Point Unwarp(Point p, double px, double py, double E)
    {
        if (tilt < 0.001) return p;
        double sT = Math.Sin(tilt), cT = Math.Cos(tilt), yp = py - p.Y, den = E * cT - yp * sT;
        if (den <= 1e-6) return new Point(double.NaN, double.NaN);
        double y0 = yp * E / den, sc = E / (E + y0 * sT);
        return new Point(px + (p.X - px) / sc, py - y0);
    }

    sealed class BandSet
    {
        readonly StreamGeometry?[] g = new StreamGeometry?[5];
        readonly StreamGeometryContext?[] c = new StreamGeometryContext?[5];
        public void Run(int band, List<Point> p)
        {
            if (p.Count < 2) return;
            if (c[band] == null) { g[band] = new StreamGeometry(); c[band] = g[band]!.Open(); }
            c[band]!.BeginFigure(p[0], false, false);
            c[band]!.PolyLineTo(p.GetRange(1, p.Count - 1), true, true);
        }
        public void Draw(DrawingContext dc, Brush brush, Func<int, double> width)
        {
            for (int b = 0; b < 5; b++)
            {
                if (g[b] == null) continue;
                c[b]!.Close(); g[b]!.Freeze();
                dc.DrawGeometry(null, new Pen(brush, width(b)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g[b]);
                g[b] = null; c[b] = null;
            }
        }
    }

    public MapAssets? Assets;

    // plan mode: the map shows a planned trip (north up, any zoom, no truck) instead of following the truck
    public bool PlanMode;
    public float[] PlanPts = Array.Empty<float>();
    public int[] PlanEdgePt = Array.Empty<int>();
    public byte[] PlanLift = Array.Empty<byte>();
    public List<(float X, float Z, string Label, Color C)> Pins = new();

    public void ShowPlan(float cx, float cz, float zoom)
    {
        PlanMode = true; Free = true; freeBearing = 0; freeCx = cx; freeCz = cz; UserZoom = zoom;
        InvalidateVisual();
    }

    public void HidePlan()
    {
        PlanMode = false; Free = false; Pins = new(); PlanPts = Array.Empty<float>(); PlanEdgePt = Array.Empty<int>(); PlanLift = Array.Empty<byte>(); UserZoom = DefaultZoom;
        InvalidateVisual();
    }
    public float SpeedKmh;

    // ---------------------------------------------------------------------------- drawing --

    FormattedText Text(string t, double size, Brush b, bool bold)
    {
        string key = t + "|" + size.ToString("0.#", CultureInfo.InvariantCulture) + "|" + (bold ? "b" : "n") + "|" + b.GetHashCode();
        if (!textCache.TryGetValue(key, out var ft))
        {
            if (textCache.Count > 4000) textCache.Clear();
            ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? Face : FaceNormal, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            textCache[key] = ft;
        }
        return ft;
    }

    void Center(DrawingContext dc, string t, double x, double y, double size, Brush b, bool bold)
    {
        var ft = Text(t, size, b, bold);
        dc.DrawText(ft, new Point(x - ft.Width / 2, y - ft.Height / 2));
    }

    static readonly Brush CityShadow = F(Color.FromArgb(150, 10, 12, 20)), CityText = F(Color.FromArgb(225, 236, 224, 228));
    static readonly Brush BorderRed = F(Color.FromRgb(200, 40, 40));

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        var m = Map;

        // tilt: eases in above 15 km/h while following the truck, back to flat when slow or looking around
        long nowT = Environment.TickCount64;
        double dtT = Math.Min(0.1, Math.Max(0, (nowT - lastTick) / 1000.0)); lastTick = nowT;
        if (SpeedKmh > 15f) driving = true; else if (SpeedKmh < 12f) driving = false;
        double target = driving && !Free && HasFix && m != null && !dragging && !PlanMode ? TiltMax : 0;
        if (Math.Abs(tilt - target) > 0.0005)
        {
            tilt += Math.Clamp(target - tilt, -dtT * 1.6, dtT * 1.6);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(InvalidateVisual));
        }
        bool tilted = tilt >= 0.001;

        dc.DrawRectangle(Bg, null, new Rect(0, 0, W, H));
        if (m == null) { Center(dc, "Loading the map…", W / 2, H / 2, 16, MsgBrush, false); return; }
        if (!HasFix && !PlanMode) { Center(dc, "Waiting for the game…", W / 2, H / 2, 16, MsgBrush, false); return; }
        double s = PxPerM; bool isFree = Free; double vb = ViewBearing;
        double px = isFree ? W / 2 : PivotX, py = isFree ? H / 2 : PivotY;
        double cs = Math.Cos(vb), sn = Math.Sin(vb);
        float cx = isFree ? freeCx : TruckX, cz = isFree ? freeCz : TruckZ;
        double E = Ecam(H, py), yMax = 3.2 * H;
        (double X, double Y, double Sc) P(float wx, float wz) { double a = wx - cx, b = wz - cz; return Warp(px + (a * cs + b * sn) * s, py - (a * sn - b * cs) * s, px, py, E); }
        float radius = (float)(Math.Max(Math.Max(px, W - px), Math.Max(py, H - py)) * 1.42 / s + 20);
        if (tilted) radius = Math.Max(radius, (float)(yMax / s * 1.05 + 20));
        double zoom = UserZoom;
        double Fs(double sc) => tilted ? Math.Clamp(Math.Pow(sc, 0.6), 0.5, 1.1) : 1.0;
        void Scaled(double x, double y, double f, Action draw)
        {
            if (Math.Abs(f - 1) < 0.02) { draw(); return; }
            dc.PushTransform(new ScaleTransform(f, f, x, y)); draw(); dc.Pop();
        }
        bool showLocal = zoom > 0.3, showJunction = zoom > 0.45;
        foreach (var b in buckets) b.Clear();
        ferryEdges.Clear();
        bool drawRoads = !(PlanMode && zoom < 0.06);   // zoomed far out while planning: just the route and the towns
        if (drawRoads) m.QueryEdges(cx, cz, radius, e =>
        {
            if (m.IsFerry(e)) { ferryEdges.Add(e); return; }
            int cls = Math.Min(3, (int)m.ECls[e]);
            if ((cls == 0 && !showLocal) || (cls == 3 && !showJunction)) return;
            if (m.EPtStart[e + 1] - m.EPtStart[e] < 2) return;
            buckets[cls].Add(e);
        });

        // lots, yards and green patches under the roads
        if (zoom > 0.4)
            foreach (var a in m.Areas)
            {
                if (a.MaxX < cx - radius || a.MinX > cx + radius || a.MaxZ < cz - radius || a.MinZ > cz + radius) continue;
                var g = new StreamGeometry();
                bool ok = true;
                using (var c = g.Open())
                {
                    pts.Clear();
                    for (int k = 0; k < a.X.Length; k++)
                    {
                        var q = P(a.X[k], a.Z[k]);
                        if (tilted && q.Sc < 0.28) { ok = false; break; }
                        pts.Add(new Point(q.X, q.Y));
                    }
                    if (ok) { c.BeginFigure(pts[0], true, true); c.PolyLineTo(pts.GetRange(1, pts.Count - 1), true, false); }
                }
                if (!ok) continue;
                g.Freeze();
                dc.DrawGeometry(AreaBrush[Math.Min(3, (int)a.Color)], null, g);
            }

        // roads: outline pass, then fill pass (per class, per depth band so far roads get thinner).
        // Roads on flyovers and bridges are drawn after the rest, raised and with a shadow, so the roads below pass underneath.
        foreach (var l in elevBuckets) l.Clear();
        bool hasLift = m.EHasLift.Length > 0;
        if (hasLift)
            for (int cls = 0; cls < 4; cls++)
            {
                var bl = buckets[cls]; int keep = 0;
                for (int i = 0; i < bl.Count; i++) { int e = bl[i]; if (m.EHasLift[e]) elevBuckets[cls].Add(e); else bl[keep++] = e; }
                bl.RemoveRange(keep, bl.Count - keep);
            }
        double liftK = tilted ? s * Math.Sin(tilt) : 0;   // screen px for one metre of height
        var ex = new double[260]; var ey = new double[260]; var es = new double[260];
        var run = new List<Point>();
        // mode 0: on the ground, 1: raised deck, 2: its shadow on the ground
        void EdgeRuns(BandSet set, int e, int mode)
        {
            int a = m.EPtStart[e], b = m.EPtStart[e + 1], n = Math.Min(b - a, 255);
            for (int i = 0; i < n; i++)
            {
                var q = P(m.PtX[a + i], m.PtZ[a + i]);
                double qx = q.X, qy = q.Y;
                if (mode != 0)
                {
                    double lf = 0.5 * m.PtLift[a + i];
                    if (mode == 1) qy -= lf * liftK * q.Sc;
                    else if (!tilted) { double o = Math.Min(12, lf * s * 0.3); qx += o; qy += o; }
                }
                ex[i] = qx; ey[i] = qy; es[i] = q.Sc;
            }
            if (!tilted)
            {
                run.Clear();
                for (int i = 0; i < n; i++) run.Add(new Point(ex[i], ey[i]));
                set.Run(0, run);
                return;
            }
            int cur = -1; run.Clear();
            for (int i = 0; i + 1 < n; i++)
            {
                double sc = (es[i] + es[i + 1]) / 2;
                if (sc < 0.28) { if (run.Count > 1) set.Run(cur, run); run.Clear(); cur = -1; continue; }
                int bd = Band(sc);
                if (bd != cur) { if (run.Count > 1) set.Run(cur, run); run.Clear(); run.Add(new Point(ex[i], ey[i])); cur = bd; }
                run.Add(new Point(ex[i + 1], ey[i + 1]));
            }
            if (run.Count > 1) set.Run(cur, run);
        }
        double Wd(int cls, int band) => Math.Max(WidthsM[cls] * s, cls == 2 ? 3.4 : 2.4) * BandFactor[band];
        var order = new[] { 3, 0, 1, 2 };
        void DrawRoads(List<int>[] bk, int mode)
        {
            if (mode == 2)
            {
                foreach (int cls in order)
                {
                    if (bk[cls].Count == 0) continue;
                    var set = new BandSet();
                    foreach (int e in bk[cls]) EdgeRuns(set, e, 2);
                    set.Draw(dc, DeckShadow, b => Wd(cls, b) + 5);
                }
                mode = 1;
            }
            foreach (int cls in order)
            {
                if (bk[cls].Count == 0) continue;
                var set = new BandSet();
                foreach (int e in bk[cls]) EdgeRuns(set, e, mode);
                set.Draw(dc, CasingBrushes[cls], b => Wd(cls, b) + 2.6);
            }
            foreach (int cls in order)
            {
                if (bk[cls].Count == 0) continue;
                var set = new BandSet();
                foreach (int e in bk[cls]) EdgeRuns(set, e, mode);
                set.Draw(dc, ClassBrush[cls], b => Wd(cls, b));
            }
        }
        DrawRoads(buckets, 0);
        foreach (int e in ferryEdges)
        {
            int a = m.EPtStart[e], b = m.EPtStart[e + 1] - 1;
            var q0 = P(m.PtX[a], m.PtZ[a]); var q1 = P(m.PtX[b], m.PtZ[b]);
            if (tilted && (q0.Sc < 0.28 || q1.Sc < 0.28)) continue;
            dc.DrawLine(FerryPen, new Point(q0.X, q0.Y), new Point(q1.X, q1.Y));
        }

        // route
        var rPts = PlanMode ? PlanPts : RoutePts; var rStart = PlanMode ? PlanEdgePt : RouteEdgePt; var rLift = PlanMode ? PlanLift : RouteLift;
        if (rLift.Length != rPts.Length / 2) rLift = Array.Empty<byte>();
        int rFirst = 0, rLast = 0; bool routeLifted = false;
        double rw = Math.Max(10 * s, 6);
        if (rPts.Length > 0 && rStart.Length > 0)
        {
            int total = rPts.Length / 2;
            rFirst = PlanMode ? 0 : rStart[Math.Min(Math.Max(0, RouteIdx - 1), rStart.Length - 1)];
            rLast = PlanMode ? total : Math.Min(total, rFirst + 6000);
            if (rLift.Length > 0) for (int p = rFirst; p < rLast && !routeLifted; p++) routeLifted = rLift[p] > 0;
        }
        // deck = false: route on the ground; deck = true: the parts on flyovers / bridges, raised
        void DrawRoute(bool deck, bool ghost)
        {
            if (rLast <= rFirst) return;
            var casing = new BandSet(); var fill = new BandSet();
            void BuildRoute(BandSet set)
            {
                int cur = -1; run.Clear();
                double lx = 0, ly = 0, ls = 1;
                for (int p = rFirst; p < rLast; p++)
                {
                    var q = P(rPts[p * 2], rPts[p * 2 + 1]);
                    double qy = deck && rLift.Length > 0 ? q.Y - 0.5 * rLift[p] * liftK * q.Sc : q.Y;
                    if (p > rFirst)
                    {
                        bool segUp = rLift.Length > 0 && (rLift[p] > 0 || rLift[p - 1] > 0);
                        double sc = (ls + q.Sc) / 2;
                        if (segUp != deck || sc < 0.28) { if (run.Count > 1) set.Run(cur, run); run.Clear(); cur = -1; }
                        else
                        {
                            int bd = tilted ? Band(sc) : 0;
                            if (bd != cur) { if (run.Count > 1) set.Run(cur, run); run.Clear(); run.Add(new Point(lx, ly)); cur = bd; }
                            run.Add(new Point(q.X, qy));
                        }
                    }
                    lx = q.X; ly = qy; ls = q.Sc;
                }
                if (run.Count > 1) set.Run(cur, run);
            }
            if (!ghost) { BuildRoute(casing); BuildRoute(fill); casing.Draw(dc, CasingBrush, b => (rw + 5) * BandFactor[b]); fill.Draw(dc, RouteBrush, b => rw * BandFactor[b]); }
            else { BuildRoute(fill); fill.Draw(dc, RouteGhost, b => rw * BandFactor[b]); }
        }
        DrawRoute(false, false);
        if (hasLift)
        {
            DrawRoads(elevBuckets, 2);
            if (routeLifted)
            {
                DrawRoute(true, false);
                DrawRoute(false, true);   // the route below shows faintly through the deck above it
            }
        }

        // finishing flag at the destination (the end of the mapped road can be well short of it)
        if (FlagOn && !PlanMode)
        {
            var q = P(FlagX, FlagZ);
            if (FlagLink && RoutePts.Length >= 2)
            {
                var r = P(RoutePts[^2], RoutePts[^1]);
                if (!(tilted && (q.Sc < 0.3 || r.Sc < 0.3))) dc.DrawLine(FlagLinkPen, new Point(r.X, r.Y), new Point(q.X, q.Y));
            }
            if (!(tilted && q.Sc < 0.3) && q.X > -80 && q.X < W + 80 && q.Y > -80 && q.Y < H + 80)
                Scaled(q.X, q.Y, Fs(q.Sc), () =>
                {
                    var pole = new Pen(Brushes.White, 3.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawEllipse(PoiShadow, null, new Point(q.X, q.Y + 1), 7, 3);
                    dc.DrawLine(pole, new Point(q.X, q.Y), new Point(q.X, q.Y - 50));
                    double x0 = q.X + 1.5, y0 = q.Y - 50, cell = 7;
                    dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1.4), new Rect(x0, y0, cell * 5, cell * 4));
                    for (int ix = 0; ix < 5; ix++) for (int iy = 0; iy < 4; iy++)
                        if ((ix + iy) % 2 == 0) dc.DrawRectangle(Brushes.Black, null, new Rect(x0 + ix * cell, y0 + iy * cell, cell, cell));
                });
        }

        // big area (city) names
        // area (city) names stay on at every zoom level
        {
            double size = Math.Clamp(26 * Math.Pow(zoom, 0.35), PlanMode ? 11 : 20, 34);
            foreach (var city in m.Cities)
            {
                float dx = city.X - cx, dz = city.Z - cz;
                if (dx * dx + dz * dz > radius * radius) continue;
                var q = P(city.X, city.Z);
                if ((tilted && q.Sc < 0.3) || q.X < -120 || q.X > W + 120 || q.Y < -30 || q.Y > H + 30) continue;
                string name = city.Name.ToUpperInvariant();
                double f = Fs(q.Sc);
                Scaled(q.X, q.Y, f, () =>
                {
                    var sh = Text(name, size, CityShadow, true); var ft = Text(name, size, CityText, true);
                    dc.DrawText(sh, new Point(q.X - ft.Width / 2 + 1.5, q.Y - ft.Height / 2 + 1.5));
                    dc.DrawText(ft, new Point(q.X - ft.Width / 2, q.Y - ft.Height / 2));
                });
            }
        }

        // border crossings: checkpoint symbol, or the flag of the country being entered
        if (zoom > 0.35)
            foreach (var bd in m.Borders)
            {
                float dx = bd.X - cx, dz = bd.Z - cz;
                if (dx > radius || dx < -radius || dz > radius || dz < -radius) continue;
                var q = P(bd.X, bd.Z);
                if ((tilted && q.Sc < 0.3) || q.X < -140 || q.X > W + 140 || q.Y < -60 || q.Y > H + 90) continue;
                double f = Fs(q.Sc);
                if (bd.Kind == 0)
                {
                    if (zoom < 0.6) continue;
                    Scaled(q.X, q.Y, f, () =>
                    {
                        var box = new Rect(q.X - 34, q.Y - 12, 68, 24);
                        dc.DrawRoundedRectangle(Brushes.White, new Pen(BorderRed, 2.2), box, 4, 4);
                        Center(dc, "BORDER", q.X, q.Y, 12, BorderRed, true);
                    });
                }
                else
                {
                    var flag = Assets?.Flag(bd.Cc);
                    if (flag == null) continue;
                    string country = CountryNames.Of(bd.Cc);
                    Scaled(q.X, q.Y, f, () =>
                    {
                        // the flag (2x) with the name of the country being entered right under it
                        var r = new Rect(q.X - 38, q.Y - 38, 76, 76);
                        dc.DrawEllipse(Brushes.White, null, new Point(q.X, q.Y), 42, 42);
                        dc.DrawImage(flag, r);
                        var ft = Text(country, 22, PillText, true);
                        var nb = new Rect(q.X - ft.Width / 2 - 8, q.Y + 36, ft.Width + 16, ft.Height + 2);
                        dc.DrawRoundedRectangle(PillBrush, new Pen(Brushes.White, 1.5), nb, 6, 6);
                        dc.DrawText(ft, new Point(nb.X + 8, nb.Y + 1));
                    });
                }
            }

        if (zoom > 0.5)
        {
            // road-number shields
            foreach (var rl in m.RoadLabels)
            {
                float dx = rl.X - cx, dz = rl.Z - cz;
                if (dx > radius || dx < -radius || dz > radius || dz < -radius) continue;
                var p = P(rl.X, rl.Z);
                if ((tilted && p.Sc < 0.3) || p.X < -80 || p.X > W + 80 || p.Y < -40 || p.Y > H + 40) continue;
                // US: Interstates blue with a red top, US routes white with a black edge, state routes and loops white pills
                bool interstate = rl.Text.StartsWith("I-"), usOther = !interstate && Games.Current == Games.Ats;
                var ft = Text(rl.Text, 34, usOther ? ShieldDark : Brushes.White, true);
                double bw = ft.Width + 24, bh = ft.Height + 4;
                var box = new Rect(p.X - bw / 2, p.Y - bh / 2, bw, bh);
                Scaled(p.X, p.Y, Fs(p.Sc), () =>
                {
                    if (interstate)
                    {
                        dc.DrawRoundedRectangle(ShieldBlue, null, box, 6, 6);
                        dc.PushClip(new RectangleGeometry(box, 6, 6));
                        dc.DrawRectangle(ShieldRed, null, new Rect(box.X, box.Y, box.Width, box.Height * 0.26));
                        dc.Pop();
                        dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 3.2), box, 6, 6);
                    }
                    else if (usOther) dc.DrawRoundedRectangle(Brushes.White, new Pen(ShieldDark, 3.2), box, rl.Text.StartsWith("US ") ? 6 : bh / 2, rl.Text.StartsWith("US ") ? 6 : bh / 2);
                    else dc.DrawRoundedRectangle(rl.Text.StartsWith('E') ? ShieldGreen : ShieldBlue, new Pen(Brushes.White, 3.2), box, 6, 6);
                    dc.DrawText(ft, new Point(box.X + 12, box.Y + 2));
                });
            }
        }
        if (zoom > 0.75)
        {
            // companies: the logo when the game has one, otherwise the name
            foreach (var co in m.Companies)
            {
                float dx = co.X - cx, dz = co.Z - cz;
                if (dx > radius || dx < -radius || dz > radius || dz < -radius) continue;
                var p = P(co.X, co.Z);
                if ((tilted && p.Sc < 0.35) || p.X < -130 || p.X > W + 130 || p.Y < -50 || p.Y > H + 50) continue;
                var logo = Assets?.Logo(co.Id);
                if (logo != null)
                {
                    Scaled(p.X, p.Y, Fs(p.Sc), () =>
                    {
                        var box = new Rect(p.X - 112, p.Y - 32, 224, 64);   // logos are drawn at 2x the size of the web and Android ones
                        dc.DrawRoundedRectangle(PillBrush, null, box, 8, 8);
                        dc.DrawImage(logo, new Rect(p.X - 104, p.Y - 26, 208, 52));
                    });
                    continue;
                }
                string name = m.CompanyNames.TryGetValue(co.Id, out var nm2) ? nm2 : co.Id;
                var ft = Text(name, 30, PillText, true);   // 2x the size of the web and Android ones
                var nb = new Rect(p.X - ft.Width / 2 - 10, p.Y - ft.Height / 2 - 2, ft.Width + 20, ft.Height + 4);
                Scaled(p.X, p.Y, Fs(p.Sc), () => { dc.DrawRoundedRectangle(PillBrush, null, nb, 8, 8); dc.DrawText(ft, new Point(nb.X + 10, nb.Y + 2)); });
            }
        }
        if (zoom > 0.3)
        {
            double r = (zoom > 0.8 ? 24 : 18) * 1.75;   // badges are 1.75x the size of the web and Android ones
            foreach (var poi in m.Pois)
            {
                if (!ShowPoiTypes.Contains(poi.Type)) continue;
                float dx = poi.X - cx, dz = poi.Z - cz;
                if (dx > radius || dx < -radius || dz > radius || dz < -radius) continue;
                var p = P(poi.X, poi.Z);
                if ((tilted && p.Sc < 0.3) || p.X < -r || p.X > W + r || p.Y < -r || p.Y > H + r) continue;
                Scaled(p.X, p.Y, Fs(p.Sc), () =>
                {
                    DrawPoi(dc, poi.Type, p.X, p.Y, r);
                });
            }
        }
        if (PlanMode)
            foreach (var pin in Pins)
            {
                var p = P(pin.X, pin.Z);
                if (p.X < -60 || p.X > W + 60 || p.Y < -60 || p.Y > H + 60) continue;
                var pc = pin.C; var pl = pin.Label;
                dc.DrawEllipse(new SolidColorBrush(pc), new Pen(Brushes.White, 3), new Point(p.X, p.Y), 20, 20);
                Center(dc, pl, p.X, p.Y, 22, Brushes.White, true);
            }
        for (int i = 0; !PlanMode && i < Stops.Count; i++)
        {
            var p = P(Stops[i].X, Stops[i].Z);
            if (p.X < -60 || p.X > W + 60 || p.Y < -60 || p.Y > H + 60) continue;
            int idx = i;
            Scaled(p.X, p.Y, Fs(p.Sc), () =>
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(25, 118, 210)), new Pen(new SolidColorBrush(Color.FromRgb(18, 92, 58)), 3), new Point(p.X, p.Y), 17, 17);
                Center(dc, (idx + 1).ToString(CultureInfo.InvariantCulture), p.X, p.Y, 20, Brushes.White, true);
            });
        }
        var nm = PlanMode ? null : NextManeuver;
        if (nm != null)
        {
            var p = P(nm.X, nm.Z);
            if (p.X > -80 && p.X < W + 80 && p.Y > -80 && p.Y < H + 80)
                Scaled(p.X, p.Y, Fs(p.Sc), () =>
                {
                    double r = 26;
                    var edge = Color.FromRgb(18, 92, 58);
                    dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(edge), 3), new Point(p.X, p.Y), r, r);
                    if (!isFree) Glyphs.Draw(dc, nm.Turn, nm.ExitNo, nm.AngleDeg, p.X, p.Y, r * 1.55, edge);
                    else dc.DrawEllipse(new SolidColorBrush(edge), null, new Point(p.X, p.Y), r * 0.35, r * 0.35);
                });
        }

        // haze over the far distance
        if (tilted)
        {
            double sT = Math.Sin(tilt), cT = Math.Cos(tilt);
            double scFar = E / (E + yMax * sT), yFar = py - yMax * cT * scFar;
            double fadeTo = Math.Max(yFar + 60, H * 0.2);
            var hz = Color.FromRgb(34, 40, 54);
            var grad = new LinearGradientBrush(new GradientStopCollection { new GradientStop(hz, 0), new GradientStop(Color.FromArgb((byte)(255 * Math.Min(1, tilt / TiltMax)), hz.R, hz.G, hz.B), 0.35), new GradientStop(Color.FromArgb(0, hz.R, hz.G, hz.B), 1) }, new Point(0, 0), new Point(0, 1));
            grad.Freeze();
            dc.DrawRectangle(grad, null, new Rect(0, 0, W, fadeTo));
        }

        if (PlanMode) return;
        var tp = (X: px, Y: py);
        double t = 15;
        dc.PushTransform(new TranslateTransform(tp.X, tp.Y));
        dc.PushTransform(new RotateTransform((Bearing - vb) * 180 / Math.PI));
        var tg = new StreamGeometry();
        using (var c = tg.Open())
        {
            c.BeginFigure(new Point(0, -t * 1.2), true, true);
            c.LineTo(new Point(t * 0.85, t * 0.85), true, false); c.LineTo(new Point(0, t * 0.4), true, false); c.LineTo(new Point(-t * 0.85, t * 0.85), true, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0, 214, 120)), new Pen(Brushes.White, 2.5), tg);
        dc.Pop(); dc.Pop();
    }
}
