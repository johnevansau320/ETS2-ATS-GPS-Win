using System.Text.RegularExpressions;

namespace Ets2Gps;

/// <summary>A keyboard key as the game reads it: a scan code, plus whether it is an "extended" key (arrows, Home, the keypad's / and Enter ...).</summary>
public readonly record struct GameKey(ushort Scan, bool Extended, string Name);

/// <summary>
/// The player's own key bindings for "cruise control faster / slower", read from the game's controls.sii of the profile in use
/// (Documents\Euro Truck Simulator 2\profiles or steam_profiles). The files are plain text and are only read, never changed.
/// </summary>
public static class GameControls
{
    public sealed record Result(GameKey? Faster, GameKey? Slower, string Source, string? Problem)
    {
        public string Describe() => Faster != null && Slower != null ? $"{Faster.Value.Name} / {Slower.Value.Name}" : "none";
    }

    /// <summary>The default bindings of the game (Numpad + and Numpad -), used when the controls file cannot be found.</summary>
    static readonly Result Default = new(new GameKey(0x4E, false, "Numpad +"), new GameKey(0x4A, false, "Numpad -"), "default keys (the game's controls file was not found)", null);

    // names the game uses in controls.sii -> (scan code, extended, label)
    static readonly Dictionary<string, (ushort, bool, string)> Keys = Build();

    static Dictionary<string, (ushort, bool, string)> Build()
    {
        var d = new Dictionary<string, (ushort, bool, string)>(StringComparer.OrdinalIgnoreCase);
        void A(string n, int sc, string label, bool ext = false) => d[n] = ((ushort)sc, ext, label);
        const string letters = "qwertyuiop";
        for (int i = 0; i < letters.Length; i++) A(letters[i].ToString(), 0x10 + i, letters[i].ToString().ToUpperInvariant());
        const string row2 = "asdfghjkl";
        for (int i = 0; i < row2.Length; i++) A(row2[i].ToString(), 0x1E + i, row2[i].ToString().ToUpperInvariant());
        const string row3 = "zxcvbnm";
        for (int i = 0; i < row3.Length; i++) A(row3[i].ToString(), 0x2C + i, row3[i].ToString().ToUpperInvariant());
        for (int i = 1; i <= 9; i++) A("key" + i, 0x01 + i, i.ToString());
        A("key0", 0x0B, "0");
        for (int i = 1; i <= 10; i++) A("f" + i, 0x3A + i, "F" + i);
        A("f11", 0x57, "F11"); A("f12", 0x58, "F12");
        A("minus", 0x0C, "-"); A("equal", 0x0D, "="); A("backspace", 0x0E, "Backspace"); A("tab", 0x0F, "Tab");
        A("lbracket", 0x1A, "["); A("rbracket", 0x1B, "]"); A("enter", 0x1C, "Enter"); A("semicolon", 0x27, ";"); A("apostrophe", 0x28, "'");
        A("grave", 0x29, "`"); A("backslash", 0x2B, "\\"); A("comma", 0x33, ","); A("period", 0x34, "."); A("slash", 0x35, "/"); A("space", 0x39, "Space");
        A("num7", 0x47, "Numpad 7"); A("num8", 0x48, "Numpad 8"); A("num9", 0x49, "Numpad 9"); A("numminus", 0x4A, "Numpad -");
        A("num4", 0x4B, "Numpad 4"); A("num5", 0x4C, "Numpad 5"); A("num6", 0x4D, "Numpad 6"); A("numplus", 0x4E, "Numpad +");
        A("num1", 0x4F, "Numpad 1"); A("num2", 0x50, "Numpad 2"); A("num3", 0x51, "Numpad 3"); A("num0", 0x52, "Numpad 0"); A("numperiod", 0x53, "Numpad .");
        A("nummultiply", 0x37, "Numpad *"); A("numslash", 0x35, "Numpad /", true); A("numenter", 0x1C, "Numpad Enter", true);
        A("uarrow", 0x48, "Up arrow", true); A("darrow", 0x50, "Down arrow", true); A("larrow", 0x4B, "Left arrow", true); A("rarrow", 0x4D, "Right arrow", true);
        A("home", 0x47, "Home", true); A("end", 0x4F, "End", true); A("pgup", 0x49, "Page Up", true); A("pgdn", 0x51, "Page Down", true);
        A("ins", 0x52, "Insert", true); A("del", 0x53, "Delete", true);
        A("scrollock", 0x46, "Scroll Lock"); A("pause", 0x45, "Pause", true);
        return d;
    }

    static readonly Regex Line = new(@"^\s*config_lines\[\d+\]:\s*""mix\s+(cruiectrlinc|cruiectrldec)\s+`(?<b>[^`]*)`""", RegexOptions.Compiled);

