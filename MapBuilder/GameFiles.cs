using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ets2Gps.MapBuilder;

/// <summary>One mod of the active profile as reported by the TruckSim GPS server.</summary>
public sealed record ModPackage(string Package, string WorkshopId, string DisplayName);

/// <summary>Locates the install of the current game (ETS2 or ATS) and its active mods and mounts them in game load order.</summary>
public static class GameFiles
{
    static string AppId => Games.Current.SteamApp;
    static readonly string[] Skip =
    {
        "base_vehicle", "dlc_flags_", "dlc_lunar", "dlc_christmas", "dlc_halloween", "dlc_valentine", "dlc_pink_ribbon", "dlc_prehistoric",
        "dlc_fantasy", "dlc_space", "dlc_toys", "dlc_pirates", "dlc_rocket", "dlc_streetart", "dlc_super_stripes", "dlc_lines",
    };

    public static string ServerUrl = "http://127.0.0.1:31377";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static string? DocumentsDir()
    {
        var cands = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents"),
        };
        foreach (var c in cands)
            if (!string.IsNullOrEmpty(c) && Directory.Exists(Path.Combine(c, Games.Current.Folder))) return Path.Combine(c, Games.Current.Folder);
        return null;
    }

    public static List<string> SteamLibraries()
    {
        var libs = new List<string>();
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string root)
            {
                libs.Add(root);
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
        }
        catch { /* no Steam registry entry */ }
        foreach (char d in "CDEFGH") { libs.Add(d + @":\SteamLibrary"); libs.Add(d + @":\Steam"); }
        var outList = new List<string>();
        foreach (var l in libs)
        {
            string n;
            try { n = Path.GetFullPath(l); } catch { continue; }
            if (!outList.Contains(n, StringComparer.OrdinalIgnoreCase) && Directory.Exists(n)) outList.Add(n);
        }
        return outList;
    }

    public static string FindGameDir(string? explicitDir = null)
    {
        if (!string.IsNullOrEmpty(explicitDir)) return explicitDir;
        foreach (var lib in SteamLibraries())
        {
            string g = Path.Combine(lib, "steamapps", "common", Games.Current.Folder);
            if (Directory.Exists(g)) return g;
        }
        throw new InvalidOperationException($"Could not find {Games.Current.Name} (looked in the Steam libraries).");
    }

    static JsonElement HttpJson(string path) =>
        JsonDocument.Parse(Http.GetStringAsync(ServerUrl + path).GetAwaiter().GetResult()).RootElement.Clone();

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "") : "";

    /// <summary>Mods from the TruckSim GPS server, lowest priority first (= mount order), or null when the server is not reachable.</summary>
    public static List<ModPackage>? ActiveModPackages(Action<string> log)
    {
        try
        {
            var st = HttpJson("/api/game/state?game=" + Games.Current.Id);
            JsonElement prof = default; bool have = false;
            if (st.TryGetProperty("activeProfile", out var ap) && ap.ValueKind == JsonValueKind.Object) { prof = ap; have = true; log("  active profile: " + Str(ap, "name")); }
            if (!have)
            {
                var profs = HttpJson("/api/game/profiles?game=" + Games.Current.Id);
                if (!profs.TryGetProperty("profiles", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;
                prof = arr[0];
                log("  game not running; using first profile " + Str(prof, "name"));
            }
            var mods = HttpJson($"/api/game/profile-mods?game={Games.Current.Id}&id={Uri.EscapeDataString(Str(prof, "id"))}&type={Uri.EscapeDataString(Str(prof, "type"))}");
            var list = new List<ModPackage>();
            if (mods.TryGetProperty("mods", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray()) list.Add(new ModPackage(Str(m, "package"), Str(m, "workshopId"), Str(m, "displayName")));
            list.Reverse();
            return list;
        }
        catch (Exception e) { log($"  TruckSim GPS server not reachable ({e.Message})"); return null; }
    }

    static string GameVersion(FileSystem fs)
    {
        try
        {
            var t = HttpJson("/api/ets2/telemetry");
            if (t.TryGetProperty("game", out var g)) { var v = Str(g, "gameProductVersion"); if (v.Length > 0) return v; }
        }
        catch { /* server not reachable */ }
        var d = fs.Read("version.txt");
        return d != null ? System.Text.Encoding.UTF8.GetString(d).Trim() : "";
    }

    /// <summary>Paths (mount order) for one profile mod entry.</summary>
    static List<string> ResolvePackage(ModPackage pkg, string? docs, List<string> libs, string gameVersion)
    {
        if (pkg.WorkshopId.Length > 0)
        {
            foreach (var lib in libs)
            {
                string d = Path.Combine(lib, "steamapps", "workshop", "content", AppId, pkg.WorkshopId);
                if (Directory.Exists(d)) return WorkshopPaths(d, gameVersion);
            }
            return new();
        }
        if (docs != null)
        {
            foreach (var ext in new[] { ".scs", ".zip" })
            {
                string p = Path.Combine(docs, "mod", pkg.Package + ext);
                if (File.Exists(p)) return new() { p };
            }
            string dir = Path.Combine(docs, "mod", pkg.Package);
            if (Directory.Exists(dir)) return new() { dir };
        }
        return new();
    }

    static string? FindNamed(string d, string baseName)
    {
        foreach (var ext in new[] { ".scs", ".zip", "" })
        {
            string p = Path.Combine(d, baseName + ext);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        return null;
    }

    static int[] VKey(string v) => Regex.Matches(v, @"\d+").Select(m => int.Parse(m.Value)).Take(3).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
    static int CmpKey(int[] a, int[] b) { for (int i = 0; i < 3; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]); return 0; }

    /// <summary>Shell-style pattern match (* and ?), like Python's fnmatch.</summary>
    static bool FnMatch(string text, string pattern) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    static List<string> WorkshopPaths(string d, string gameVersion)
    {
        string vs = Path.Combine(d, "versions.sii");
        if (!File.Exists(vs))
            return Directory.GetFileSystemEntries(d).Where(f => f.EndsWith(".scs") || f.EndsWith(".zip")).OrderBy(f => f, StringComparer.Ordinal).ToList();
        string txt = File.ReadAllText(vs);
        var universal = new List<string>();
        var versioned = new List<(int[] Key, string Path)>();
        foreach (Match bm in Regex.Matches(txt, @"package_version_info\s*:\s*\S+\s*\{(.*?)\}", RegexOptions.Singleline))
        {
            string b = bm.Groups[1].Value;
            var nm = Regex.Match(b, "package_name:\\s*\"([^\"]+)\"");
            if (!nm.Success) continue;
            var compat = Regex.Matches(b, "compatible_versions\\[\\]:\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            var p = FindNamed(d, nm.Groups[1].Value);
            if (p == null) continue;
            if (compat.Count == 0) universal.Add(p);
            else if (gameVersion.Length > 0)
            {
                if (compat.Any(c => FnMatch(gameVersion, c) || FnMatch(gameVersion + ".", c))) versioned.Add((new[] { 0, 0, 0 }, p));
            }
            else
                versioned.Add((compat.Select(c => VKey(c.Replace("*", "0"))).Aggregate((x, y) => CmpKey(x, y) >= 0 ? x : y), p)); // unknown game version: newest
        }
        if (versioned.Count > 0 && gameVersion.Length == 0)
        {
            var best = versioned[0];
            foreach (var v in versioned) if (CmpKey(v.Key, best.Key) > 0 || (CmpKey(v.Key, best.Key) == 0 && string.CompareOrdinal(v.Path, best.Path) > 0)) best = v;
            versioned = new() { best };
        }
        return universal.Concat(versioned.Select(v => v.Path)).ToList();
    }

    /// <summary>Mounts the base game and DLC archives, then the active mods; returns the game version.</summary>
    public static (FileSystem Fs, string Version) MountEverything(string gameDir, Action<string> log, bool useMods = true, List<ModPackage>? knownMods = null)
    {
        var fs = new FileSystem { Log = log };
        int n = 0;
        foreach (var f in Directory.GetFiles(gameDir, "*.scs").OrderBy(x => x, StringComparer.Ordinal))
        {
            string b = Path.GetFileName(f);
            if (Skip.Any(s => b.StartsWith(s, StringComparison.Ordinal))) continue;
            if (fs.Mount(f)) n++;
        }
        log($"mounted {n} game archives");
        string version = GameVersion(fs);
        log("game version: " + version);
        var mods = new List<string>();
        if (useMods)
        {
            var pk = knownMods ?? ActiveModPackages(log);
            string? docs = DocumentsDir();
            var libs = SteamLibraries();
            if (pk == null)
            {
                log("  falling back to every .scs/.zip/folder in Documents/mod (alphabetical)");
                string? mdir = docs != null ? Path.Combine(docs, "mod") : null;
                if (mdir != null && Directory.Exists(mdir)) mods.AddRange(Directory.GetFileSystemEntries(mdir).OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal));
            }
            else
                foreach (var p in pk)
                {
                    var paths = ResolvePackage(p, docs, libs, version);
                    if (paths.Count == 0) log("  ! mod not found on disk: " + (p.DisplayName.Length > 0 ? p.DisplayName : p.Package));
                    mods.AddRange(paths);
                }
        }
        foreach (var m in mods)
            if (fs.Mount(m)) log("  + mod " + Path.GetFileName(m));
        return (fs, version);
    }
}
