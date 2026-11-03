using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ets2Gps;

/// <summary>
/// The trip file (*.ets2trip, plain JSON) that the Windows app, the web version and the Android app all read and write, so a
/// trip can be shared. Trips are map positions (a start, stops and a destination), so they fit the same map they were made on.
/// A file is data: everything in it is validated and nothing in it is ever executed.
/// </summary>
public static class TripFile
{
    public const string Format = "ets2gps-trip";
    public const int Version = 1, MaxTrips = 200, MaxStops = 50, MaxChars = 1_000_000, MaxName = 80;
    static readonly string[] Modes = { "HEAVY", "FASTEST", "TOURIST" };

    public sealed record Result(List<SavedTrip> Trips, int Skipped, string? Error);

    public static string Serialize(IEnumerable<SavedTrip> trips)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            void Pt(string prop, StopPoint? p)
            {
                if (p == null) { w.WriteNull(prop); return; }
                w.WriteStartObject(prop); w.WriteNumber("x", Math.Round(p.X, 1)); w.WriteNumber("z", Math.Round(p.Z, 1)); w.WriteString("name", p.Name); w.WriteEndObject();
            }
            w.WriteStartObject();
            w.WriteString("format", Format); w.WriteNumber("version", Version);
            w.WriteString("note", "ETS2-ATS GPS trip file. Positions are map coordinates: they fit the same map (game and ProMods versions) they were made on.");
            w.WriteStartArray("trips");
            foreach (var t in trips)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name); w.WriteString("mode", t.Mode); w.WriteBoolean("avoidFerries", t.AvoidFerries);
                Pt("start", t.Start);
                w.WriteStartArray("stops");
                foreach (var s in t.Stops) { w.WriteStartObject(); w.WriteNumber("x", Math.Round(s.X, 1)); w.WriteNumber("z", Math.Round(s.Z, 1)); w.WriteString("name", s.Name); w.WriteEndObject(); }
                w.WriteEndArray();
                Pt("dest", t.Dest);
                w.WriteNumber("lengthM", Math.Round(t.LengthM));
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static string Clean(string? s, string fallback)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) s = fallback;
        return s.Length > MaxName ? s[..MaxName] : s;
    }

    static StopPoint? ReadPoint(JsonElement e, string fallbackName)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("x", out var x) || !e.TryGetProperty("z", out var z) || x.ValueKind != JsonValueKind.Number || z.ValueKind != JsonValueKind.Number) return null;
        double dx = x.GetDouble(), dz = z.GetDouble();
        if (!double.IsFinite(dx) || !double.IsFinite(dz) || Math.Abs(dx) > 1e7 || Math.Abs(dz) > 1e7) return null;
        string name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? Clean(n.GetString(), fallbackName) : fallbackName;
        return new StopPoint { X = (float)dx, Z = (float)dz, Name = name };
    }

    /// <summary>Reads a trip file. Trips that are not valid are skipped and counted; a file that is not a trip file gives an error.</summary>
    public static Result Parse(string text)
    {
        var none = new List<SavedTrip>();
        if (text.Length > MaxChars) return new Result(none, 0, "That file is too big to be a trip file");
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var f) || f.GetString() != Format) return new Result(none, 0, "This is not an ETS2-ATS GPS trip file");
            if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() > Version) return new Result(none, 0, "This trip file is from a newer version of ETS2-ATS GPS - update the app");
            if (!root.TryGetProperty("trips", out var arr) || arr.ValueKind != JsonValueKind.Array) return new Result(none, 0, "The file contains no trips");
            var list = new List<SavedTrip>(); int skipped = 0;
            foreach (var e in arr.EnumerateArray())
            {
                if (list.Count >= MaxTrips) { skipped++; continue; }
                var dest = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("dest", out var d) ? ReadPoint(d, "Destination") : null;
                if (dest == null) { skipped++; continue; }
                var stops = new List<StopPoint>(); bool bad = false;
                if (e.TryGetProperty("stops", out var st) && st.ValueKind == JsonValueKind.Array)
                    foreach (var s in st.EnumerateArray())
                    {
                        var p = ReadPoint(s, "Stop " + (stops.Count + 1));
                        if (p == null || stops.Count >= MaxStops) { bad = true; break; }
                        stops.Add(p);
                    }
                if (bad) { skipped++; continue; }
                StopPoint? start = e.TryGetProperty("start", out var sp) && sp.ValueKind == JsonValueKind.Object ? ReadPoint(sp, "Start") : null;
                string mode = e.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "").ToUpperInvariant() : "HEAVY";
                if (!Modes.Contains(mode)) mode = "HEAVY";
                float len = e.TryGetProperty("lengthM", out var l) && l.ValueKind == JsonValueKind.Number && double.IsFinite(l.GetDouble()) ? (float)Math.Clamp(l.GetDouble(), 0, 1e8) : 0f;
                string name = Clean(e.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null, start != null ? start.Name + " → " + dest.Name : "Imported trip");
                list.Add(new SavedTrip
                {
                    Name = name, Mode = mode, AvoidFerries = e.TryGetProperty("avoidFerries", out var af) && af.ValueKind == JsonValueKind.True,
                    Start = start, Dest = dest, Stops = stops, LengthM = len,
                });
            }
            return new Result(list, skipped, null);
        }
        catch (JsonException) { return new Result(none, 0, "This is not an ETS2-ATS GPS trip file"); }
        catch (Exception e) { return new Result(none, 0, "Could not read the file: " + e.Message); }
    }
}
