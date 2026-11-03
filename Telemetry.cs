using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ets2Gps;

public sealed class JobInfo
{
    public string SrcCity = "", SrcCompany = "", DestCity = "", DestCompany = "", Cargo = "";
    public float MassKg; public long Income;
    public float DueMin = -1f;   // game minutes left until the deadline, -1 unknown
    public int PlannedKm, Units = 1;
}

/// <summary>Extra fields for the trip / job screen. Damage values are percent.</summary>
public sealed class Detail
{
    public string TruckName = "", TrailerName = "", TrailerPlate = "", JobMarket = "";
    public float OdometerKm, FuelL, FuelCapL, ConsumptionLPerKm, RangeKm;
    public bool TrailerAttached;
    public float TrailerChassis, TrailerWheels, TrailerBody, CargoDamage;
    public float NavDistanceKm;  // the game's own remaining distance to its navigation target, game km
}

public sealed class TruckState
{
    public bool Connected, Paused;
    public string GameName = "";      // "ETS2" or "ATS" (the server's game.gameName)
    public float X, Y, Z;               // Y = altitude in map metres
    public float ReportedHeadingDeg;
    public float SpeedKmh;
    public int SpeedLimitKmh;
    public bool CruiseOn;
    public float CruiseKmh;             // the cruise control setting
    public string DestCityId = "", DestCity = "", DestCompanyId = "", DestCompany = "";
    public bool HasTrailer;
    public float FuelFraction = -1f;    // 0..1, -1 unknown
    public bool FuelWarning;
    public bool OnJob, JobDelivered, JobCancelled, JobFinished;   // the server's gameplay section (events are edge-detected by the app)
    public float NextRestMin = -1f;
    public JobInfo? Job;
    public float NavEtaMin = -1f;       // the game's own ETA in game minutes
    public float GameTimeMin = -1f;     // in-game clock as minutes since day 1 00:00 (day 1 is a Monday)
    public Detail Detail = new();
}

public static class Telemetry
{
    static readonly Regex RestRx = new(@"^\d{4}-\d{2}-(\d{2})T(\d{2}):(\d{2})", RegexOptions.Compiled);
    static readonly Regex ClockRx = new(@"^\d{4}-(\d{2})-(\d{2})T(\d{2}):(\d{2})", RegexOptions.Compiled);

