namespace Ets2Gps.MapBuilder;

/// <summary>
/// Finds the grade-separated crossings (flyovers, bridges, stacked interchanges) and gives every road point a lift:
/// how far (0.5 m units) the road is above the road it crosses. The maps draw lifted roads in front, with a shadow, and
/// raise them in the tilted 3D view.
/// </summary>
public static class Lift
{
    const double CellM = 24, HitDist = 6.5, MinGap = 4.2, MaxLift = 24, ReachM = 150, SampleM = 5;

    static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    public sealed class Edge
    {
        public ulong A, B;
        public double[] X = Array.Empty<double>(), Z = Array.Empty<double>(), Y = Array.Empty<double>();
    }

    /// <summary>Per edge: null when nothing is lifted, otherwise one byte per point (0.5 m units).</summary>
    public static byte[]?[] Compute(IReadOnlyList<Edge> edges)
    {
        int n = edges.Count;
        var result = new byte[]?[n];
        // grid of road segments
        var grid = new Dictionary<long, List<(int E, int S)>>();
            for (int e = 0; e < n; e++)
        {
            var ed = edges[e];
            for (int i = 0; i + 1 < ed.X.Length; i++)
            {
                double len = Math.Sqrt((ed.X[i + 1] - ed.X[i]) * (ed.X[i + 1] - ed.X[i]) + (ed.Z[i + 1] - ed.Z[i]) * (ed.Z[i + 1] - ed.Z[i]));
                int steps = Math.Max(1, (int)(len / 8));
                long last = long.MinValue;
                for (int k = 0; k <= steps; k++)
                {
                    double t = (double)k / steps;
                    int cx = (int)Math.Floor((ed.X[i] + (ed.X[i + 1] - ed.X[i]) * t) / CellM), cz = (int)Math.Floor((ed.Z[i] + (ed.Z[i + 1] - ed.Z[i]) * t) / CellM);
                    long key = Key(cx, cz);
                    if (key == last) continue;
                    last = key;
                    if (!grid.TryGetValue(key, out var l)) grid[key] = l = new();
                    if (l.Count == 0 || l[^1] != (e, i)) l.Add((e, i));
                }
            }
        }

        // edges meeting at a node
        var atNode = new Dictionary<ulong, List<int>>();
        for (int e = 0; e < n; e++)
        {
            if (edges[e].X.Length < 2) continue;
            foreach (ulong u in new[] { edges[e].A, edges[e].B })
            {
                if (!atNode.TryGetValue(u, out var l)) atNode[u] = l = new();
                l.Add(e);
            }
        }

        var lift = new double[]?[n];
        void Raise(int e, int i, double v)
        {
            var arr = lift[e] ??= new double[edges[e].X.Length];
            if (v > arr[i]) arr[i] = v;
        }

        // lift along the road from point i (direction dir) while it is above the reference height; continues into connected roads
        void Spread(int e, int i, int dir, double refY, double budget, int from, int depth, double prevUp = double.MaxValue)
        {
            var ed = edges[e];
            while (i >= 0 && i < ed.X.Length)
            {
                double up = ed.Y[i] - refY;
                if (up < 0.6 || up > prevUp + 0.4) return;   // back on the ground, or the road climbs again (a hill, not a ramp)
                prevUp = Math.Min(prevUp, up);
                Raise(e, i, Math.Min(MaxLift, up));
                int j = i + dir;
                if (j >= 0 && j < ed.X.Length)
                {
                    budget -= Math.Sqrt((ed.X[j] - ed.X[i]) * (ed.X[j] - ed.X[i]) + (ed.Z[j] - ed.Z[i]) * (ed.Z[j] - ed.Z[i]));
                    if (budget <= 0) return;
                    i = j;
                    continue;
                }
                if (depth >= 6) return;
                ulong node = dir > 0 ? ed.B : ed.A;
                if (!atNode.TryGetValue(node, out var next)) return;
                foreach (int e2 in next)
                {
                    if (e2 == e || e2 == from) continue;
                    var o = edges[e2];
                    if (o.A == node) Spread(e2, 0, 1, refY, budget, e, depth + 1, prevUp);
                    else if (o.B == node) Spread(e2, o.X.Length - 1, -1, refY, budget, e, depth + 1, prevUp);
                }
                return;
            }
        }

        // the lowest road passing under each point of every road: read-only, so the roads are checked in parallel (the lifting below
        // only takes maxima, and runs in the same order as before, so the map comes out the same)
        var cells = grid.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        var unders = new double[]?[n];
        Parallel.For(0, n, e =>
        {
            var ed = edges[e];
            if (ed.X.Length < 2) return;
            var under = new double[ed.X.Length];
            Array.Fill(under, double.MaxValue);
            bool any = false;
            for (int i = 0; i + 1 < ed.X.Length; i++)
            {
                double dx = ed.X[i + 1] - ed.X[i], dz = ed.Z[i + 1] - ed.Z[i];
                double len = Math.Sqrt(dx * dx + dz * dz);
                int steps = Math.Max(1, (int)(len / SampleM));
                for (int k = 0; k <= steps; k++)
                {
                    double t = (double)k / steps;
                    double px = ed.X[i] + dx * t, pz = ed.Z[i] + dz * t, py = ed.Y[i] + (ed.Y[i + 1] - ed.Y[i]) * t;
                    int cx = (int)Math.Floor(px / CellM), cz = (int)Math.Floor(pz / CellM);
                    double lowest = double.MaxValue;
                    for (int ax = -1; ax <= 1; ax++)
                        for (int az = -1; az <= 1; az++)
                        {
                            if (!cells.TryGetValue(Key(cx + ax, cz + az), out var cell)) continue;
                            foreach (var (oe, os) in cell)
                            {
                                if (oe == e) continue;
                                var o = edges[oe];
                                // a road at about the same height can never be far enough below: skip it before the distance maths
                                if (py - Math.Min(o.Y[os], o.Y[os + 1]) < MinGap - 0.001) continue;
                                double sx = o.X[os + 1] - o.X[os], sz = o.Z[os + 1] - o.Z[os];
                                double l2 = sx * sx + sz * sz;
                                double u = l2 < 1e-9 ? 0 : Math.Clamp(((px - o.X[os]) * sx + (pz - o.Z[os]) * sz) / l2, 0, 1);
                                double qx = o.X[os] + sx * u - px, qz = o.Z[os] + sz * u - pz;
                                if (qx * qx + qz * qz > HitDist * HitDist) continue;
                                double oy = o.Y[os] + (o.Y[os + 1] - o.Y[os]) * u;
                                if (py - oy >= MinGap && oy < lowest) lowest = oy;
                            }
                        }
                    if (lowest < double.MaxValue)
                    {
                        int pi = t < 0.5 ? i : i + 1;
                        if (lowest < under[pi]) under[pi] = lowest;
                        any = true;
                    }
                }
            }
            if (any) unders[e] = under;
        });

        for (int e = 0; e < n; e++)
        {
            var ed = edges[e];
            var under = unders[e];
            if (under == null) continue;
            for (int i = 0; i < under.Length; i++)
            {
                if (under[i] == double.MaxValue) continue;
                Raise(e, i, Math.Min(MaxLift, ed.Y[i] - under[i]));
                Spread(e, i, 1, under[i], ReachM, -1, 0);
                Spread(e, i, -1, under[i], ReachM, -1, 0);
            }
        }

        for (int e = 0; e < n; e++)
        {
            var arr = lift[e];
            if (arr == null) continue;
            var bytes = new byte[arr.Length];
            bool nz = false;
            for (int i = 0; i < arr.Length; i++) { int v = (int)Math.Round(arr[i] * 2); bytes[i] = (byte)Math.Clamp(v, 0, 255); if (bytes[i] > 0) nz = true; }
            if (nz) result[e] = bytes;
        }
        return result;
    }
}