    /// <summary>Reads the bindings of the profile the game last used. Never throws.</summary>
    public static Result Read()
    {
        try
        {
            var file = FindControlsFile();
            if (file == null) return Default;
            string? faster = null, slower = null;
            foreach (var line in File.ReadLines(file))
            {
                var m = Line.Match(line);
                if (!m.Success) continue;
                if (m.Groups[1].Value == "cruiectrlinc") faster = m.Groups["b"].Value; else slower = m.Groups["b"].Value;
            }
            if (faster == null && slower == null) return Default;   // an older / different layout: keep the game's defaults
            var f = FirstKey(faster); var s = FirstKey(slower);
            string src = "profile " + ProfileName(file);
            if (f == null || s == null)
                return new Result(f, s, src, $"{Games.Current.Name} has no keyboard key assigned to \"cruise control faster / slower\" in this profile (joystick buttons cannot be pressed from here). " +
                    "Assign keys in the game's Options > Keyboard (for example Numpad + and Numpad -) to use the automatic speed limit.");
            return new Result(f, s, src, null);
        }
        catch { return Default; }
    }

    static readonly Regex QuickSaveLine = new(@"^\s*config_lines\[\d+\]:\s*""mix\s+quicksave\s+`(?<b>[^`]*)`""", RegexOptions.Compiled);

    /// <summary>The player's key for "quick save" (the game's default is F5); null when it is bound to no plain keyboard key.</summary>
    public static GameKey? QuickSaveKey()
    {
        try
        {
            var file = FindControlsFile();
            if (file == null) return new GameKey(0x3F, false, "F5");
            foreach (var line in File.ReadLines(file))
            {
                var m = QuickSaveLine.Match(line);
                if (m.Success) return FirstKey(m.Groups["b"].Value);
            }
            return new GameKey(0x3F, false, "F5");
        }
        catch { return null; }
    }

    /// <summary>The first plain keyboard key in a binding such as "joy.b9?0 | keyboard.numplus?0 | semantical.cruiectrlinc?0" (keys that need a modifier are skipped).</summary>
    static GameKey? FirstKey(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding)) return null;
        foreach (var part in binding.Split('|'))
        {
            var p = part.Trim();
            int q = p.IndexOf('?'); if (q >= 0) p = p[..q];
            if (!p.StartsWith("keyboard.", StringComparison.OrdinalIgnoreCase)) continue;
            if (Keys.TryGetValue(p["keyboard.".Length..], out var k)) return new GameKey(k.Item1, k.Item2, k.Item3);
        }
        return null;
    }

    static string ProfileName(string file)
    {
        var hex = Path.GetFileName(Path.GetDirectoryName(file)!);
        try
        {
            var bytes = Convert.FromHexString(hex);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch { return hex; }
    }

    // ------------------------------------------------------------------------ finding the file --

    /// <summary>The folder of the profile the game used last (where its saves are), or null.</summary>
    public static string? ProfileDir() { try { var f = FindControlsFile(); return f == null ? null : Path.GetDirectoryName(f); } catch { return null; } }

    static readonly Regex ProfilePath = new(@"/home/(profiles|steam_profiles)/([0-9A-Fa-f]+)/", RegexOptions.Compiled);

    /// <summary>The controls.sii of the profile the game used last (named in game.log.txt), else the most recently changed one.</summary>
    static string? FindControlsFile()
    {
        var over = Environment.GetEnvironmentVariable("ETS2_CONTROLS_FILE");   // test only
        if (!string.IsNullOrEmpty(over)) return File.Exists(over) ? over : null;
        var roots = new List<string>();
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var r in new[] { Path.Combine(docs, Games.Current.Folder),
                                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", Games.Current.Folder) })
            if (Directory.Exists(r) && !roots.Contains(r, StringComparer.OrdinalIgnoreCase)) roots.Add(r);
        string? best = null; DateTime bestTime = DateTime.MinValue;
        foreach (var root in roots)
        {
            // the log of the last game start says which profile was loaded
            try
            {
                var log = Path.Combine(root, "game.log.txt");
                if (File.Exists(log))
                {
                    Match? last = null;
                    foreach (var line in File.ReadLines(log)) { var m = ProfilePath.Match(line); if (m.Success) last = m; }
                    if (last != null)
                    {
                        var f = Path.Combine(root, last.Groups[1].Value, last.Groups[2].Value, "controls.sii");
                        if (File.Exists(f)) return f;
                    }
                }
            }
            catch { /* fall through */ }
            foreach (var sub in new[] { "profiles", "steam_profiles" })
            {
                var dir = Path.Combine(root, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (var p in Directory.EnumerateDirectories(dir))
                {
                    var f = Path.Combine(p, "controls.sii");
                    if (!File.Exists(f)) continue;
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > bestTime) { bestTime = t; best = f; }
                }
            }
        }
        return best;
    }
}