    /// <summary>"0001-01-01T10:11:00" (a duration encoded as a date) -> minutes, or -1.</summary>
    public static float RestMinutes(string t)
    {
        var m = RestRx.Match(t ?? "");
        return m.Success ? (int.Parse(m.Groups[1].Value) - 1) * 1440 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) : -1f;
    }

    /// <summary>"0001-05-05T23:49:00Z" -> minutes since 0001-01-01 00:00 (a Monday), or -1.</summary>
    public static float ClockMinutes(string t)
    {
        var m = ClockRx.Match(t ?? "");
        if (!m.Success) return -1f;
        int[] cum = { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334 };
        int mo = Math.Clamp(int.Parse(m.Groups[1].Value) - 1, 0, 11);
        return (cum[mo] + int.Parse(m.Groups[2].Value) - 1) * 1440 + int.Parse(m.Groups[3].Value) * 60 + int.Parse(m.Groups[4].Value);
    }

    static readonly Dictionary<int, byte> Cp1252 = new()
    {
        [0x20AC] = 0x80, [0x201A] = 0x82, [0x0192] = 0x83, [0x201E] = 0x84, [0x2026] = 0x85, [0x2020] = 0x86, [0x2021] = 0x87, [0x02C6] = 0x88,
        [0x2030] = 0x89, [0x0160] = 0x8A, [0x2039] = 0x8B, [0x0152] = 0x8C, [0x017D] = 0x8E, [0x2018] = 0x91, [0x2019] = 0x92, [0x201C] = 0x93,
        [0x201D] = 0x94, [0x2022] = 0x95, [0x2013] = 0x96, [0x2014] = 0x97, [0x02DC] = 0x98, [0x2122] = 0x99, [0x0161] = 0x9A, [0x203A] = 0x9B,
        [0x0153] = 0x9C, [0x017E] = 0x9E, [0x0178] = 0x9F,
    };

    /// <summary>The server sometimes sends UTF-8 text decoded as Windows-1252 ("Ð¦Ðµ..."); undo that when it yields clean UTF-8.</summary>
    public static string FixText(string s)
    {
        if (!s.Any(c => (c >= 0xC2 && c <= 0xF4) || (c >= 0xDC80 && c <= 0xDCFF))) return s;
        var bytes = new List<byte>();
        foreach (char ch in s)
        {
            if (ch >= 0xDC80 && ch <= 0xDCFF) bytes.Add((byte)(ch - 0xDC00));
            else if (ch < 0x100) bytes.Add((byte)ch);
            else if (Cp1252.TryGetValue(ch, out var b)) bytes.Add(b);
            else return s;
        }
        try
        {
            var dec = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            return dec.Length < s.Length ? dec : s;
        }
        catch { return s; }
    }

    static JsonElement? Obj(JsonElement? e, string name) =>
        e is JsonElement el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : null;

    static string Str(JsonElement? o, string key)
    {
        var v = Obj(o, key);
        return v is JsonElement e && e.ValueKind == JsonValueKind.String ? FixText((e.GetString() ?? "").Trim())
            : v is JsonElement n && n.ValueKind == JsonValueKind.Number ? n.GetRawText() : "";
    }

    static double Num(JsonElement? o, string key) =>
        Obj(o, key) is JsonElement e && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0.0;

    static bool Bool(JsonElement? o, string key) => Obj(o, key) is JsonElement e && e.ValueKind == JsonValueKind.True;

    public static TruckState Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement? root = doc.RootElement;
        var game = Obj(root, "game"); var truck = Obj(root, "truck"); var pl = Obj(truck, "placement");
        var nav = Obj(root, "navigation"); var job = Obj(root, "job");
        JsonElement? tr = null; bool attached = false;
        if (Obj(root, "trailers") is JsonElement ta && ta.ValueKind == JsonValueKind.Array && ta.GetArrayLength() > 0)
        {
            tr = ta[0];
            attached = Bool(tr, "attached");
        }
        double cap = Num(truck, "fuelCapacity");
        var gp = Obj(root, "gameplay");
        string destId = Str(job, "destinationCityId");
        var s = new TruckState
        {
            Connected = Bool(game, "connected"), Paused = Bool(game, "paused"), GameName = Str(game, "gameName"),
            X = (float)Num(pl, "x"), Y = (float)Num(pl, "y"), Z = (float)Num(pl, "z"), ReportedHeadingDeg = (float)Num(pl, "heading"),
            SpeedKmh = (float)Num(truck, "speed"), SpeedLimitKmh = (int)Math.Round(Num(nav, "speedLimit")),
            CruiseOn = Bool(truck, "cruiseControlOn"), CruiseKmh = (float)Num(truck, "cruiseControlSpeed"),
            DestCityId = destId, DestCity = Str(job, "destinationCity"), DestCompanyId = Str(job, "destinationCompanyId"), DestCompany = Str(job, "destinationCompany"),
            HasTrailer = attached,
            FuelFraction = cap > 0 ? (float)(Num(truck, "fuel") / cap) : -1f,
            FuelWarning = Bool(truck, "fuelWarningOn"),
            NextRestMin = RestMinutes(Str(game, "nextRestStopTime")),
            NavEtaMin = RestMinutes(Str(nav, "estimatedTime")),
            GameTimeMin = ClockMinutes(Str(game, "time")),
            OnJob = Bool(gp, "onJob"), JobDelivered = Bool(gp, "jobDelivered"), JobCancelled = Bool(gp, "jobCancelled"), JobFinished = Bool(gp, "jobFinished"),
        };
        if (destId.Length > 0)
            s.Job = new JobInfo
            {
                SrcCity = Str(job, "sourceCity"), SrcCompany = Str(job, "sourceCompany"),
                DestCity = Str(job, "destinationCity"), DestCompany = Str(job, "destinationCompany"),
                Cargo = Str(job, "cargo"), MassKg = (float)Num(job, "cargoMass"), Income = (long)Num(job, "income"),
                DueMin = RestMinutes(Str(job, "remainingTime")), PlannedKm = (int)Math.Round(Num(job, "plannedDistanceKm")),
                Units = Math.Max(1, (int)Math.Round(Num(job, "unitCount"))),
            };
        s.Detail = new Detail
        {
            TruckName = (Str(truck, "make") + " " + Str(truck, "model")).Trim(),
            OdometerKm = (float)Num(truck, "odometer"), FuelL = (float)Num(truck, "fuel"), FuelCapL = (float)cap,
            ConsumptionLPerKm = (float)Num(truck, "fuelAverageConsumption"), RangeKm = (float)Num(truck, "fuelRange"),
            TrailerAttached = attached,
            TrailerName = attached ? (Str(tr, "brand") + " " + Str(tr, "name")).Trim() : "",
            TrailerPlate = attached ? Str(tr, "licensePlate") : "",
            TrailerChassis = attached ? (float)(Num(tr, "wearChassis") * 100) : 0f,
            TrailerWheels = attached ? (float)(Num(tr, "wearWheels") * 100) : 0f,
            TrailerBody = attached ? (float)(Num(tr, "wearBody") * 100) : 0f,
            CargoDamage = attached ? (float)(Num(tr, "cargoDamage") * 100) : 0f,
            NavDistanceKm = (float)(Num(nav, "estimatedDistance") / 1000.0), JobMarket = Str(job, "jobMarket"),
        };
        return s;
    }
}

