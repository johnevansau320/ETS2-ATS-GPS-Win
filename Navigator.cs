using System.Globalization;

namespace Ets2Gps;

public enum Turn { KeepLeft, KeepRight, BearLeft, BearRight, TurnLeft, TurnRight, UTurn, Roundabout, Arrive }

/// <summary>One lane of the approach: direction it takes (degrees, + = right; null = unknown), whether the route uses it, an optional second direction.</summary>
public sealed record Lane(double? Angle, bool Preferred, double? Alt);

public sealed class Maneuver
{
    public Turn Turn;
    public float AtM;          // distance along the route
    public int ExitNo;         // roundabout exit number, else 0
    public uint Jid;           // junction (prefab) the maneuver happens in, 0 for a ring built from roads
    public bool Exit;          // a ramp / exit road leaving a bigger road (spoken as "take the exit on the right")
    public float AngleDeg;     // signed turn angle, + = right
    public float X, Z;         // position of the junction entry
    public string? Towards;    // next town the route heads for after this maneuver
    public List<Lane>? Lanes;
}

public sealed class LaneHint { public float AtM; public List<Lane> Lanes = new(); }

public sealed class NavState
{
    public float DistToNextM;
    public Maneuver? Next, Following;
    public float RemainingM, RemainingS;
    public bool OffRoute, Arrived;
    public bool OnRoute;   // the truck is on the route right now (OffRoute only turns true after a few seconds away from it)
    public string? Speech;
    public List<Lane>? Lanes;
    public float LanesDistM;
}

/// <summary>Follows a route: maneuvers, lane guidance, progress and spoken prompts (port of Navigator.kt).</summary>
public sealed class Navigator
{
    public const float OffRouteM = 45f;
    public float DistScale = 19f;
    public string ArriveWord = "your destination";

    readonly MapData map;
    readonly Router router;
    int[] route = Array.Empty<int>();
    float[] cum = Array.Empty<float>(), secs = Array.Empty<float>();
    public List<Maneuver> Maneuvers { get; private set; } = new();
    List<LaneHint> laneHints = new();
    int[] stage = Array.Empty<int>();
    int idx, offCount;
    float progress;

    public Navigator(MapData map, Router router) { this.map = map; this.router = router; }

    public bool Active => route.Length > 0;
    public int CurrentIndex => idx;
    public float RouteLengthM => cum.Length > 0 ? cum[^1] : 0f;

    /// <summary>Speed the router assumes for the road the truck is on now (m/s), 0 for junction paths.</summary>
    public float ModelSpeedMps
    {
        get
        {
            if (route.Length == 0) return 0f;
            int e = route[Math.Clamp(idx, 0, route.Length - 1)] >> 1;
            return map.EJid[e] != 0 ? 0f : map.ESpeed[e] / 3.6f;
        }
    }

    public void Clear()
    {
        route = Array.Empty<int>(); cum = Array.Empty<float>(); secs = Array.Empty<float>();
        Maneuvers = new(); laneHints = new(); stage = Array.Empty<int>();
        idx = 0; progress = 0f; offCount = 0;
    }

    /// <summary>remainingM = how much of the first edge was still ahead when the route was computed.</summary>
    // Prompts already given are remembered for a short while, so a recalculated route (the truck was off the road for a moment) does not read the same prompt out again.
    readonly Dictionary<(Turn, int, int), (int Stage, long At)> memo = new();
    static (Turn, int, int) MemoKey(Maneuver mv) => (mv.Turn, (int)MathF.Round(mv.X / 15f), (int)MathF.Round(mv.Z / 15f));

