using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ets2Gps.MapBuilder;

namespace Ets2Gps;

/// <summary>
/// The small web server built into the app (what the separate "companion" used to do): serves the map, the web app, the lane
/// panel for the lane overlay window and the telemetry for the browser, and speaks prompts sent by the tablet on this PC.
/// Plain TCP, so it needs no administrator rights.
/// </summary>
public sealed class BuiltInServer : IDisposable
{
    readonly AppSettings cfg;
    readonly Voice voice;
    TcpListener? listener;
    readonly CancellationTokenSource stop = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    volatile string lanesJson = "{\"lanes\":null,\"caption\":\"\"}";
    long lanesAt;
    public string? Problem { get; private set; }
    /// <summary>Set by the app: the cruise-follows-limit state as JSON, and the way to change it (/cruise, used by the tablet and web apps).</summary>
    public Func<string>? CruiseJson;
    public Func<string>? UpdateJson;   // is a newer Windows app available (for the tablet / browser version)
    public Action<bool>? CruiseSet;
    public int Port { get; private set; }

    public BuiltInServer(AppSettings cfg, Voice voice) { this.cfg = cfg; this.voice = voice; }

    /// <summary>The lane panel the app is showing right now (the lane overlay window reads it from /lanes).</summary>
    long localLanesAt;
    public void SetLanes(string json)
    {
        lanesJson = json; Interlocked.Exchange(ref lanesAt, Environment.TickCount64);
        bool has = !json.StartsWith("{\"lanes\":null", StringComparison.Ordinal);
        Interlocked.Exchange(ref localLanesAt, has ? Environment.TickCount64 : 0);
    }

    /// <summary>A panel pushed by another client (tablet, browser). Its "no lanes" never blanks lanes this app is showing right now.</summary>
    void SetLanesFromClient(string json)
    {
        using var d = JsonDocument.Parse(json);
        bool none = !d.RootElement.TryGetProperty("lanes", out var l) || l.ValueKind != JsonValueKind.Array;
        long mine = Interlocked.Read(ref localLanesAt);
        if (none && mine != 0 && Environment.TickCount64 - mine < 3000) return;
        lanesJson = json; Interlocked.Exchange(ref lanesAt, Environment.TickCount64); Interlocked.Exchange(ref localLanesAt, 0);
    }