/// <summary>Polls the TruckSim GPS telemetry server (default http://127.0.0.1:31377/api/ets2/telemetry) several times a second.</summary>
public sealed class TelemetryClient : IDisposable
{
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    readonly CancellationTokenSource stop = new();
    public Func<string> Host { get; set; } = () => "127.0.0.1";
    public event Action<TruckState>? State;
    public event Action<string>? Status;

    public void Start() => Task.Run(Loop);

    async Task Loop()
    {
        int failures = 0;
        while (!stop.IsCancellationRequested)
        {
            int delay = 130;
            try
            {
                string host = Host().Trim();
                if (host.Length == 0) { Status?.Invoke("Set the telemetry server address in Settings"); delay = 1000; }
                else
                {
                    string h = host.Contains(':') ? host : host + ":31377";
                    string json = await http.GetStringAsync($"http://{h}/api/ets2/telemetry", stop.Token);
                    var st = Telemetry.Parse(json);
                    failures = 0;
                    State?.Invoke(st);
                }
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                failures++;
                Status?.Invoke($"No telemetry server ({e.GetType().Name}) - is the TruckSim GPS server running? Retrying");
                delay = failures < 5 ? 700 : 2000;
            }
            try { await Task.Delay(delay, stop.Token); } catch { return; }
        }
    }

    public void Dispose() { stop.Cancel(); http.Dispose(); }
}

/// <summary>
/// Steady position, heading and speed from the noisy position stream (world metres / s). Heading comes from the direction of
/// travel when moving and from the reported heading when stationary; altitude gradient from height change over distance driven.
/// </summary>
public sealed class MotionFilter
{
    public float X, Z, Bearing, SpeedMps;
    float vx, vz; long lastT; bool haveFix; float maxReported;
    public float Altitude, Travelled, GradientPct; public bool GradientValid;
    readonly float[] gd = new float[96], gy = new float[96];
    int gCount, gHead; float lastPush = -1e9f;
    public bool HaveFix => haveFix;

