using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ets2Gps.MapBuilder;

/// <summary>Everything remembered about the cached map (etsmap.meta.json next to etsmap.bin).</summary>
public sealed class MapMeta
{
    public string Fingerprint { get; set; } = "";
    public long Built { get; set; }
    public long Bytes { get; set; }
    public bool Mods { get; set; }
}

/// <summary>
/// Builds the road map from the installed game and active mods (in this process, no Python needed), keeps it in
/// %APPDATA%\ETS2-GPS and rebuilds it when the game, its DLC or the mod list changes.
/// </summary>
public static class MapService
{
    // one set of files per game: etsmap.bin ... for ETS2, atsmap.bin ... for ATS
    static string P => Games.Current.FilePrefix;
    public static string MapPath => Path.Combine(AppSettings.Dir, P + "map.bin");
    public static string MetaPath => Path.Combine(AppSettings.Dir, P + "map.meta.json");
    public static string AssetsPath => Path.Combine(AppSettings.Dir, P + "assets.bin");
    public static string DropsPath => Path.Combine(AppSettings.Dir, P + "map.drops.json");
    public static string LogPath => Path.Combine(AppSettings.Dir, Games.Current == Games.Ets2 ? "map_build.log" : P + "map_build.log");

    public static MapMeta? ReadMeta()
    {
        try { return JsonSerializer.Deserialize<MapMeta>(File.ReadAllText(MetaPath)); }
        catch { return null; }
    }

    /// <summary>Hash of everything that changes the map: the builder version, the game archives and the mod list.</summary>
    static string Fingerprint(string gameDir, List<ModPackage>? mods)
    {
        var sb = new StringBuilder("v" + BuildMap.FormatVersion);
        foreach (var f in Directory.GetFiles(gameDir, "*.scs").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (f == null) continue;
            if (!(f.StartsWith("base_map") || f.StartsWith("def") || f.StartsWith("dlc_") || f == "base.scs")) continue;
            var fi = new FileInfo(Path.Combine(gameDir, f));
            long mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            sb.Append(f).Append(':').Append(fi.Length).Append(':').Append(mtime);
        }
        if (mods != null) foreach (var m in mods) sb.Append(m.Package).Append('|').Append(m.WorkshopId);
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Makes sure an up-to-date map is in the cache and returns it. A saved map is kept when the TruckSim server (which knows
    /// the active mod list) is not reachable, so a closed game never triggers a pointless rebuild.
    /// </summary>
    public static async Task<MapData> LoadAsync(AppSettings cfg, IProgress<string> progress)
    {
        Directory.CreateDirectory(AppSettings.Dir);
        GameFiles.ServerUrl = "http://" + (cfg.TelemetryHost.Contains(':') ? cfg.TelemetryHost : cfg.TelemetryHost + ":31377");
        bool have = File.Exists(MapPath);
        await Task.Run(() =>
        {
            string? gameDir = null;
            try { gameDir = GameFiles.FindGameDir(); }
            catch (Exception e) { if (!have) throw new InvalidOperationException(e.Message); }
            if (gameDir == null) return;
            progress.Report("Checking the game and mods…");
            using var logFile = new StreamWriter(LogPath, false) { AutoFlush = true };
            void Log(string s) => logFile.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + s);
            var mods = GameFiles.ActiveModPackages(Log);
            var meta = ReadMeta();
            string fp = Fingerprint(gameDir, mods);
            bool rebuild = !have || meta == null || (mods != null && meta.Fingerprint != fp);
            if (!rebuild) return;
            Log("building the map from " + gameDir);
            progress.Report("Building the map from your game (first time, or after a game or mod update)…");
            var (fs, _) = GameFiles.MountEverything(gameDir, Log, true, mods);
            string tmp = MapPath + ".part";
            int lastPct = -1;
            BuildMap.Build(fs, tmp, Log, (text, frac) =>
            {
                int pct = (int)(frac * 100);
                if (pct != lastPct) { lastPct = pct; progress.Report($"{text} {pct}%"); }
            }, AssetsPath, DropsPath);
            File.Move(tmp, MapPath, true);
            File.WriteAllText(MetaPath, JsonSerializer.Serialize(new MapMeta
            {
                Fingerprint = fp, Built = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Bytes = new FileInfo(MapPath).Length, Mods = mods != null,
            }));
        });
        progress.Report("Loading the map…");
        return await Task.Run(() =>
        {
            var map = MapData.Load(MapPath);
            try { map.Drops = CompanyDrops.Index(System.Text.Json.JsonSerializer.Deserialize<List<CompanyDrops>>(File.ReadAllText(DropsPath)) ?? new()); }
            catch { /* no drop-off points yet (map built by an older version): job routes end at the company as before */ }
            return map;
        });
    }
}
