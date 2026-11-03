using System.Globalization;
using System.Text;

namespace Ets2Gps;

/// <summary>What the newest save says about the game's own GPS: the points it routes through (the last one is its destination) and whether a job is on.</summary>
public sealed record GameGpsState(DateTime SavedUtc, string SaveName, List<(float X, float Z)> Ahead, bool OnJob, bool JobSelected);

/// <summary>
/// Reads the game's GPS destination from the newest save of the profile in use (the game writes it on every autosave and quick save).
/// The save is decoded in memory with SIIDecryptSharp; save files are only read, never changed.
/// </summary>
public static class GameGps
{
    const double NavNodeScale = 256.0;   // gps_waypoint_storage positions are in 1/256 m

    /// <summary>game.sii of the most recently written save of the profile, with its time, or null.</summary>
    public static (string File, DateTime Utc)? NewestSave()
    {
        var profile = GameControls.ProfileDir();
        if (profile == null) return null;
        var saves = Path.Combine(profile, "save");
        if (!Directory.Exists(saves)) saves = CloudSaves(Path.GetFileName(profile)) ?? saves;
        if (!Directory.Exists(saves)) return null;
        (string, DateTime)? best = null;
        foreach (var dir in Directory.EnumerateDirectories(saves))
        {
            var f = Path.Combine(dir, "game.sii");
            if (!File.Exists(f)) continue;
            var t = File.GetLastWriteTimeUtc(f);
            if (best == null || t > best.Value.Item2) best = (f, t);
        }
        return best;
    }

    /// <summary>
    /// A Steam Cloud profile (steam_profiles) keeps only its settings in Documents: Steam stores its saves under
    /// Steam\userdata\&lt;account&gt;\&lt;app id&gt;\remote\profiles\&lt;profile&gt;\save. The most recently written one of those, or null.
    /// </summary>
    static string? CloudSaves(string profileHex)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is not string root) return null;
            var userdata = Path.Combine(root, "userdata");
            if (!Directory.Exists(userdata)) return null;
            return Directory.EnumerateDirectories(userdata)
                .Select(u => Path.Combine(u, Games.Current.SteamApp, "remote", "profiles", profileHex, "save"))
                .Where(Directory.Exists)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Decodes a save and picks out the GPS waypoints ahead and the job state. Throws when the save cannot be read.</summary>
    public static GameGpsState Read(string file, DateTime savedUtc)
    {
        var text = Encoding.UTF8.GetString(SIIDecryptSharp.Decryptor.Decrypt(file));
        string? unitId = null, unitType = null;
        var aheadIds = new List<string>();
        var positions = new Dictionary<string, string>();
        bool economyDone = false, playerDone = false, onJob = false, selected = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line == "}") { if (unitType == "economy") economyDone = true; if (unitType == "player") playerDone = true; unitType = null; continue; }
            if (unitType == null)
            {
                int sep = line.IndexOf(" : ", StringComparison.Ordinal);
                if (sep > 0 && line.EndsWith(" {", StringComparison.Ordinal)) { unitType = line[..sep]; unitId = line[(sep + 3)..^2]; }
                continue;
            }
            if (line[0] != ' ') continue;
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 1) continue;
            string key = line[1..colon], value = line[(colon + 2)..].Trim();
            if (unitType == "economy" && !economyDone && key.StartsWith("stored_gps_ahead_waypoints[", StringComparison.Ordinal)) aheadIds.Add(value);
            else if (unitType == "player" && !playerDone)
            {
                if ((key == "current_job" || key == "current_car_job") && value != "null") onJob = true;
                if (key == "selected_job" && value != "null") selected = true;
            }
            else if (unitType == "gps_waypoint_storage" && key == "nav_node_position" && unitId != null) positions[unitId] = value;
        }
        var ahead = new List<(float X, float Z)>();
        foreach (var id in aheadIds)
            if (positions.TryGetValue(id, out var pos) && ParseTriple(pos) is { } p)
                ahead.Add(((float)(p[0] / NavNodeScale), (float)(p[2] / NavNodeScale)));
        return new GameGpsState(savedUtc, Path.GetFileName(Path.GetDirectoryName(file)) ?? "", ahead, onJob, selected);
    }

    static double[]? ParseTriple(string value)
    {
        if (!value.StartsWith('(') || !value.EndsWith(')')) return null;
        var parts = value[1..^1].Split(',');
        if (parts.Length != 3) return null;
        var r = new double[3];
        for (int i = 0; i < 3; i++)
        {
            var t = parts[i].Trim();
            if (t.StartsWith('&'))   // the decoder writes some floats as "&" + their IEEE-754 bits in hex
            {
                if (!uint.TryParse(t[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits)) return null;
                r[i] = BitConverter.Int32BitsToSingle((int)bits);
            }
            else if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out r[i])) return null;
        }
        return r;
    }
}