    public bool Start()
    {
        Port = cfg.ServerPort;
        try
        {
            listener = new TcpListener(IPAddress.Any, Port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            listener.Start();
        }
        catch (SocketException e)
        {
            Problem = $"Port {Port} is in use (another ETS2-ATS GPS or the old companion is running), so the tablet / browser server is off: {e.Message}";
            return false;
        }
        _ = Task.Run(AcceptLoop);
        return true;
    }

    async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var c = await listener!.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(() => Handle(c));
            }
            catch { if (stop.IsCancellationRequested) return; }
        }
    }

    static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "application/javascript; charset=utf-8", ".css" => "text/css",
        ".json" => "application/json", ".webmanifest" => "application/manifest+json", ".png" => "image/png", ".ico" => "image/x-icon", _ => "application/octet-stream",
    };

    static async Task Reply(Stream s, int code, string ctype, byte[] body, bool noStore = true)
    {
        string status = code == 200 ? "OK" : code == 400 ? "Bad Request" : code == 404 ? "Not Found" : code == 502 ? "Bad Gateway" : "Error";
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {status}\r\nContent-Type: {ctype}\r\nContent-Length: {body.Length}\r\n" +
            (noStore ? "Cache-Control: no-store\r\n" : "") + "Connection: close\r\n\r\n");
        await s.WriteAsync(head);
        await s.WriteAsync(body);
    }

    async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = client.SendTimeout = 15000;
            var s = client.GetStream();
            // read the request head
            var buf = new byte[65536]; int len = 0, headEnd = -1;
            while (headEnd < 0 && len < buf.Length)
            {
                int n = await s.ReadAsync(buf.AsMemory(len));
                if (n <= 0) return;
                len += n;
                headEnd = IndexOf(buf, len, "\r\n\r\n");
            }
            if (headEnd < 0) return;
            string head = Encoding.ASCII.GetString(buf, 0, headEnd);
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            string method = parts[0], target = parts[1];
            int clen = 0;
            foreach (var l in lines.Skip(1)) if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l[15..].Trim(), out clen);
            clen = Math.Min(clen, 20000);
            var body = new byte[clen];
            int have = Math.Min(len - (headEnd + 4), clen);
            Array.Copy(buf, headEnd + 4, body, 0, have);
            while (have < clen) { int n = await s.ReadAsync(body.AsMemory(have, clen - have)); if (n <= 0) break; have += n; }
            string path = target.Split('?')[0];
            string query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";

            switch (path)
            {
                case "/info":
                    {
                        var m = MapService.ReadMeta();
                        string json = JsonSerializer.Serialize(new { name = "ets2-gps", game = Games.Current.Id, port = Port, fingerprint = m?.Fingerprint ?? "", built = m?.Built ?? 0, bytes = m?.Bytes ?? 0, mods = m?.Mods ?? false });
                        await Reply(s, 200, "application/json", Encoding.UTF8.GetBytes(json));
                        return;
                    }
                case "/map.bin":
                    {
                        if (!File.Exists(MapService.MapPath)) { await Reply(s, 404, "text/plain", Encoding.ASCII.GetBytes("map not built yet")); return; }
                        await using var f = new FileStream(MapService.MapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {f.Length}\r\nX-Map-Game: {Games.Current.Id}\r\nX-Map-Built: {MapService.ReadMeta()?.Built ?? 0}\r\nConnection: close\r\n\r\n"));
                        await f.CopyToAsync(s);
                        return;
                    }
                case "/assets.bin":
                    {
                        if (!File.Exists(MapService.AssetsPath)) { await Reply(s, 404, "text/plain", Encoding.ASCII.GetBytes("no logos / flags yet")); return; }
                        await Reply(s, 200, "application/octet-stream", await File.ReadAllBytesAsync(MapService.AssetsPath));
                        return;
                    }
                case "/update":
                    await Reply(s, 200, "application/json", Encoding.UTF8.GetBytes(UpdateJson?.Invoke() ?? "{\"available\":false}"));
                    return;
                case "/drops.json":
                    {
                        if (!File.Exists(MapService.DropsPath)) { await Reply(s, 404, "text/plain", Encoding.ASCII.GetBytes("no drop-off points yet")); return; }
                        await Reply(s, 200, "application/json", await File.ReadAllBytesAsync(MapService.DropsPath));
                        return;
                    }
                case "/gamegps":
                    await Reply(s, 200, "application/json", Encoding.UTF8.GetBytes(await GameGpsJson()));
                    return;
                case "/cruise":
                    {
                        string on = ParseQuery(query, "on");
                        if (on == "1" || on == "0") CruiseSet?.Invoke(on == "1");
                        await Reply(s, 200, "application/json", Encoding.UTF8.GetBytes(CruiseJson?.Invoke() ?? "{\"on\":false}"));
                        return;
                    }
                case "/say":
                    {
                        string text = method == "POST" ? Encoding.UTF8.GetString(body) : ParseQuery(query, "text");
                        if (!string.IsNullOrWhiteSpace(text)) voice.SayAlways(text.Trim()[..Math.Min(300, text.Trim().Length)]);
                        await Reply(s, 200, "text/plain", Encoding.ASCII.GetBytes("ok"));
                        return;
                    }
                case "/lanes":
                    {
                        if (method == "POST")
                        {
                            try { JsonDocument.Parse(body).Dispose(); SetLanesFromClient(Encoding.UTF8.GetString(body)); await Reply(s, 200, "text/plain", Encoding.ASCII.GetBytes("ok")); }
                            catch (JsonException) { await Reply(s, 400, "text/plain", Encoding.ASCII.GetBytes("bad json")); }
                            return;
                        }
                        // the stored panel with its age in seconds (the reader's clock does not matter)
                        using var doc = JsonDocument.Parse(lanesJson);
                        var sb = new StringBuilder("{");
                        bool first = true;
                        foreach (var p in doc.RootElement.EnumerateObject()) { if (!first) sb.Append(','); first = false; sb.Append(JsonSerializer.Serialize(p.Name)).Append(':').Append(p.Value.GetRawText()); }
                        long at = Interlocked.Read(ref lanesAt);
                        sb.Append(",\"age\":").Append(at == 0 ? "null" : ((Environment.TickCount64 - at) / 1000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
                        await Reply(s, 200, "application/json", Encoding.UTF8.GetBytes(sb.ToString()));
                        return;
                    }
                case "/api/telemetry":
                    {
                        try
                        {
                            string host = cfg.TelemetryHost.Contains(':') ? cfg.TelemetryHost : cfg.TelemetryHost + ":31377";
                            var data = await Http.GetByteArrayAsync($"http://{host}/api/ets2/telemetry");
                            await Reply(s, 200, "application/json", data);
                        }
                        catch (Exception e) { await Reply(s, 502, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = e.Message }))); }
                        return;
                    }
            }
            // the web app: files embedded in the exe
            string name = path == "/" ? "index.html" : path.TrimStart('/');
            if (name.Contains('/') || name.Contains('\\') || name.StartsWith('.')) { await Reply(s, 404, "text/plain", Encoding.ASCII.GetBytes("not found")); return; }
            using var res = typeof(BuiltInServer).Assembly.GetManifestResourceStream("web/" + name);
            if (res == null) { await Reply(s, 404, "text/plain", Encoding.ASCII.GetBytes("ETS2-ATS GPS. Endpoints: /info, /map.bin, /api/telemetry, /lanes, / (web app)")); return; }
            using var ms = new MemoryStream();
            res.CopyTo(ms);
            await Reply(s, 200, Mime(name), ms.ToArray());
        }
        catch { /* client went away */ }
    }

    static readonly SemaphoreSlim gameGpsLock = new(1, 1);
    static GameGpsState? gameGpsCache;

    /// <summary>
    /// The game's GPS destination from the newest save, for the tablet / browser version (which cannot read the PC's saves):
    /// {"available", "savedMs", "ageSec", "onJob", "ahead": [[x, z], ...]}; the last point of "ahead" is the destination. A save is decoded once.
    /// </summary>
    static async Task<string> GameGpsJson()
    {
        await gameGpsLock.WaitAsync();
        try
        {
            var newest = GameGps.NewestSave();
            if (newest is not { } n) return "{\"available\":false}";
            if ((gameGpsCache == null || gameGpsCache.SavedUtc != n.Utc) && DateTime.UtcNow - n.Utc > TimeSpan.FromSeconds(1.5))   // let the game finish writing
                gameGpsCache = await Task.Run(() => GameGps.Read(n.File, n.Utc));
            var g = gameGpsCache;
            if (g == null) return "{\"available\":false}";
            return JsonSerializer.Serialize(new
            {
                available = true, savedMs = new DateTimeOffset(g.SavedUtc).ToUnixTimeMilliseconds(), ageSec = (int)(DateTime.UtcNow - g.SavedUtc).TotalSeconds,
                onJob = g.OnJob, ahead = g.Ahead.Select(p => new[] { MathF.Round(p.X, 1), MathF.Round(p.Z, 1) }),
            });
        }
        catch (Exception e) { return JsonSerializer.Serialize(new { available = false, error = e.Message }); }
        finally { gameGpsLock.Release(); }
    }

    static string ParseQuery(string q, string key)
    {
        foreach (var kv in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = kv.IndexOf('=');
            if (i > 0 && kv[..i] == key) return Uri.UnescapeDataString(kv[(i + 1)..].Replace('+', ' '));
        }
        return "";
    }

    static int IndexOf(byte[] b, int len, string marker)
    {
        var m = Encoding.ASCII.GetBytes(marker);
        for (int i = 0; i <= len - m.Length; i++)
        {
            int k = 0;
            while (k < m.Length && b[i + k] == m[k]) k++;
            if (k == m.Length) return i;
        }
        return -1;
    }

    public void Dispose() { stop.Cancel(); try { listener?.Stop(); } catch { /* already closed */ } }
}
