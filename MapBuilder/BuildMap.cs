using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Ets2Gps.MapBuilder;

/// <summary>
/// Builds the compact road network (etsmap.bin, format version 8) from the mounted ETS2 map and mods.
/// Port of companion/build_map.py: roads and prefab nav curves become directed edges, with road grades, hidden items,
/// ferries, junction lane data, cities, companies and POIs.
/// </summary>
public static class BuildMap
{
    /// <summary>Changes whenever the builder's output could change (it triggers an automatic rebuild).</summary>
    public const int FormatVersion = 22;
    /// <summary>The binary layout the app reads.</summary>
    public const int FileVersion = 10;

    const double LaneW = 4.5;
    static readonly Dictionary<uint, int> SpawnPoi = new() { [3] = 1, [4] = 2, [5] = 3, [6] = 4, [7] = 5, [8] = 8, [15] = 6, [16] = 7 };
    static readonly UInt128 HudParking = Sii.StrToToken("hud_parking");
    // prefab spawn point types of a company yard
    const uint SpawnUnloadEasy = 2, SpawnCompany = 13, SpawnUnloadMedium = 19, SpawnUnloadHard = 20;
    static readonly string[] NotRoundaboutWords = { "gas", "plaza", "parking", "depot", "service", "station", "tshape", "truckstop" };

    /// <summary>Overlay token such as "al_sh2", "at_e45", "b_a1" -> the text of the road-number shield ("SH 2", "E45", "A1"), or null for other overlays.</summary>
    // ATS states (map overlay tokens start with their postal code: ca_r1, mt24, txl338)
    static readonly HashSet<string> UsStates = new() { "az", "ar", "ca", "co", "id", "il", "ia", "ks", "la", "mo", "mt", "ne", "nv", "nm", "ok", "or", "sd", "tx", "ut", "wa", "wy" };

