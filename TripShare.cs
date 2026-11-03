using System.Text;
using Microsoft.Win32;

namespace Ets2Gps;

/// <summary>Export and import of saved trips (trip files, *.ets2trip), so trips can be shared with other people and apps.</summary>
public sealed partial class MainWindow
{
    void TripFileMenu()
    {
        var items = new List<string> { "Import trips from a file…" };
        if (cfg.GameTrips.Count > 0) items.Add("Export all trips…");
        Dlg.Pick(this, "Share trips", items, i =>
        {
            if (i == 0) ImportTripsDialog();
            else ExportTripsDialog(cfg.GameTrips.ToList(), "ETS2 trips");
        }, "Trip files (*.ets2trip) can be sent to friends or posted online; the Android app, the web version and this app all read them.");
    }

    static string SafeFileName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace('→', '-').Trim();
        return s.Length == 0 ? "trip" : s;
    }

    void ExportTripsDialog(List<SavedTrip> trips, string defaultName)
    {
        if (trips.Count == 0) { Toast("No saved trips to export"); return; }
        var dlg = new SaveFileDialog
        {
            Title = "Export trips", FileName = SafeFileName(defaultName), DefaultExt = ".ets2trip", AddExtension = true,
            Filter = "ETS2-ATS GPS trip (*.ets2trip)|*.ets2trip|JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, TripFile.Serialize(trips), new UTF8Encoding(false));
            Toast(trips.Count == 1 ? "Trip exported: " + Path.GetFileName(dlg.FileName) : $"{trips.Count} trips exported: " + Path.GetFileName(dlg.FileName));
        }
        catch (Exception e) { Toast("Could not save the file: " + e.Message); }
    }

    void ImportTripsDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import trips", CheckFileExists = true,
            Filter = "ETS2-ATS GPS trips (*.ets2trip;*.json)|*.ets2trip;*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        Toast(ImportTripsFromFile(dlg.FileName));
    }

    /// <summary>Adds the trips in a trip file to the saved trips; returns a short message about what happened.</summary>
    string ImportTripsFromFile(string path)
    {
        string text;
        try
        {
            if (new FileInfo(path).Length > TripFile.MaxChars * 4L) return "That file is too big to be a trip file";
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) { return "Could not read the file: " + e.Message; }
        var res = TripFile.Parse(text);
        if (res.Error != null) return res.Error;
        int added = 0, dupes = 0, outside = 0;
        var m = map;
        foreach (var t in res.Trips)
        {
            // positions outside the loaded map: made for a different map, they could not be routed
            if (m != null && new[] { t.Dest }.Concat(t.Stops).Concat(t.Start != null ? new[] { t.Start } : Array.Empty<StopPoint>())
                    .Any(p => p.X < m.MinX - 20000 || p.X > m.MaxX + 20000 || p.Z < m.MinZ - 20000 || p.Z > m.MaxZ + 20000)) { outside++; continue; }
            if (cfg.GameTrips.Any(x => SameTrip(x, t))) { dupes++; continue; }
            string name = t.Name; int n = 2;
            while (cfg.GameTrips.Any(x => x.Name == name)) name = $"{t.Name} ({n++})";
            t.Name = name;
            cfg.GameTrips.Insert(0, t);
            added++;
        }
        if (added > 0) cfg.Save();
        var parts = new List<string> { added == 1 ? "1 trip imported" : $"{added} trips imported" };
        if (dupes > 0) parts.Add($"{dupes} already saved");
        int bad = res.Skipped + outside;
        if (bad > 0) parts.Add($"{bad} skipped (" + (outside > 0 ? "not on this map or " : "") + "not valid)");
        return string.Join(", ", parts);
    }

    static bool SameTrip(SavedTrip a, SavedTrip b) =>
        Math.Abs(a.Dest.X - b.Dest.X) < 1 && Math.Abs(a.Dest.Z - b.Dest.Z) < 1 && a.Stops.Count == b.Stops.Count &&
        a.Stops.Zip(b.Stops).All(p => Math.Abs(p.First.X - p.Second.X) < 1 && Math.Abs(p.First.Z - p.Second.Z) < 1) &&
        (a.Start == null) == (b.Start == null) && (a.Start == null || (Math.Abs(a.Start.X - b.Start!.X) < 1 && Math.Abs(a.Start.Z - b.Start.Z) < 1));

    /// <summary>Test hooks: --trips-export file / --trips-import file run the export / import without a dialog and write the outcome to file.result.txt.</summary>
    void AutoTripFiles(string? exportPath, string? importPath)
    {
        try
        {
            if (exportPath != null) { File.WriteAllText(exportPath, TripFile.Serialize(cfg.GameTrips), new UTF8Encoding(false)); File.WriteAllText(exportPath + ".result.txt", $"exported {cfg.GameTrips.Count}"); }
            if (importPath != null) File.WriteAllText(importPath + ".result.txt", ImportTripsFromFile(importPath) + $"; now {cfg.GameTrips.Count} trips");
        }
        catch (Exception e) { File.WriteAllText((exportPath ?? importPath) + ".result.txt", "FAILED " + e.Message); }
    }
}