    void PushAlt(float y)
    {
        Altitude = y;
        if (Travelled - lastPush >= 5f)
        {
            lastPush = Travelled; gd[gHead] = Travelled; gy[gHead] = y;
            gHead = (gHead + 1) % gd.Length;
            if (gCount < gd.Length) gCount++;
        }
        for (int i = 0; i < gCount; i++)
        {
            int idx = (gHead - 1 - i + gd.Length * 2) % gd.Length;
            float dd = Travelled - gd[idx];
            if (dd >= 60f)
            {
                float g = (y - gy[idx]) / dd * 100f;
                GradientPct += (g - GradientPct) * (GradientValid ? 0.3f : 1f);
                GradientValid = true;
                return;
            }
        }
    }

    float ReportedBearing(float h)
    {
        if (h > maxReported) maxReported = h;
        float deg = maxReported <= 1.0001f ? h * 360f : h;
        return ((360f - deg) % 360f) * MathF.PI / 180f; // SCS heading: 0 = north, counter-clockwise
    }

    static float Blend(float a, float b, float k) => a + Router.AngleDiff(b, a) * k;

    public void Update(TruckState s, long nowMs)
    {
        if (!s.Connected) return;
        if (!haveFix || M.Hypot(s.X - X, s.Z - Z) > 400f) // first fix or teleport
        {
            X = s.X; Z = s.Z; haveFix = true; lastT = nowMs; vx = 0; vz = 0;
            gCount = 0; gHead = 0; GradientValid = false; lastPush = -1e9f; Altitude = s.Y;
            Bearing = ReportedBearing(s.ReportedHeadingDeg);
            return;
        }
        float dt = (nowMs - lastT) / 1000f, dx = s.X - X, dz = s.Z - Z, moved = M.Hypot(dx, dz);
        if (moved < 0.01f)
        {
            if (dt > 0.8f) { vx = 0; vz = 0; SpeedMps = 0; Bearing = Blend(Bearing, ReportedBearing(s.ReportedHeadingDeg), 0.1f); lastT = nowMs; }
            return;
        }
        if (dt < 0.02f) return;
        vx += (dx / dt - vx) * 0.5f; vz += (dz / dt - vz) * 0.5f;
        SpeedMps = M.Hypot(vx, vz);
        if (moved > 0.4f && moved / dt > 1.5f) Bearing = Blend(Bearing, MathF.Atan2(dx, -dz), 0.35f);
        X = s.X; Z = s.Z; lastT = nowMs;
        Travelled += moved;
        PushAlt(s.Y);
    }

    /// <summary>Extrapolated position for smooth rendering.</summary>
    public (float X, float Z) Predicted(long nowMs)
    {
        float dt = Math.Clamp((nowMs - lastT) / 1000f, 0f, 0.4f);
        return (X + vx * dt, Z + vz * dt);
    }
}

/// <summary>Altitude samples along the trip (one every ~25 map metres), decimated when it grows large.</summary>
public sealed class ElevationLog
{
    public List<float> Dist = new(), Alt = new();
    float step = 25f, lastD = -1e9f;

    public void Add(float distance, float y)
    {
        if (distance - lastD < step) return;
        lastD = distance;
        if (Dist.Count >= 8192)
        {
            Dist = Dist.Where((_, i) => i % 2 == 0).ToList(); Alt = Alt.Where((_, i) => i % 2 == 0).ToList(); step *= 2f;
        }
        Dist.Add(distance); Alt.Add(y);
    }

    public void Clear() { Dist.Clear(); Alt.Clear(); lastD = -1e9f; step = 25f; }
    public float Ascent() { float a = 0; for (int i = 1; i < Alt.Count; i++) { float d = Alt[i] - Alt[i - 1]; if (d > 0.3f) a += d; } return a; }
    public float Descent() { float a = 0; for (int i = 1; i < Alt.Count; i++) { float d = Alt[i] - Alt[i - 1]; if (d < -0.3f) a -= d; } return a; }
}