    public void SetRoute(RouteResult res, float remainingM, bool reroute = false)
    {
        route = res.Edges;
        int n = route.Length;
        cum = new float[n + 1]; secs = new float[n + 1];
        for (int i = 0; i < n; i++)
        {
            cum[i + 1] = cum[i] + map.ELen[route[i] >> 1];
            secs[i + 1] = secs[i] + router.EdgeSeconds(route[i]);
        }
        idx = 0;
        progress = map.ELen[route[0] >> 1] - remainingM;
        offCount = 0;
        Maneuvers = BuildManeuvers();
        AssignTowards();
        if (!reroute) memo.Clear();
        long now = Environment.TickCount64;
        stage = Maneuvers.Select(mv =>
        {
            int s = mv.AtM < progress ? 3 : 0;   // nothing behind the start gets announced
            if (reroute && memo.TryGetValue(MemoKey(mv), out var m) && now - m.At < 90_000) s = Math.Max(s, m.Stage);
            return s;
        }).ToArray();
    }

    float Tangent(int de, bool atEnd, float win = 6f)
    {
        int n = map.DeCount(de);
        if (n < 2) return 0f;
        if (atEnd)
        {
            float px = map.DeX(de, n - 1), pz = map.DeZ(de, n - 1);
            for (int i = n - 2; i >= 0; i--)
            {
                float qx = map.DeX(de, i), qz = map.DeZ(de, i);
                if (M.Hypot(px - qx, pz - qz) >= win || i == 0) return Router.BearingOf(px - qx, pz - qz);
            }
        }
        else
        {
            float px = map.DeX(de, 0), pz = map.DeZ(de, 0);
            for (int i = 1; i < n; i++)
            {
                float qx = map.DeX(de, i), qz = map.DeZ(de, i);
                if (M.Hypot(qx - px, qz - pz) >= win || i == n - 1) return Router.BearingOf(qx - px, qz - pz);
            }
        }
        return 0f;
    }

    static float DegDiff(float a, float b) => Router.AngleDiff(a, b) * 180f / MathF.PI;

    /// <summary>Total turning along a directed edge in degrees, + = right, NOT wrapped to +-180 (a 270 degree loop ramp counts as 270).</summary>
    float CumTurn(int de, float win = 3f)
    {
        int n = map.DeCount(de);
        if (n < 3) return 0f;
        float tot = 0f, prev = float.NaN, lx = map.DeX(de, 0), lz = map.DeZ(de, 0);
        for (int i = 1; i < n; i++)
        {
            float x = map.DeX(de, i), z = map.DeZ(de, i);
            if (M.Hypot(x - lx, z - lz) < win && i != n - 1) continue;
            float h = Router.BearingOf(x - lx, z - lz);
            if (!float.IsNaN(prev)) tot += DegDiff(h, prev);
            prev = h; lx = x; lz = z;
        }
        return tot;
    }

    // ------------------------------------------------------------------------ roundabouts the map builder did not flag --
    // The builder flags a junction as a roundabout only when enough of its arms are connected to roads. A junction that has the shape of one anyway
    // (every straight-on path is forced to bend around a central island) is treated as a roundabout here too.
    readonly Dictionary<uint, bool> geoRb = new();

    bool IsRoundabout(uint jid, int entry, List<(int De, float Angle)> alts)
    {
        if (map.Roundabouts.Contains(jid)) return true;
        if (geoRb.TryGetValue(jid, out var known)) return known;
        var nodes = new HashSet<int> { entry };
        foreach (var a in alts) nodes.Add(map.DeTo(a.De));
        bool seenStraight = false, rb = true;
        foreach (int node in nodes)
        {
            for (int i = 0; i < router.OutCount(node) && rb; i++)
            {
                int j = router.OutAt(node, i);
                if (map.EJid[j >> 1] != jid) continue;
                int cnt = map.DeCount(j);
                if (cnt < 3) continue;
                if (MathF.Abs(DegDiff(Tangent(j, true), Tangent(j, false))) > 25f) continue;   // not carrying straight on
                float x0 = map.DeX(j, 0), z0 = map.DeZ(j, 0), x1 = map.DeX(j, cnt - 1), z1 = map.DeZ(j, cnt - 1);
                float chord = M.Hypot(x1 - x0, z1 - z0);
                if (chord < 12f) continue;
                float bulge = 0f;
                for (int q = 0; q < cnt; q++) bulge = MathF.Max(bulge, MathF.Abs((map.DeX(j, q) - x0) * (z1 - z0) - (map.DeZ(j, q) - z0) * (x1 - x0)) / chord);
                if (bulge < 4f) rb = false;   // one straight path is really straight: an ordinary intersection
                else seenStraight = true;
            }
        }
        bool result = rb && seenStraight && nodes.Count >= 3;
        geoRb[jid] = result;
        return result;
    }