    /// <summary>ATS road numbers: is10 -> I-10, us101 -> US 101, ca_r1 -> CA 1, txl338 -> LOOP 338, bus40 -> BUS 40.</summary>
    static string? UsRoadLabelText(string tok)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tok, "^is([0-9]{1,3})([a-z]?)$");
        if (m.Success) return "I-" + m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant();
        m = System.Text.RegularExpressions.Regex.Match(tok, "^us([0-9]{1,3})([a-z]?)$");
        if (m.Success) return "US " + m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant();
        m = System.Text.RegularExpressions.Regex.Match(tok, "^bus([0-9]{1,3})$");
        if (m.Success) return "BUS " + m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(tok, "^txl([0-9]{1,4})$");
        if (m.Success) return "LOOP " + m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(tok, "^([a-z]{2})_?r?([0-9]{1,4})([a-z]?)$");
        if (m.Success && UsStates.Contains(m.Groups[1].Value)) return m.Groups[1].Value.ToUpperInvariant() + " " + m.Groups[2].Value + m.Groups[3].Value.ToUpperInvariant();
        return null;
    }

    static string? RoadLabelText(string tok)
    {
        if (Games.Current == Games.Ats) return UsRoadLabelText(tok);
        var m = System.Text.RegularExpressions.Regex.Match(tok, "^[a-z]{1,3}_([a-z]{0,3})([0-9]{1,4})([a-z]?)$");
        if (!m.Success || tok.StartsWith("border_")) return null;
        string letters = m.Groups[1].Value.ToUpperInvariant();
        return letters + (letters.Length >= 2 ? " " : "") + m.Groups[2].Value + m.Groups[3].Value;
    }

    // ------------------------------------------------------------------------------ geometry --

    static double PyMod(double a, double b) => a - b * Math.Floor(a / b);   // Python's float % (sign of the divisor)
    static int PyMod(int a, int n) => ((a % n) + n) % n;
    static float F32(double v) => (float)v;

    /// <summary>
    /// sqrt(a*a + b*b), correctly rounded in practice (exact squares via FMA, then one Newton correction). The map must come out
    /// the same as the Python builder's, which uses math.hypot; .NET's own Hypot differs in the last bit, enough to flip ties
    /// when two prefab nodes score exactly alike.
    /// </summary>
    static double Hy(double a, double b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        if (a == 0) return b;
        if (b == 0) return a;
        if (a < b) (a, b) = (b, a);
        double p = a * a, pe = Math.FusedMultiplyAdd(a, a, -p), q = b * b, qe = Math.FusedMultiplyAdd(b, b, -q);
        double sum = p + q, t = (p - sum) + q, e = t + pe + qe;
        double h = Math.Sqrt(sum + e);
        double r = Math.FusedMultiplyAdd(-h, h, sum) + e;
        return h + r / (2 * h);
    }
    static int Q4(double v) => (int)Math.Floor(v * 4 + 0.5);

    static (double X, double Z) RotatePoint(double x, double z, double ang, double rx, double rz)
    {
        double s = Math.Sin(ang), c = Math.Cos(ang), nx = x - rx, nz = z - rz;
        return (nx * c - nz * s + rx, nx * s + nz * c + rz);
    }

    static double Hermite(double s, double p0, double p1, double t0, double t1)
    {
        double s2 = s * s, s3 = s * s * s;
        return (2 * s3 - 3 * s2 + 1) * p0 + (-2 * s3 + 3 * s2) * p1 + (s3 - 2 * s2 + s) * t0 + (s3 - s2) * t1;
    }

    static List<(double X, double Z)> RoadPoints((double X, double Z, double Rot) sn, (double X, double Z, double Rot) en)
    {
        double radius = Hy(en.X - sn.X, en.Z - sn.Z);
        double tsx = Math.Cos(-(Math.PI * 0.5 - sn.Rot)) * radius, tsz = Math.Sin(-(Math.PI * 0.5 - sn.Rot)) * radius;
        double tex = Math.Cos(-(Math.PI * 0.5 - en.Rot)) * radius, tez = Math.Sin(-(Math.PI * 0.5 - en.Rot)) * radius;
        int n = Math.Max(3, Math.Min(24, (int)(radius / 18) + 2));
        var pts = new List<(double, double)>(n);
        for (int i = 0; i < n; i++)
        {
            double s = (double)i / (n - 1);
            pts.Add((Hermite(s, sn.X, en.X, tsx, tex), Hermite(s, sn.Z, en.Z, tsz, tez)));
        }
        return pts;
    }

    /// <summary>Height along a road between two nodes: a smooth step from the first node's height to the second's.</summary>
    static List<double> RoadYs(double y0, double y1, int n)
    {
        var ys = new List<double>(n);
        for (int i = 0; i < n; i++) { double t = (double)i / (n - 1); ys.Add(y0 + (y1 - y0) * (t * t * (3 - 2 * t))); }
        return ys;
    }

    static (double, double) QuatDir(double w, double x, double y, double z)
    {
        double theta = 2.0 * Math.Atan2(y, w);
        return (-Math.Sin(theta), -Math.Cos(theta));
    }

    sealed class Curve
    {
        public (double X, double Z) S, E;
        public double SY, EY;
        public (double X, double Z) SDir, EDir;
        public double Len;
        public List<int> Next = new();
        public int Pc;
    }

    static List<double> CurveYs(Curve c, int steps)
    {
        var ys = new List<double>(steps);
        for (int i = 0; i < steps; i++) { double t = (double)i / (steps - 1); ys.Add(c.SY + (c.EY - c.SY) * (t * t * (3 - 2 * t))); }
        return ys;
    }

    static List<(double X, double Z)> CurvePoints(Curve c, int steps)
    {
        var pts = new List<(double, double)>(steps);
        for (int i = 0; i < steps; i++)
        {
            double s = (double)i / (steps - 1);
            pts.Add((Hermite(s, c.S.X, c.E.X, c.SDir.X * c.Len, c.EDir.X * c.Len), Hermite(s, c.S.Z, c.E.Z, c.SDir.Z * c.Len, c.EDir.Z * c.Len)));
        }
        return pts;
    }

    static List<Curve>? ParseCurves(Prefab pf)
    {
        var d = pf.Raw;
        int stride = pf.Version >= 25 ? 0x84 : 0x80;
        var curves = new List<Curve>();
        for (int i = 0; i < pf.NavCount; i++)
        {
            int r = pf.NavOff + i * stride;
            if (r + stride > d.Length) return null;
            double sx = Bin.F32(d, r + 0x10), sz = Bin.F32(d, r + 0x18), ex = Bin.F32(d, r + 0x1C), ez = Bin.F32(d, r + 0x24);
            var sq = new double[4]; var eq = new double[4];
            for (int k = 0; k < 4; k++) { sq[k] = Bin.F32(d, r + 0x28 + 4 * k); eq[k] = Bin.F32(d, r + 0x38 + 4 * k); }
            double ln = Bin.F32(d, r + 0x48);
            var nxt = new int[4];
            for (int k = 0; k < 4; k++) nxt[k] = Bin.I32(d, r + 0x4C + 4 * k);
            int nc = Bin.I32(d, r + 0x6C), pc = Bin.I32(d, r + 0x70);
            if (nxt.Any(v => v >= pf.NavCount) || nc < 0 || nc > 4 || pc < 0 || pc > 4) return null;
            curves.Add(new Curve
            {
                S = (sx, sz), E = (ex, ez), SY = Bin.F32(d, r + 0x14), EY = Bin.F32(d, r + 0x20), SDir = QuatDir(sq[0], sq[1], sq[2], sq[3]), EDir = QuatDir(eq[0], eq[1], eq[2], eq[3]),
                Len = Math.Max(ln, 0.01), Next = nxt.Where(v => v >= 0).ToList(), Pc = pc,
            });
        }
        return curves;
    }

    static bool HasCycle(List<Curve> curves)
    {
        var color = new int[curves.Count];
        for (int s0 = 0; s0 < curves.Count; s0++)
        {
            if (color[s0] != 0) continue;
            var stack = new Stack<(int U, int I)>();
            stack.Push((s0, 0)); color[s0] = 1;
            while (stack.Count > 0)
            {
                var (u, i) = stack.Pop();
                var nxt = curves[u].Next;
                if (i < nxt.Count)
                {
                    stack.Push((u, i + 1));
                    int v = nxt[i];
                    if (color[v] == 1) return true;
                    if (color[v] == 0) { color[v] = 1; stack.Push((v, 0)); }
                }
                else color[u] = 2;
            }
        }
        return false;
    }

    static (double Ux, double Uy, double R)? Circle3((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-6) return null;
        double ux = ((a.X * a.X + a.Y * a.Y) * (b.Y - c.Y) + (b.X * b.X + b.Y * b.Y) * (c.Y - a.Y) + (c.X * c.X + c.Y * c.Y) * (a.Y - b.Y)) / d;
        double uy = ((a.X * a.X + a.Y * a.Y) * (c.X - b.X) + (b.X * b.X + b.Y * b.Y) * (a.X - c.X) + (c.X * c.X + c.Y * c.Y) * (b.X - a.X)) / d;
        return (ux, uy, Hy(a.X - ux, a.Y - uy));
    }

    /// <summary>Prefab-level: do at least three curved lanes share one circle (centre and radius) covering >= 70 % of it?</summary>
    static bool HasRing(List<Curve> curves)
    {
        var arcs = new List<(double Cx, double Cz, double R, List<(double X, double Z)> Pts)>();
        foreach (var c in curves)
        {
            var pts = CurvePoints(c, 7);
            if (pts.Count < 3) continue;
            double h0 = Math.Atan2(pts[1].Z - pts[0].Z, pts[1].X - pts[0].X);
            double h1 = Math.Atan2(pts[^1].Z - pts[^2].Z, pts[^1].X - pts[^2].X);
            if (Math.Abs(PyMod(h1 - h0 + Math.PI, 2 * Math.PI) - Math.PI) < Math.PI * 20 / 180) continue;
            var cc = Circle3(pts[0], pts[pts.Count / 2], pts[^1]);
            if (cc is { } q && q.R >= 5 && q.R <= 70) arcs.Add((q.Ux, q.Uy, q.R, pts));
        }
        foreach (var (cx, cz, r, _) in arcs)
        {
            var grp = arcs.Where(a => Hy(a.Cx - cx, a.Cz - cz) < 4 && Math.Abs(a.R - r) < 4).ToList();
            if (grp.Count < 3) continue;
            var bins = new HashSet<int>();
            foreach (var a in grp)
                foreach (var q in a.Pts)
                    bins.Add((int)(((Math.Atan2(q.Z - cz, q.X - cx) + Math.PI) / (2 * Math.PI)) * 24) % 24);
            if (bins.Count / 24.0 >= 0.7) return true;
        }
        return false;
    }

    /// <summary>
    /// Is this placed junction a roundabout? With at least three connected arms, traffic carrying straight on between two
    /// opposite arms is forced to bend around a central island (a sideways bulge), whereas at an ordinary intersection that
    /// path is straight.
    /// </summary>
    static bool IsRoundabout(List<ulong> unodes, int origin, Dictionary<(int, int), (double Len, List<(double X, double Z)> Pts)> paths, Func<ulong, bool> isConnected)
    {
        int nn = unodes.Count;
        var connected = new HashSet<int>();
        for (int i = 0; i < nn; i++) if (isConnected(unodes[PyMod(i - origin, nn)])) connected.Add(i);
        if (connected.Count < 3) return false;
        bool seenStraight = false;
        foreach (var ((a, b), (_, pts)) in paths)
        {
            if (!connected.Contains(a) || !connected.Contains(b) || pts.Count < 3) continue;
            double h0 = Math.Atan2(pts[1].Z - pts[0].Z, pts[1].X - pts[0].X);
            double h1 = Math.Atan2(pts[^1].Z - pts[^2].Z, pts[^1].X - pts[^2].X);
            if (Math.Abs(PyMod(h1 - h0 + Math.PI, 2 * Math.PI) - Math.PI) > Math.PI * 25 / 180) continue; // not carrying straight on
            var (x0, z0) = pts[0]; var (x1, z1) = pts[^1];
            double chord = Hy(x1 - x0, z1 - z0);
            if (chord < 12) continue;
            double bulge = pts.Max(q => Math.Abs((q.X - x0) * (z1 - z0) - (q.Z - z0) * (x1 - x0)) / chord);
            if (bulge < 4.0) return false; // one straight-through path is genuinely straight: an ordinary intersection
            seenStraight = true;
        }
        return seenStraight;
    }

    /// <summary>Prefab node whose cross-section (the line through the node, perpendicular to its direction) the point lies on.</summary>
    static int NearestNode(Prefab pf, (double X, double Z) p)
    {
        int best = -1; double bs = 1e30;
        for (int i = 0; i < pf.Nodes.Count; i++)
        {
            var (x, z, rx, rz, lanes) = pf.Nodes[i];
            double dx = p.X - x, dz = p.Z - z;
            double ln = Hy(rx, rz); if (ln == 0) ln = 1.0;
            double along = Math.Abs(dx * rx + dz * rz) / ln;
            double lat = Math.Abs(-dx * rz + dz * rx) / ln;
            double half = Math.Max(lanes, 1) * 2.25 + 1.5;
            double score = along * 4.0 + Math.Max(0.0, lat - half) + 0.01 * Math.Pow(dx * dx + dz * dz, 0.5);
            if (score < bs) { best = i; bs = score; }
        }
        return best;
    }

    /// <summary>Chains of nav curves from a prefab entry node to an exit node: (entry, exit) -> (length, local points).</summary>
    static Dictionary<(int, int), (double Len, List<(double X, double Z)> Pts)> PrefabPaths(Prefab pf, List<Curve> curves, Dictionary<(int, int), List<double>>? ysOut = null)
    {
        var best = new Dictionary<(int, int), (double Len, List<int> Chain)>();
        var starts = new List<int>();
        for (int i = 0; i < curves.Count; i++) if (curves[i].Pc == 0) starts.Add(i);
        int budget = 4000;
        var chain = new List<int>();
        var seen = new HashSet<int>();

        void Dfs(int i)
        {
            if (budget <= 0) return;
            budget--;
            var c = curves[i];
            chain.Add(i); seen.Add(i);
            if (c.Next.Count == 0)
            {
                int a = NearestNode(pf, curves[chain[0]].S), b = NearestNode(pf, c.E);
                if (a != b && a >= 0 && b >= 0)
                {
                    double length = chain.Sum(k => curves[k].Len);
                    var key = (a, b);
                    if (!best.TryGetValue(key, out var cur) || length < cur.Len) best[key] = (length, new List<int>(chain));
                }
            }
            else
                foreach (int n in c.Next) if (!seen.Contains(n)) Dfs(n);
            chain.RemoveAt(chain.Count - 1);
            seen.Remove(i);
        }

        foreach (int s in starts) Dfs(s);
        var output = new Dictionary<(int, int), (double, List<(double X, double Z)>)>();
        foreach (var (key, (length, ch)) in best)
        {
            var pts = new List<(double X, double Z)>(); var yl = new List<double>();
            foreach (int k in ch)
            {
                var c = curves[k];
                int steps = Math.Max(2, Math.Min(10, (int)(c.Len / 5) + 2));
                var seg = CurvePoints(c, steps); var sy = CurveYs(c, steps);
                if (pts.Count == 0) { pts.AddRange(seg); yl.AddRange(sy); } else { pts.AddRange(seg.Skip(1)); yl.AddRange(sy.Skip(1)); }
            }
            output[key] = (length, pts);
            if (ysOut != null) ysOut[key] = yl;
        }
        return output;
    }

    /// <summary>Lane connections of a prefab: (entry node, exit node) -> (lanes at the entry, bitmask of lanes that reach the exit).</summary>
    static Dictionary<(int, int), (int N, int Mask)> PrefabLanes(Prefab pf, List<Curve> curves)
    {
        var starts = new List<int>();
        for (int i = 0; i < curves.Count; i++) if (curves[i].Pc == 0) starts.Add(i);
        var byNode = new Dictionary<int, List<int>>();
        foreach (int i in starts)
        {
            int a = NearestNode(pf, curves[i].S);
            if (a >= 0) { if (!byNode.TryGetValue(a, out var l)) byNode[a] = l = new(); l.Add(i); }
        }
        var laneOf = new Dictionary<int, int>();
        var count = new Dictionary<int, int>();
        foreach (var (a, idxs) in byNode)
        {
            var (dx, dz) = curves[idxs[0]].SDir;
            double rx = -dz, rz = dx; // right-hand side of the travel direction
            double Lat(int i) => Math.Round((curves[i].S.X * rx + curves[i].S.Z * rz) * 2, MidpointRounding.ToEven) / 2;
            var lat = idxs.Select(Lat).Distinct().OrderBy(v => v).ToList();
            count[a] = Math.Min(lat.Count, 15);
            foreach (int i in idxs) laneOf[i] = Math.Min(lat.IndexOf(Lat(i)), 14);
        }
        var output = new Dictionary<(int, int), (int, int)>();
        foreach (int i in starts)
        {
            if (!laneOf.ContainsKey(i)) continue;
            int a = NearestNode(pf, curves[i].S);
            int budget = 300;
            var seen = new HashSet<int>();
            var stack = new List<int> { i };
            var ends = new HashSet<int>();
            while (stack.Count > 0 && budget > 0)
            {
                budget--;
                int u = stack[^1]; stack.RemoveAt(stack.Count - 1);
                if (!seen.Add(u)) continue;
                var c = curves[u];
                if (c.Next.Count == 0)
                {
                    int b = NearestNode(pf, c.E);
                    if (b >= 0 && b != a) ends.Add(b);
                }
                else stack.AddRange(c.Next);
            }
            foreach (int b in ends)
            {
                var cur = output.TryGetValue((a, b), out var v) ? v : (count[a], 0);
                output[(a, b)] = (cur.Item1, cur.Item2 | (1 << laneOf[i]));
            }
        }
        return output.ToDictionary(kv => kv.Key, kv => (kv.Value.Item1, kv.Value.Item2));
    }

    // -------------------------------------------------------------------------------- sectors --

    sealed class Collected
    {
        public Dictionary<ulong, (double X, double Z, double Rot)> Nodes = new();
        public Dictionary<ulong, double> NodeY = new();
        public Dictionary<ulong, (ulong Uid, ulong Su, ulong Eu, ulong Look, bool Secret, byte Dlc, bool Hidden)> Roads = new();
        public Dictionary<ulong, (ulong Uid, ulong PrefabTok, List<ulong> Nodes, int Origin, bool Secret, byte Dlc, bool Hidden)> Prefabs = new();
        public Dictionary<ulong, (ulong Tok, double X, double Z)> Cities = new();
        public List<(ulong Company, ulong City, double X, double Z)> Companies = new();
        public List<(ulong Port, ulong Link, ulong Node, bool Train, double X, double Z)> Ferries = new();
        public List<(int Type, double X, double Z)> Overlays = new();
        public List<(ulong Tok, double X, double Z)> RoadLabels = new();
        public List<(List<ulong> Nodes, uint Color)> Areas = new();
    }

    static Collected CollectSectors(FileSystem fs, Defs defs, Action<string> log, Action<double>? progress)
    {
        var names = fs.ListDir("map").Files.Where(f => f.EndsWith(".mbd")).Select(m => m[..^4]).ToList();
        if (names.Count == 0) throw new InvalidOperationException("no map (.mbd) found in the mounted archives");
        if (names.Count > 1)
        {
            // keep the map provided by the highest-priority archive (mods mount last)
            var order = fs.Mounted.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
            int Prio(string n) => fs.Files.TryGetValue(CityHash.Hash($"map/{n}.mbd"), out var arc) && order.TryGetValue(arc.Path, out var i) ? i : -1;
            var ets = names.Where(n => n != "usa").ToList(); if (ets.Count == 0) ets = names;
            string keep = ets[0]; int bp = Prio(keep);
            foreach (var n in ets) if (Prio(n) > bp) { keep = n; bp = Prio(n); }
            log($"maps found: {string.Join(", ", names)} -> using '{keep}'");
            names = new() { keep };
        }
        var col = new Collected();
        int skipped = 0, bad = 0;
        foreach (var name in names)
        {
            var files = fs.ListDir("map/" + name).Files.Where(f => f.EndsWith(".base")).ToList();
            log($"parsing {files.Count} sectors of map '{name}'");
            int done = 0;
            foreach (var f in files)
            {
                progress?.Invoke(files.Count == 0 ? 1 : (double)done++ / files.Count);
                var d = fs.Read($"map/{name}/{f}");
                Sector s;
                try { s = SectorParser.Parse(d!, defs); }
                catch (Exception e) { log($"  ! {f}: {e.Message}"); bad++; continue; }
                if (s.Version < 884) { skipped++; continue; }
                if (!s.Ok) { bad++; log($"  ! {f}: did not parse to end of file; ignoring"); continue; }
                foreach (var kv in s.Nodes) col.Nodes.TryAdd(kv.Key, kv.Value);
                foreach (var kv in s.NodeY) col.NodeY.TryAdd(kv.Key, kv.Value);
                foreach (var r in s.Roads) col.Roads[r.Uid] = r;
                foreach (var p in s.Prefabs) col.Prefabs[p.Uid] = p;
                foreach (var c in s.Cities) col.Cities.TryAdd(c.Tok, c);
                col.Companies.AddRange(s.Companies);
                col.Ferries.AddRange(s.Ferries);
                col.Overlays.AddRange(s.Overlays);
                col.RoadLabels.AddRange(s.RoadLabels);
                col.Areas.AddRange(s.Areas);
            }
        }
        if (skipped > 0) log($"  ! {skipped} sectors use an unsupported (very old) format and were skipped");
        return col;
    }

    // ----------------------------------------------------------------------------------- build --

    sealed class GEdge
    {
        public ulong A, B; public int Flags, Cls, Speed; public List<(double X, double Z)> Pts = new(); public List<double>? Ys; public uint Jid; public int Wq;
    }

    /// <summary>
    /// Writes etsmap.drops.json: for each company location, the entrance point of its yard and the easy / medium / hard drop-off spots
    /// (where the game puts the delivery flag). The yard is the company prefab whose company point is nearest, within 250 m.
    /// </summary>
    static void WriteDrops(string path, IEnumerable<(string Id, string City, double X, double Z)> companies,
        List<(double GX, double GZ, (double X, double Z)? Easy, (double X, double Z)? Medium, (double X, double Z)? Hard)> yards, Action<string> log)
    {
        static float[]? P((double X, double Z)? p) => p is { } q ? new[] { (float)Math.Round(q.X, 1), (float)Math.Round(q.Z, 1) } : null;
        var list = new List<CompanyDrops>();
        foreach (var (id, city, x, z) in companies)
        {
            int best = -1; double bd = 250;
            for (int i = 0; i < yards.Count; i++) { double d = Hy(yards[i].GX - x, yards[i].GZ - z); if (d < bd) { bd = d; best = i; } }
            if (best < 0) continue;
            var y = yards[best];
            if (y.Easy == null && y.Medium == null && y.Hard == null) continue;
            list.Add(new CompanyDrops { Company = id, City = city, Gate = P((y.GX, y.GZ))!, Easy = P(y.Easy), Medium = P(y.Medium), Hard = P(y.Hard) });
        }
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(list));
        log($"{list.Count} company drop-off points");
    }

    /// <summary>Builds etsmap.bin (zlib-compressed) into outPath.</summary>
    public static void Build(FileSystem fs, string outPath, Action<string> log, Action<string, double>? progress = null, string? assetsPath = null, string? dropsPath = null)
    {
        var t0 = DateTime.UtcNow;
        progress?.Invoke("Reading the game's road definitions…", 0);
        var defs = new Defs(fs, log);
        progress?.Invoke("Reading the map sectors…", 0);
        var col = CollectSectors(fs, defs, log, f => progress?.Invoke("Reading the map sectors…", f));
        var nodes = col.Nodes;
        var roads = col.Roads.Values.ToList();
        var prefabs = col.Prefabs.Values.ToList();
        var cities = col.Cities.Values.ToList();
        log($"{nodes.Count} nodes, {roads.Count} roads, {prefabs.Count} prefabs, {cities.Count} cities");
        progress?.Invoke("Building the road network…", 0);

        var graph = new List<GEdge>();
        var junctions = new SortedDictionary<uint, (int Count, bool Rb)>();
        var oneway = new List<(int Index, ulong Look)>();
        var pois = new List<(int Type, double X, double Z)>();
        var ringCache = new Dictionary<ulong, bool>();
        var jctCands = new List<(uint Jid, List<ulong> Unodes, int Origin, Dictionary<(int, int), (double, List<(double X, double Z)>)> Paths)>();
        var lanesOf = new Dictionary<(ulong, ulong, uint), (int N, int Mask)>();

        foreach (var (uid, su, eu, look, secret, _, hid) in roads)
        {
            if (!nodes.TryGetValue(su, out var sn) || !nodes.TryGetValue(eu, out var en)) continue;
            var rl = defs.RoadLooks[look];
            int dl = rl.DriveLeft, dr = rl.DriveRight;
            if (dl + dr == 0) continue; // rails, trams, pedestrian-only
            int total = rl.Left + rl.Right;
            double width = rl.Offset + LaneW * total;
            var pts = RoadPoints(sn, en);
            if (secret) continue;
            var rys = RoadYs(col.NodeY.GetValueOrDefault(su), col.NodeY.GetValueOrDefault(eu), pts.Count);
            int wq = Math.Min(255, (int)(Math.Max(width, 4.5) * 4));
            int dt = dl + dr;
            int cls = dt >= 6 ? 2 : dt >= 4 ? 1 : 0;
            int speed = rl.Track ? 30 : cls == 2 ? 110 : cls == 1 ? 90 : 50;
            bool fwd = dr > 0, bwd = dl > 0;
            if (fwd != bwd && rl.Side.Length > 0) { fwd = rl.Side == "R"; bwd = rl.Side == "L"; }
            int hbit = hid ? 16 : 0;
            if (fwd && bwd) graph.Add(new GEdge { A = su, B = eu, Flags = 1 | (rl.Grade << 1) | hbit, Cls = cls, Speed = speed, Pts = pts, Ys = rys, Jid = 0, Wq = wq });
            else if (fwd) { oneway.Add((graph.Count, look)); graph.Add(new GEdge { A = su, B = eu, Flags = (rl.Grade << 1) | hbit, Cls = cls, Speed = speed, Pts = pts, Ys = rys, Jid = 0, Wq = wq }); }
            else if (bwd) { oneway.Add((graph.Count, look)); var rp = new List<(double, double)>(pts); rp.Reverse(); var rrys = new List<double>(rys); rrys.Reverse(); graph.Add(new GEdge { A = eu, B = su, Flags = (rl.Grade << 1) | hbit, Cls = cls, Speed = speed, Pts = rp, Ys = rrys, Jid = 0, Wq = wq }); }
        }

        int npref = 0, nonav = 0, pdone = 0;
        int dbgN = 0, dbgBig = 0; double dbgSum0 = 0, dbgSum1 = 0;
        // Closed border crossings: a border checkpoint whose drivable roads are all hidden from the game's map (the Russian borders, the
        // Lebanon - Israel border). Its gates never open and the game's navigation never routes through it, so its paths are left out.
        var roadsAt = new Dictionary<ulong, (int Hidden, int Shown)>();
        foreach (var (_, su, eu, look, secret, _, hid) in roads)
        {
            var rl = defs.RoadLooks[look];
            if (secret || rl.DriveLeft + rl.DriveRight == 0) continue;
            foreach (var u in new[] { su, eu })
            {
                var c = roadsAt.GetValueOrDefault(u);
                roadsAt[u] = hid ? (c.Hidden + 1, c.Shown) : (c.Hidden, c.Shown + 1);
            }
        }
        bool ClosedBorder(List<ulong> unodes)
        {
            int hidden = 0, shown = 0;
            foreach (var u in unodes) if (roadsAt.TryGetValue(u, out var c)) { hidden += c.Hidden; shown += c.Shown; }
            return hidden > 0 && shown == 0;
        }
        int closedBorders = 0;
        // company yards: the company point (where the game's GPS route ends, at the entrance) and the drop-off spots inside
        var prefabAreas = new List<(uint Color, List<(float X, float Z)> Pts)>();
        var yards = new List<(double GX, double GZ, (double X, double Z)? Easy, (double X, double Z)? Medium, (double X, double Z)? Hard)>();
        foreach (var (uid, ptok, unodes, origin, secret, _, hid) in prefabs)
        {
            if ((++pdone & 0x3FF) == 0) progress?.Invoke("Building the road network…", (double)pdone / prefabs.Count);
            var pf = defs.Prefab(ptok);
            if (pf == null || unodes.Count == 0 || origin >= pf.Nodes.Count) continue;
            if (!nodes.TryGetValue(unodes[0], out var onode)) continue;
            var mpo = pf.Nodes[origin];
            double rot = onode.Rot - Math.PI - Math.Atan2(mpo.Rz, mpo.Rx) + Math.PI / 2;
            double sx = onode.X - mpo.X, sz = onode.Z - mpo.Z;
            (double X, double Z) Tf((double X, double Z) p) => RotatePoint(sx + p.X, sz + p.Z, rot, onode.X, onode.Z);
            npref++;
            if (!secret && pf.Spawns.Any(sp => sp.Type == SpawnCompany))
            {
                (double X, double Z)? At(uint type) { foreach (var sp in pf.Spawns) if (sp.Type == type) return Tf((sp.X, sp.Z)); return null; }
                var gate = At(SpawnCompany)!.Value;
                yards.Add((gate.X, gate.Z, At(SpawnUnloadEasy), At(SpawnUnloadMedium), At(SpawnUnloadHard)));
            }
            if (!hid && !secret)
            {
                foreach (var sp in pf.Spawns)
                    if (SpawnPoi.TryGetValue(sp.Type, out int t)) { var q = Tf((sp.X, sp.Z)); pois.Add((t, q.X, q.Z)); }
                var seenTrig = new HashSet<uint>();
                foreach (var (tid, act, tx, tz) in pf.Triggers)
                    if (act == (ulong)HudParking && seenTrig.Add(tid)) { var q = Tf((tx, tz)); pois.Add((3, q.X, q.Z)); }
                // the prefab's own map shapes: company compounds, lots, buildings, grass
                foreach (var (color, ppts) in pf.Polys)
                    prefabAreas.Add((color, ppts.Select(p => { var q = Tf(p); return (F32(q.X), F32(q.Z)); }).ToList()));
            }
            // routing: nav curve chains between prefab nodes
            if (pf.NavCount == 0) continue;
            var curves = ParseCurves(pf);
            if (curves == null || curves.Count == 0) { nonav++; continue; }
            var pathYs = new Dictionary<(int, int), List<double>>();
            var paths = PrefabPaths(pf, curves, pathYs);
            double dy = col.NodeY.GetValueOrDefault(unodes[0]) - (origin < pf.NodeY.Count ? pf.NodeY[origin] : 0);
            var plane = PrefabLanes(pf, curves);
            string path = defs.PrefabPaths.TryGetValue(ptok, out var pp) ? pp.ToLowerInvariant() : "";
            if (path.Contains("/border") && ClosedBorder(unodes)) { closedBorders++; continue; }
            // unique id per placed junction (the game's item uids collide when truncated to 32 bits)
            uint jid = (uint)(junctions.Count + 1);
            if (!ringCache.TryGetValue(ptok, out var isRing))
                ringCache[ptok] = isRing = (HasRing(curves) || HasCycle(curves)) && !NotRoundaboutWords.Any(w => path.Contains(w));
            if (isRing) jctCands.Add((jid, unodes, origin, paths));
            junctions[jid] = (pf.Nodes.Count, false);
            int nn = unodes.Count;
            foreach (var ((a, b), (_, lpts)) in paths)
            {
                if (nn != pf.Nodes.Count) continue;
                // item node k corresponds to prefab node (origin + k) % n
                ulong ua = unodes[PyMod(a - origin, nn)], ub = unodes[PyMod(b - origin, nn)];
                List<double>? jys = null;
                if (pathYs.TryGetValue((a, b), out var pyl) && pyl.Count == lpts.Count && pyl.Count >= 2 && col.NodeY.TryGetValue(ua, out var yA) && col.NodeY.TryGetValue(ub, out var yB))
                {
                    // anchor both ends on the heights of the nodes the roads attach to; the shape in between comes from the prefab
                    double e0 = yA - (pyl[0] + dy), e1 = yB - (pyl[^1] + dy);
                    dbgN++; dbgSum0 += Math.Abs(e0); dbgSum1 += Math.Abs(e1); if (Math.Abs(e0) > 2) dbgBig++;
                    jys = new List<double>(pyl.Count);
                    for (int q = 0; q < pyl.Count; q++) { double t = (double)q / (pyl.Count - 1); jys.Add(pyl[q] + dy + e0 * (1 - t) + e1 * t); }
                }
                graph.Add(new GEdge { A = ua, B = ub, Flags = hid ? 16 : 0, Cls = 3, Speed = 40, Pts = lpts.Select(Tf).ToList(), Ys = jys, Jid = jid, Wq = 30 });
                if (plane.TryGetValue((a, b), out var ln)) lanesOf[(ua, ub, jid)] = ln;
            }
        }
        log($"junction height anchor: {dbgN} paths, mean start error {dbgSum0 / Math.Max(1, dbgN):0.00} m, end {dbgSum1 / Math.Max(1, dbgN):0.00} m, {dbgBig} over 2 m");
        log($"prefabs used: {npref} ({nonav} with unreadable nav data, {closedBorders} closed border crossings left out); {graph.Count} graph edges");

        // Self-check one-way directions: at a node that junction paths only enter (or only leave), the road attached to it must
        // leave (or enter). Look types that disagree overall are flipped.
        var outP = new Dictionary<ulong, int>(); var inP = new Dictionary<ulong, int>();
        foreach (var e in graph)
            if (e.Jid != 0) { outP[e.A] = outP.GetValueOrDefault(e.A) + 1; inP[e.B] = inP.GetValueOrDefault(e.B) + 1; }
        int Vote(ulong n, bool enters)
        {
            int o = outP.GetValueOrDefault(n), i = inP.GetValueOrDefault(n);
            if (o > 0 && i == 0) return enters ? 1 : -1;
            if (i > 0 && o == 0) return enters ? -1 : 1;
            return 0;
        }
        var score = new Dictionary<ulong, int>();
        foreach (var (gi, look) in oneway) score[look] = score.GetValueOrDefault(look) + Vote(graph[gi].B, true) + Vote(graph[gi].A, false);
        var flip = score.Where(kv => kv.Value <= -3).Select(kv => kv.Key).ToHashSet();
        foreach (var (gi, look) in oneway)
            if (flip.Contains(look)) { var e = graph[gi]; (e.A, e.B) = (e.B, e.A); e.Pts.Reverse(); e.Ys?.Reverse(); }
        if (flip.Count > 0) log($"flipped one-way direction for {flip.Count} road types");

        // Roundabout status per placed junction. An arm only counts as connected if a drivable road, or another junction,
        // attaches to it (unused arms of a prefab often touch decorative road items).
        var roadNodes = new HashSet<ulong>();
        var jidsAt = new Dictionary<ulong, HashSet<uint>>();
        foreach (var e in graph)
        {
            if (e.Jid == 0) { roadNodes.Add(e.A); roadNodes.Add(e.B); }
            else
            {
                if (!jidsAt.TryGetValue(e.A, out var sa)) jidsAt[e.A] = sa = new(); sa.Add(e.Jid);
                if (!jidsAt.TryGetValue(e.B, out var sb)) jidsAt[e.B] = sb = new(); sb.Add(e.Jid);
            }
        }
        foreach (var (uid, unodes, origin, paths) in jctCands)
        {
            bool Conn(ulong u) => roadNodes.Contains(u) || (jidsAt.TryGetValue(u, out var set) && set.Any(j => j != uid));
            if (IsRoundabout(unodes, origin, paths, Conn)) junctions[uid] = (junctions[uid].Count, true);
        }

        // ferries / trains: link the road node of one port to the road node of the connected port
        var pfByUid = prefabs.ToDictionary(p => p.Uid);
        var ports = new Dictionary<ulong, List<ulong>>();
        foreach (var (ptok, link, fnode, train, fx, fz) in col.Ferries)
        {
            ulong? best = null;
            if (pfByUid.TryGetValue(link, out var lp))
            {
                double bd = double.MaxValue;
                foreach (var u in lp.Nodes)
                {
                    if (!nodes.TryGetValue(u, out var nd)) continue;
                    double dd = (nd.X - fx) * (nd.X - fx) + (nd.Z - fz) * (nd.Z - fz);
                    if (dd < bd) { bd = dd; best = u; }
                }
            }
            if (best != null) { if (!ports.TryGetValue(ptok, out var l)) ports[ptok] = l = new(); l.Add(best.Value); }
        }
        int nf = 0;
        foreach (var (aTok, bTok, mins) in defs.FerryConns)
        {
            if (aTok > ulong.MaxValue || bTok > ulong.MaxValue) continue; // names over 12 letters never match a 64-bit token
            if (!ports.TryGetValue((ulong)aTok, out var la) || la.Count == 0 || !ports.TryGetValue((ulong)bTok, out var lb) || lb.Count == 0) continue;
            ulong na = la[0], nb = lb[0];
            var (an, bn) = (nodes[na], nodes[nb]);
            double length = Hy(bn.X - an.X, bn.Z - an.Z);
            double secs = Math.Max(60.0, mins * 60.0 / 19.0); // game minutes -> real seconds
            int kmh = Math.Max(5, Math.Min(250, (int)(length / secs * 3.6)));
            graph.Add(new GEdge { A = na, B = nb, Flags = 1 | 8, Cls = 1, Speed = kmh, Pts = new() { (an.X, an.Z), (bn.X, bn.Z) }, Jid = 0, Wq = 8 }); // bit 3 = ferry / train link
            nf++;
        }
        log($"{nf} ferry/train links (of {defs.FerryConns.Count} connections)");

        // compact nodes: only those referenced by graph edges
        progress?.Invoke("Saving the map…", 0);
        var idx = new Dictionary<ulong, int>();
        var nx = new List<(double X, double Z)>();
        foreach (var e in graph)
            foreach (var u in new[] { e.A, e.B })
                if (!idx.ContainsKey(u) && nodes.TryGetValue(u, out var nd)) { idx[u] = nx.Count; nx.Add((nd.X, nd.Z)); }
        graph = graph.Where(e => idx.ContainsKey(e.A) && idx.ContainsKey(e.B)).ToList();
        double minx = nx.Min(p => p.X), maxx = nx.Max(p => p.X), minz = nx.Min(p => p.Z), maxz = nx.Max(p => p.Z);

        using var raw = new MemoryStream(40_000_000);
        using var w = new BinaryWriter(raw, Encoding.UTF8, true);
        w.Write(Encoding.ASCII.GetBytes("ETSM")); w.Write((ushort)FileVersion); w.Write((ushort)0);
        w.Write(F32(minx)); w.Write(F32(minz)); w.Write(F32(maxx)); w.Write(F32(maxz));
        w.Write((uint)nx.Count);
        foreach (var (x, z) in nx) { w.Write(F32(x)); w.Write(F32(z)); }
        w.Write((uint)junctions.Count);
        foreach (var (jid, (cnt, rb)) in junctions) { w.Write(jid); w.Write((byte)cnt); w.Write((byte)(rb ? 1 : 0)); }
        w.Write((uint)graph.Count);
        foreach (var e in graph)
        {
            var pts = e.Pts.Count > 255 ? e.Pts.Take(255).ToList() : e.Pts;
            w.Write((uint)idx[e.A]); w.Write((uint)idx[e.B]); w.Write((byte)e.Flags); w.Write((byte)e.Cls); w.Write((byte)(e.Speed / 5)); w.Write((byte)e.Wq);
            w.Write(e.Jid); w.Write((byte)pts.Count);
            var start = nx[idx[e.A]];
            int qx = Q4(F32(start.X)), qz = Q4(F32(start.Z));
            foreach (var (x, z) in pts)
            {
                int px = Q4(x), pz = Q4(z);
                int dx = Math.Max(-32768, Math.Min(32767, px - qx)), dz = Math.Max(-32768, Math.Min(32767, pz - qz));
                w.Write((short)dx); w.Write((short)dz);
                qx += dx; qz += dz;
            }
        }
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); if (b.Length > 255) b = b[..255]; w.Write((byte)b.Length); w.Write(b); }
        var loc = Defs.LoadLocale(fs);
        var cl = new List<(string Id, string Name, double X, double Z)>();
        var seenCity = new HashSet<ulong>();
        foreach (var (tok, x, z) in cities)
            if (defs.Cities.TryGetValue(tok, out var c) && seenCity.Add(tok))
            {
                string name = loc.TryGetValue(c.Loc, out var ln) && ln.Length > 0 ? ln : c.Name;
                cl.Add((c.Id, name.Replace("�", ""), x, z));
            }
        w.Write((uint)cl.Count);
        foreach (var (id, name, x, z) in cl) { Str(id); Str(name); w.Write(F32(x)); w.Write(F32(z)); }
        var comp = new Dictionary<(ulong, ulong, long, long), (string, string, double, double)>();
        foreach (var (co, ci, x, z) in col.Companies)
        {
            var key = (co, ci, (long)Math.Round(x, MidpointRounding.ToEven), (long)Math.Round(z, MidpointRounding.ToEven));
            comp.TryAdd(key, (Sii.TokenToStr(co), Sii.TokenToStr(ci), x, z));
        }
        w.Write((uint)comp.Count);
        foreach (var (cid, city, x, z) in comp.Values) { Str(cid); Str(city); w.Write(F32(x)); w.Write(F32(z)); }
        log($"{comp.Count} company locations");
        if (dropsPath != null) WriteDrops(dropsPath, comp.Values, yards, log);
        pois.AddRange(col.Overlays);
        foreach (var (_, _, _, train, fx, fz) in col.Ferries) pois.Add((train ? 10 : 9, fx, fz));   // ferry and car-train terminals
        var grid = new Dictionary<(int, long, long), List<(double X, double Z)>>();
        var uniq = new List<(int T, double X, double Z)>();
        foreach (var (t, x, z) in pois)
        {
            long gx = (long)Math.Floor(x / 30), gz = (long)Math.Floor(z / 30);
            bool dup = false;
            for (int ddx = -1; ddx <= 1 && !dup; ddx++)
                for (int ddz = -1; ddz <= 1 && !dup; ddz++)
                    if (grid.TryGetValue((t, gx + ddx, gz + ddz), out var l) && l.Any(u => Math.Abs(x - u.X) < 30 && Math.Abs(z - u.Z) < 30)) dup = true;
            if (dup) continue;
            if (!grid.TryGetValue((t, gx, gz), out var cell)) grid[(t, gx, gz)] = cell = new();
            cell.Add((x, z)); uniq.Add((t, x, z));
        }
        w.Write((uint)uniq.Count);
        foreach (var (t, x, z) in uniq) { w.Write((byte)t); w.Write(F32(x)); w.Write(F32(z)); }
        // lane table (file version 8): junction edge index, lanes at the entry, mask of lanes that take this path
        var laneRows = new List<(int Gi, int N, int Mask)>();
        for (int gi = 0; gi < graph.Count; gi++)
        {
            var e = graph[gi];
            if (e.Jid != 0 && lanesOf.TryGetValue((e.A, e.B, e.Jid), out var li) && li.N >= 2) laneRows.Add((gi, li.N, li.Mask));
        }
        w.Write((uint)laneRows.Count);
        foreach (var (gi, n, m) in laneRows) { w.Write((uint)gi); w.Write((byte)n); w.Write((ushort)m); }
        log($"lane data for {laneRows.Count} junction paths");

        // file version 9: names shown for companies, road-number shields, and the map areas (lots, yards, green patches)
        var usedCo = comp.Values.Select(v => v.Item1).Distinct().Where(id => defs.CompanyNames.ContainsKey(id)).ToList();
        w.Write((uint)usedCo.Count);
        foreach (var id in usedCo) { Str(id); Str(defs.CompanyNames[id]); }
        var labels = new List<(string Text, double X, double Z)>();
        var labelGrid = new HashSet<(string, long, long)>();
        foreach (var (tok, x, z) in col.RoadLabels.OrderBy(l => l.Tok).ThenBy(l => l.X).ThenBy(l => l.Z))
        {
            if (tok > ulong.MaxValue) continue;
            string? text = RoadLabelText(Sii.TokenToStr(tok));
            if (text == null) continue;
            long gx = (long)Math.Floor(x / 600), gz = (long)Math.Floor(z / 600);
            bool dup = false;
            for (int ddx = -1; ddx <= 1 && !dup; ddx++) for (int ddz = -1; ddz <= 1 && !dup; ddz++) dup = labelGrid.Contains((text, gx + ddx, gz + ddz));
            if (dup) continue;
            labelGrid.Add((text, gx, gz)); labels.Add((text, x, z));
        }
        w.Write((uint)labels.Count);
        foreach (var (text, x, z) in labels) { Str(text); w.Write(F32(x)); w.Write(F32(z)); }
        var areas = new List<(uint Color, List<(float X, float Z)> Pts)>();
        foreach (var (anodes, color) in col.Areas)
        {
            var pts = new List<(float X, float Z)>();
            foreach (var u in anodes) if (nodes.TryGetValue(u, out var nd)) pts.Add((F32(nd.X), F32(nd.Z)));
            if (pts.Count >= 3 && color <= 3) areas.Add((color, pts));
        }
        int itemAreas = areas.Count;
        areas.AddRange(prefabAreas);   // drawn over the map areas, like in the game
        w.Write((uint)areas.Count);
        foreach (var (color, pts) in areas)
        {
            w.Write((byte)color); w.Write((ushort)pts.Count);
            foreach (var (x, z) in pts) { w.Write(x); w.Write(z); }
        }
        log($"{usedCo.Count} company names, {labels.Count} road numbers, {itemAreas} map areas, {prefabAreas.Count} prefab shapes");
        // border crossings (checkpoint symbol, or the flag of the country being entered) and each city's country (for its flag)
        var borders = new List<(byte Kind, string Cc, double X, double Z)>();
        var borderSeen = new HashSet<(string, long, long)>();
        foreach (var (tok, x, z) in col.RoadLabels.OrderBy(l => l.Tok).ThenBy(l => l.X).ThenBy(l => l.Z))
        {
            if (tok > ulong.MaxValue) continue;
            string t = Sii.TokenToStr(tok);
            if (!t.StartsWith("border_")) continue;
            string cc = t[7..];
            byte kind = cc == "ico" ? (byte)0 : (byte)1;
            if (kind == 0) cc = "";
            else if (cc.Length < 2 || cc.Length > 3) continue;
            long gx = (long)Math.Floor(x / 300), gz = (long)Math.Floor(z / 300);
            if (!borderSeen.Add((cc, gx, gz))) continue;
            borders.Add((kind, cc, x, z));
        }
        w.Write((uint)borders.Count);
        foreach (var (kind, cc, x, z) in borders) { w.Write(kind); Str(cc); w.Write(F32(x)); w.Write(F32(z)); }
        var cityCc = new List<(string Id, string Cc)>();
        foreach (var c in defs.Cities.Values)
            if (defs.CountryIso.TryGetValue(c.Country, out var iso) && AssetPack.Iso3To2.TryGetValue(iso, out var a2)) cityCc.Add((c.Id, a2));
        w.Write((uint)cityCc.Count);
        foreach (var (id, a2) in cityCc) { Str(id); Str(a2); }
        log($"{borders.Count} border markers, {cityCc.Count} city countries");
        // file version 10: how far each road point is above the road it crosses (flyovers, bridges, stacked interchanges)
        progress?.Invoke("Finding flyovers and bridges…", 0);
        var liftEdges = new List<Lift.Edge>(graph.Count);
        foreach (var e in graph)
        {
            int cnt = Math.Min(255, e.Pts.Count);
            bool ok = e.Ys != null && e.Ys.Count >= cnt && cnt >= 2;
            liftEdges.Add(new Lift.Edge
            {
                A = e.A, B = e.B,
                X = ok ? e.Pts.Take(cnt).Select(p => p.X).ToArray() : Array.Empty<double>(),
                Z = ok ? e.Pts.Take(cnt).Select(p => p.Z).ToArray() : Array.Empty<double>(),
                Y = ok ? e.Ys!.Take(cnt).ToArray() : Array.Empty<double>(),
            });
        }
        var lifts = Lift.Compute(liftEdges);
        int liftedEdges = lifts.Count(l => l != null);
        w.Write((uint)liftedEdges);
        for (int gi = 0; gi < lifts.Length; gi++)
            if (lifts[gi] is { } lb) { w.Write((uint)gi); w.Write(lb); }
        log($"{liftedEdges} road pieces on flyovers / bridges");
        if (assetsPath != null)
        {
            try { AssetPack.Build(fs, assetsPath, log); }
            catch (Exception e) { log("  ! logos / flags failed: " + e.Message); }
        }
        w.Flush();

        using (var fsOut = File.Create(outPath))
        using (var z = new ZLibStream(fsOut, CompressionLevel.SmallestSize))
            z.Write(raw.GetBuffer(), 0, (int)raw.Length);
        log($"wrote {outPath}: {new FileInfo(outPath).Length / 1e6:0.0} MB in {(DateTime.UtcNow - t0).TotalSeconds:0}s; {nx.Count} nodes, {graph.Count} edges, {cl.Count} cities");
    }
}