    // Countries that drive on the left: their roundabouts go round clockwise, so the 1st exit is the leftmost one, not the rightmost.
    static readonly HashSet<string> LeftHandTraffic = new(StringComparer.OrdinalIgnoreCase) { "gb", "uk", "ie", "mt", "cy", "im", "gg", "je" };

    /// <summary>Does traffic go round this roundabout prefab clockwise? The paths through it get longer exit by exit, so when their lengths line
    /// up with the exits taken in one direction, that is the answer; otherwise it goes by the side of the road the country drives on.</summary>
    bool Clockwise(int entry, List<(int De, float Angle)> alts)
    {
        var byLen = alts.OrderBy(a => map.ELen[a.De >> 1]).Select(a => a.De).ToList();
        if (byLen.SequenceEqual(alts.OrderBy(a => a.Angle).Select(a => a.De))) return true;
        if (byLen.SequenceEqual(alts.OrderByDescending(a => a.Angle).Select(a => a.De))) return false;
        return map.CountryAt(map.NodeX[entry], map.NodeZ[entry]) is string cc && LeftHandTraffic.Contains(cc);
    }

    // ------------------------------------------------------------------------ roundabouts made of ordinary roads --
    // Many roundabouts are not one prefab: they are a ring of short curved road pieces (and small junction pieces) that the router
    // sees as a closed loop of directed edges. A ring is a closed loop of at most 14 edges, 80 to 260 m around, that keeps curving
    // the same way for about one full turn.

    sealed class Ring { public int[] Edges = Array.Empty<int>(); public HashSet<int> Set = new(); }
    readonly Dictionary<int, Ring?> ringOf = new();

    Ring? FindRing(int de0)
    {
        if (ringOf.TryGetValue(de0, out var cached)) return cached;
        Ring? found = null;
        float len0 = map.ELen[de0 >> 1];
        if (len0 <= 120f && MathF.Abs(CumTurn(de0)) >= 8f)
        {
            int start = map.DeFrom(de0);
            var path = new List<int> { de0 };
            Dfs(start, de0, path, len0, CumTurn(de0), ref found);
        }
        ringOf[de0] = found;
        return found;
    }

    /// <summary>The ring's junction points lie roughly on a circle (about 10 to 45 m across): a roundabout, not a block of streets.</summary>
    bool Circular(List<int> path)
    {
        float cx = 0, cz = 0;
        foreach (int e in path) { int nd = map.DeFrom(e); cx += map.NodeX[nd]; cz += map.NodeZ[nd]; }
        cx /= path.Count; cz /= path.Count;
        float mn = float.MaxValue, mx = 0, sum = 0;
        foreach (int e in path) { int nd = map.DeFrom(e); float r = M.Hypot(map.NodeX[nd] - cx, map.NodeZ[nd] - cz); mn = MathF.Min(mn, r); mx = MathF.Max(mx, r); sum += r; }
        float mean = sum / path.Count;
        return mean >= 10f && mean <= 45f && mx <= 1.45f * MathF.Max(mn, 1f);
    }

    void Dfs(int start, int last, List<int> path, float length, float turn, ref Ring? found)
    {
        if (found != null || path.Count > 14) return;
        int cur = map.DeTo(last);
        float endH = Tangent(last, true);
        for (int i = 0; i < router.OutCount(cur) && found == null; i++)
        {
            int nx = router.OutAt(cur, i);
            if (path.Contains(nx)) continue;
            float len = length + map.ELen[nx >> 1];
            if (len > 260f) continue;
            float dh = DegDiff(Tangent(nx, false), endH);
            if (MathF.Abs(dh) > 75f) continue;
            float t = turn + dh + CumTurn(nx);
            path.Add(nx);
            if (map.DeTo(nx) == start && path.Count >= 3)
            {
                float close = DegDiff(Tangent(path[0], false), Tangent(nx, true));
                float tot = t + close;
                if (len >= 80f && MathF.Abs(tot) >= 300f && MathF.Abs(tot) <= 470f)
                {
                    float sg = tot > 0 ? 1f : -1f; bool ok = true; int curved = 0;
                    foreach (int e in path) { float c = CumTurn(e) * sg; if (c < -12f) ok = false; if (c > 8f) curved++; }
                    if (ok && curved >= (path.Count > 8 ? 3 : 2) && Circular(path)) found = new Ring { Edges = path.ToArray(), Set = new HashSet<int>(path) };
                }
            }
            else Dfs(start, nx, path, len, t, ref found);
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>Finds roundabouts on the route: (first route index, last route index, ring, entry node, exit node).</summary>
    List<(int K0, int K1, Ring Ring)> RingSpans()
    {
        var spans = new List<(int, int, Ring)>();
        int n = route.Length;
        for (int k = 1; k < n - 1; k++)
        {
            var ring = FindRing(route[k]);
            if (ring == null) continue;
            int k1 = k;
            while (k1 + 1 < n - 1 && ring.Set.Contains(route[k1 + 1])) k1++;
            int k0 = k;
            while (k0 - 1 >= 1 && ring.Set.Contains(route[k0 - 1])) k0--;
            spans.Add((k0, k1, ring));
            k = k1;
        }
        return spans;
    }

    List<Maneuver> BuildManeuvers()
    {
        var outList = new List<Maneuver>();
        var hints = new List<LaneHint>();
        int n = route.Length;
        // roundabouts built from ordinary road pieces: one instruction for the whole ring, nothing for the pieces inside it
        var inRing = new bool[n];
        foreach (var (k0, k1, ring) in RingSpans())
        {
            int k0e = k0 - 1 >= 1 && map.EJid[route[k0 - 1] >> 1] != 0 && !ring.Set.Contains(route[k0 - 1]) ? k0 - 1 : k0;
            int k1e = k1 + 1 < n - 1 && map.EJid[route[k1 + 1] >> 1] != 0 && !ring.Set.Contains(route[k1 + 1]) ? k1 + 1 : k1;
            int idx0 = Array.IndexOf(ring.Edges, route[k0]), count = 0;
            for (int j = 0; j < ring.Edges.Length; j++)
            {
                int re = ring.Edges[(idx0 + j) % ring.Edges.Length];
                if (router.OutCount(map.DeTo(re)) >= 2) count++;
                if (re == route[k1]) break;
            }
            for (int q = k0e; q <= k1e; q++) inRing[q] = true;
            float ang = DegDiff(Tangent(route[k1e], true), Tangent(route[k0e - 1], true));
            outList.Add(new Maneuver { Turn = Turn.Roundabout, Jid = 0, AtM = cum[k0e], ExitNo = Math.Max(1, count), AngleDeg = ang, X = map.DeX(route[k0e], 0), Z = map.DeZ(route[k0e], 0) });
        }
        for (int k = 1; k < n - 1; k++)
        {
            if (inRing[k]) continue;
            int de = route[k], e = de >> 1;
            uint jid = map.EJid[e];
            if (jid == 0) continue;
            int entry = map.DeFrom(de), cameFrom = map.DeFrom(route[k - 1]);
            var alts = new List<(int De, float Angle)>();
            for (int i = 0; i < router.OutCount(entry); i++)
            {
                int j = router.OutAt(entry, i);
                if (map.EJid[j >> 1] != jid || map.DeTo(j) == cameFrom) continue;
                alts.Add((j, DegDiff(Tangent(j, true), Tangent(j, false))));
            }
            if (alts.Count < 2) continue;
            int ci = alts.FindIndex(a => a.De == de);
            if (ci < 0) continue;
            float angle = alts[ci].Angle;
            var straightest = alts[0];
            foreach (var a in alts) if (MathF.Abs(a.Angle) < MathF.Abs(straightest.Angle)) straightest = a;
            float x = map.DeX(de, 0), z = map.DeZ(de, 0);
            Maneuver Mk(Turn t, int exitNo) => new() { Turn = t, AtM = cum[k], ExitNo = exitNo, AngleDeg = angle, X = x, Z = z, Jid = jid };
            if (IsRoundabout(jid, entry, alts))
            {
                var sorted = Clockwise(entry, alts) ? alts.OrderBy(a => a.Angle).ToList() : alts.OrderByDescending(a => a.Angle).ToList();
                int no = sorted.FindIndex(a => a.De == de) + 1;
                outList.Add(Mk(Turn.Roundabout, no));
                continue;
            }
            float ab = MathF.Abs(angle); bool right = angle > 0;
            int before = outList.Count;
            var laneList = LanesFor(alts, de);
            // a ramp that loops more than half a turn (up to 270 degrees) ends up pointing the "other" way once the angle is wrapped to +-180:
            // judge which way it LEAVES the road instead (compare its start with the straight-on road)
            if (straightest.De != de && MathF.Abs(CumTurn(de)) > 160f && MathF.Abs(straightest.Angle) < 40f)
            {
                bool leavesRight = DegDiff(Tangent(de, false, 15f), Tangent(straightest.De, false, 15f)) > 0;
                var mv = Mk(leavesRight ? Turn.BearRight : Turn.BearLeft, 0); mv.Exit = true;
                outList.Add(mv);
            }
            else if (ab < 22f) { if (straightest.De != de) outList.Add(Mk(right ? Turn.KeepRight : Turn.KeepLeft, 0)); }
            else if (ab < 55f) outList.Add(Mk(right ? Turn.BearRight : Turn.BearLeft, 0));
            else if (ab < 135f) outList.Add(Mk(right ? Turn.TurnRight : Turn.TurnLeft, 0));
            else outList.Add(Mk(Turn.UTurn, 0));
            if (outList.Count > before) outList[^1].Lanes = laneList;
            // also hint junctions where the route just carries straight on but some lanes would leave it
            if (laneList != null && (outList.Count > before || laneList.Any(l => !l.Preferred)))
                hints.Add(new LaneHint { AtM = cum[k], Lanes = laneList });
        }
        outList.Sort((a, b) => a.AtM.CompareTo(b.AtM));
        int last = route[n - 1], cnt = map.DeCount(last);
        outList.Add(new Maneuver { Turn = Turn.Arrive, AtM = cum[n], X = map.DeX(last, cnt - 1), Z = map.DeZ(last, cnt - 1) });
        laneHints = hints;
        return outList;
    }

    /// <summary>Lane arrows for the approach to junction path de, from the map's lane table (null when unknown).</summary>
    List<Lane>? LanesFor(List<(int De, float Angle)> alts, int de)
    {
        int n = map.LanesAtEntry(de >> 1), chosen = map.LaneMaskOf(de >> 1);
        if (n < 2 || chosen == 0) return null;
        float mine = alts.First(a => a.De == de).Angle;
        var res = new List<Lane>();
        for (int i = 0; i < n; i++)
        {
            var dirs = alts.Where(a => ((map.LaneMaskOf(a.De >> 1) >> i) & 1) != 0).ToList();
            if (dirs.Count == 0) { res.Add(new Lane(null, false, null)); continue; }
            bool pref = ((chosen >> i) & 1) != 0;
            float main = mine;
            if (!pref) { var b = dirs[0]; foreach (var d in dirs) if (MathF.Abs(d.Angle) < MathF.Abs(b.Angle)) b = d; main = b.Angle; }
            float? other = null;
            foreach (var d in dirs) if (MathF.Abs(d.Angle - main) > 20f && (other == null || MathF.Abs(d.Angle) < MathF.Abs(other.Value))) other = d.Angle;
            res.Add(new Lane(main, pref, other));
        }
        return res;
    }

    /// <summary>For each maneuver, the first town the route passes after it (within 2 km of the road).</summary>
    void AssignTowards()
    {
        int nc = map.Cities.Count;
        var passAt = new float[nc]; var bestD = new float[nc];
        Array.Fill(passAt, float.MaxValue); Array.Fill(bestD, float.MaxValue);
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (int de in route)
            for (int i = 0; i < map.DeCount(de); i++)
            {
                float x = map.DeX(de, i), z = map.DeZ(de, i);
                if (x < minX) minX = x; if (x > maxX) maxX = x; if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
        var cand = new List<int>();
        for (int ci = 0; ci < nc; ci++)
        {
            var c = map.Cities[ci];
            if (c.X > minX - 2000f && c.X < maxX + 2000f && c.Z > minZ - 2000f && c.Z < maxZ + 2000f) cand.Add(ci);
        }
        for (int k = 0; k < route.Length; k++)
        {
            int de = route[k], n = map.DeCount(de);
            float len = map.ELen[de >> 1];
            for (int i = 0; i < n; i += 2)
            {
                float x = map.DeX(de, i), z = map.DeZ(de, i);
                foreach (int ci in cand)
                {
                    var c = map.Cities[ci];
                    float dx = c.X - x, dz = c.Z - z;
                    if (dx > 2000f || dx < -2000f || dz > 2000f || dz < -2000f) continue;
                    float d = M.Hypot(dx, dz);
                    if (d < 2000f && d < bestD[ci]) { bestD[ci] = d; passAt[ci] = cum[k] + len * i / Math.Max(1, n - 1); }
                }
            }
        }
        foreach (var mv in Maneuvers)
        {
            if (mv.Turn == Turn.Arrive) continue;
            int best = -1;
            foreach (int ci in cand) if (passAt[ci] > mv.AtM + 250f && (best < 0 || passAt[ci] < passAt[best])) best = ci;
            if (best >= 0) mv.Towards = map.Cities[best].Name;
        }
    }

    (float Dist, float Along) Project(int de, float x, float z)
    {
        float best = float.MaxValue, bestAlong = 0f, cumLen = 0f;
        int n = map.DeCount(de);
        for (int i = 0; i < n - 1; i++)
        {
            float x0 = map.DeX(de, i), z0 = map.DeZ(de, i), x1 = map.DeX(de, i + 1), z1 = map.DeZ(de, i + 1);
            float dx = x1 - x0, dz = z1 - z0, l2 = dx * dx + dz * dz, seg = MathF.Sqrt(l2);
            float t = l2 > 1e-6f ? ((x - x0) * dx + (z - z0) * dz) / l2 : 0f;
            t = Math.Clamp(t, 0f, 1f);
            float d = M.Hypot(x - (x0 + dx * t), z - (z0 + dz * t));
            if (d < best) { best = d; bestAlong = cumLen + seg * t; }
            cumLen += seg;
        }
        return (best, bestAlong);
    }

    /// <summary>Feed the latest truck position; returns guidance for this instant.</summary>
    public NavState Update(float x, float z, float speedMps)
    {
        if (route.Length == 0) return new NavState();
        int bestK = idx; float bestD = float.MaxValue, bestAlong = 0f;
        for (int k = Math.Max(0, idx - 1); k <= Math.Min(route.Length - 1, idx + 10); k++)
        {
            var p = Project(route[k], x, z);
            float score = p.Dist + Math.Abs(k - idx) * 0.5f;
            if (score < bestD) { bestD = score; bestK = k; bestAlong = p.Along; }
        }
        float rawDist = Project(route[bestK], x, z).Dist;
        if (rawDist > OffRouteM) offCount++; else offCount = 0;
        bool offRoute = offCount >= 8;
        if (rawDist <= OffRouteM)
        {
            idx = bestK;
            float p = cum[idx] + bestAlong;
            progress = p >= progress - 5f ? MathF.Max(progress, p) : p; // ignore tiny backward jitter
        }
        float remaining = MathF.Max(0f, cum[route.Length] - progress);
        bool arrived = remaining < 25f;
        float remS = MathF.Max(0f, secs[route.Length] - (secs[idx] + (progress - cum[idx]) / map.ELen[route[idx] >> 1] * (secs[idx + 1] - secs[idx])));
        int ni = -1;
        for (int i = 0; i < Maneuvers.Count; i++) if (Maneuvers[i].AtM > progress - 12f) { ni = i; break; }
        var next = ni >= 0 ? Maneuvers[ni] : null;
        var following = ni >= 0 && ni + 1 < Maneuvers.Count ? Maneuvers[ni + 1] : null;
        float toNext = next != null ? MathF.Max(0f, next.AtM - progress) : 0f;
        string? speech = null;
        if (next != null && !offRoute)
        {
            float tau = MathF.Max(speedMps, 4f);
            float d1 = MathF.Max(tau * 26f, 300f), d2 = MathF.Max(tau * 10f, 110f), d3 = MathF.Max(tau * 3.5f, 35f);
            int want = toNext <= d3 ? 3 : toNext <= d2 ? 2 : toNext <= d1 ? 1 : 0;
            if (want > stage[ni]) { stage[ni] = want; memo[MemoKey(next)] = (want, Environment.TickCount64); speech = Phrase(next, following, toNext, want); }
        }
        var hint = laneHints.FirstOrDefault(h => h.AtM > progress - 12f);
        return new NavState
        {
            DistToNextM = toNext, Next = next, Following = following, RemainingM = remaining, RemainingS = remS,
            OffRoute = offRoute, OnRoute = rawDist <= OffRouteM, Arrived = arrived, Speech = speech,
            Lanes = hint?.Lanes, LanesDistM = hint != null ? MathF.Max(0f, hint.AtM - progress) : 0f,
        };
    }

    string Phrase(Maneuver mv, Maneuver? then, float dist, int stg)
    {
        string baseText = Instruction(mv);
        string tail = (then != null && then.Turn != Turn.Arrive && then.AtM - mv.AtM < 320f && mv.Turn != Turn.Arrive) ? ", then " + Instruction(then) : "";
        if (mv.Turn == Turn.Arrive)
            return stg >= 3 ? "You have arrived at " + ArriveWord : "In " + SpokenDistance(dist) + ", you will arrive at " + ArriveWord;
        if (stg >= 3) return char.ToUpperInvariant(baseText[0]) + baseText[1..] + tail;
        return "In " + SpokenDistance(dist) + ", " + baseText + (mv.Towards != null ? " towards " + mv.Towards : "") + tail;
    }

    public string Instruction(Maneuver mv) => mv.Turn switch
    {
        Turn.KeepLeft => "keep left",
        Turn.KeepRight => "keep right",
        Turn.BearLeft => mv.Exit ? "take the exit on the left" : "bear left",
        Turn.BearRight => mv.Exit ? "take the exit on the right" : "bear right",
        Turn.TurnLeft => "turn left",
        Turn.TurnRight => "turn right",
        Turn.UTurn => "make a U-turn",
        Turn.Roundabout => "at the roundabout, take the " + Ordinal(mv.ExitNo) + " exit",
        _ => "arrive at " + ArriveWord,
    };

    static string Ordinal(int n) => n switch
    {
        1 => "first", 2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth", 6 => "sixth", 7 => "seventh", 8 => "eighth",
        _ => n + "th",
    };

    public string SpokenDistance(float worldM) => Units.Spoken(worldM * DistScale);

    public string ShortDistance(float worldM) => Units.Short(worldM * DistScale);
}
