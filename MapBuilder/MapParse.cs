using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ets2Gps.MapBuilder;

/// <summary>Parses ETS2 map data (.base sectors, defs, prefabs) out of the virtual file system. Binary layouts follow ts-map (sector version 884+).</summary>
public static class Bin
{
    public static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    public static int I32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
    public static ulong U64(byte[] d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o));
    public static float F32(byte[] d, int o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o));
    public static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
    public static short I16(byte[] d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o));
}

public sealed class SiiProps
{
    public readonly Dictionary<string, string> S = new();
    public readonly Dictionary<string, List<string>> L = new();
    public bool Has(string k) => S.ContainsKey(k);
    public string Get(string k, string def = "") => S.TryGetValue(k, out var v) ? v : def;
    public List<string> List(string k) => L.TryGetValue(k, out var v) ? v : new();
}

public static class Sii
{
    const string LetterSet = "\0" + "0123456789abcdefghijklmnopqrstuvwxyz_";

    /// <summary>SCS token: base-38 packed name. UInt128 so that names longer than 12 letters never alias a 64-bit token.</summary>
    public static UInt128 StrToToken(string text)
    {
        UInt128 res = 0, pow = 1;
        foreach (char c in text.ToLowerInvariant())
        {
            int id = LetterSet.IndexOf(c);
            if (id < 0) id = 0;
            res += pow * (UInt128)id;
            pow *= 38;
        }
        return res;
    }

    public static string TokenToStr(UInt128 tok)
    {
        var sb = new StringBuilder();
        while (tok != 0) { sb.Append(LetterSet[(int)(tok % 38)]); tok /= 38; }
        return sb.ToString();
    }

    static readonly Regex Header = new(@"^([A-Za-z_]\w*)\s*:\s*([^\s{]+)\s*(\{)?\s*$", RegexOptions.Compiled);
    static readonly Regex IndexKey = new(@"\[\d+\]$", RegexOptions.Compiled);

    /// <summary>Each `class : name { key: value }` block of a SII text.</summary>
    public static IEnumerable<(string Cls, string Name, SiiProps Props)> Blocks(string text)
    {
        string? cls = null, name = null;
        SiiProps? props = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw;
            int i = line.IndexOf('#'); if (i >= 0) line = line[..i];
            i = line.IndexOf("//", StringComparison.Ordinal); if (i >= 0) line = line[..i];
            line = line.Trim();
            if (line.Length == 0) continue;
            if (props == null)
            {
                var m = Header.Match(line);
                if (m.Success) { cls = m.Groups[1].Value; name = m.Groups[2].Value; props = new SiiProps(); }
                continue;
            }
            if (line.StartsWith('}')) { yield return (cls!, name!, props); cls = name = null; props = null; continue; }
            if (line == "{") continue;
            int c = line.IndexOf(':');
            if (c >= 0)
            {
                string k = line[..c].Trim(), v = line[(c + 1)..].Trim();
                if (k.EndsWith("[]")) { if (!props.L.TryGetValue(k, out var l)) props.L[k] = l = new(); l.Add(v); }
                else if (IndexKey.IsMatch(k)) { string k2 = IndexKey.Replace(k, "[]"); if (!props.L.TryGetValue(k2, out var l)) props.L[k2] = l = new(); l.Add(v); }
                else props.S[k] = v;
            }
        }
    }

    public static string Str(string v)
    {
        var m = Regex.Match(v, "\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : v.Trim();
    }

    public static string Decode(byte[] b)
    {
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1252).GetString(b); }
    }
}

public sealed record City(string Id, string Name, string Country, string Loc);
/// <summary>Track: a gravel or dirt road, driveway or farm road without AI traffic (only "no_vehicles" / "side_road" lanes). Trucks can drive it and the
/// game's GPS routes over it, so it is kept as a slow two-way road.</summary>
public sealed record RoadLook(int Left, int Right, double Offset, int DriveLeft, int DriveRight, string Side, int Grade, bool Track = false);

public sealed class Defs
{
    readonly FileSystem fs;
    readonly Action<string> log;
    public readonly Dictionary<UInt128, City> Cities = new();
    public readonly Dictionary<UInt128, RoadLook> RoadLooks = new();
    public readonly Dictionary<UInt128, string> PrefabPaths = new();
    public readonly List<(UInt128 A, UInt128 B, double Minutes)> FerryConns = new();
    /// <summary>Company id ("stokes") -> the name shown in the game ("DHL").</summary>
    public readonly Dictionary<string, string> CompanyNames = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Country id ("germany") -> ISO alpha-3 ("deu").</summary>
    public readonly Dictionary<string, string> CountryIso = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<UInt128, Prefab?> prefabCache = new();

    public Defs(FileSystem fs, Action<string> log)
    {
        this.fs = fs; this.log = log;
        Load();
    }

    List<string> Files(string folder, string prefix) => fs.ListDir(folder).Files.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    static bool IsDriveLane(string x) => x.StartsWith("traffic_lane.road", StringComparison.Ordinal) || x.StartsWith("traffic_lane.slow_road", StringComparison.Ordinal);
    static bool IsTrackLane(string x) => x.StartsWith("traffic_lane.no_vehicles", StringComparison.Ordinal) || x.StartsWith("traffic_lane.side_road", StringComparison.Ordinal);

    static double ParseDouble(string s, double def) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    void Load()
    {
        foreach (var f in Files("def", "city"))
        {
            string txt = Sii.Decode(fs.Read("def/" + f) ?? Array.Empty<byte>());
            foreach (Match m in Regex.Matches(txt, "@include\\s+\"([^\"]+)\""))
            {
                string inc = m.Groups[1].Value;
                var data = fs.Read(inc.StartsWith('/') ? inc : "def/" + inc);
                if (data == null) continue;
                foreach (var (cls, name, p) in Sii.Blocks(Sii.Decode(data)))
                {
                    if (cls != "city_data") continue;
                    string cid = name[(name.IndexOf('.') + 1)..];
                    var tok = Sii.StrToToken(cid);
                    if (!Cities.ContainsKey(tok))
                        Cities[tok] = new City(cid, Sii.Str(p.Get("city_name", cid)), p.Get("country"), Sii.Str(p.Get("city_name_localized")).Trim('@'));
                }
            }
        }
        foreach (var f in Files("def/world", "road_look"))
            foreach (var (cls, name, p) in Sii.Blocks(Sii.Decode(fs.Read("def/world/" + f) ?? Array.Empty<byte>())))
            {
                if (cls != "road_look") continue;
                var tok = Sii.StrToToken(name[(name.IndexOf('.') + 1)..]);
                if (RoadLooks.ContainsKey(tok)) continue;
                double off = ParseDouble(p.Get("road_offset", "0"), 0.0);
                var ll = p.List("lanes_left[]"); var rr = p.List("lanes_right[]");
                string side = "";
                if (p.Has("template_right") && !p.Has("template_left")) side = "R";
                else if (p.Has("template_left") && !p.Has("template_right")) side = "L";
                var lanes = ll.Concat(rr).Where(IsDriveLane).ToList();
                int grade = lanes.Any(x => x.Contains(".motorway")) ? 3 : lanes.Any(x => x.Contains(".expressway")) ? 2 : lanes.Count >= 4 ? 1 : 0;
                if (lanes.Count == 0 && ll.Concat(rr).Any(IsTrackLane)) RoadLooks[tok] = new RoadLook(ll.Count, rr.Count, off, 1, 1, "", 0, true);
                else RoadLooks[tok] = new RoadLook(ll.Count, rr.Count, off, ll.Count(IsDriveLane), rr.Count(IsDriveLane), side, grade);
            }
        foreach (var f in Files("def/world", "prefab"))
            foreach (var (cls, name, p) in Sii.Blocks(Sii.Decode(fs.Read("def/world/" + f) ?? Array.Empty<byte>())))
            {
                if (cls != "prefab_model" || !p.Has("prefab_desc")) continue;
                var tok = Sii.StrToToken(name[(name.IndexOf('.') + 1)..]);
                if (!PrefabPaths.ContainsKey(tok)) PrefabPaths[tok] = Sii.Str(p.S["prefab_desc"]);
            }
        foreach (var f in fs.ListDir("def/company").Files)
        {
            if (!f.EndsWith(".sii") && !f.EndsWith(".sui")) continue;
            foreach (var (cls, name, p) in Sii.Blocks(Sii.Decode(fs.Read("def/company/" + f) ?? Array.Empty<byte>())))
            {
                if (cls != "company_permanent" || !p.Has("name")) continue;
                string id = name[(name.LastIndexOf('.') + 1)..];
                string shown = Sii.Str(p.S["name"]).Trim();
                if (shown.Length > 0 && !CompanyNames.ContainsKey(id)) CompanyNames[id] = shown;
            }
        }
        foreach (var f in fs.ListDir("def/country").Files)
        {
            if (!f.EndsWith(".sui") && !f.EndsWith(".sii")) continue;
            foreach (var (cls, name, p) in Sii.Blocks(Sii.Decode(fs.Read("def/country/" + f) ?? Array.Empty<byte>())))
                if (cls == "country_data" && p.Has("iso_country_code")) CountryIso[name[(name.LastIndexOf('.') + 1)..]] = p.S["iso_country_code"].Trim().Trim('"');
        }
        const string dir = "def/ferry/connection";
        foreach (var f in fs.ListDir(dir).Files)
        {
            if (!f.EndsWith(".sii") && !f.EndsWith(".sui")) continue;
            foreach (var (cls, name, p) in Sii.Blocks(Sii.Decode(fs.Read(dir + "/" + f) ?? Array.Empty<byte>())))
            {
                var parts = name.Split('.');
                if (cls == "ferry_connection" && parts.Length >= 3)
                    FerryConns.Add((Sii.StrToToken(parts[1]), Sii.StrToToken(parts[2]), ParseDouble(p.Get("time", "60"), 60.0)));
            }
        }
        log($"defs: {Cities.Count} cities, {RoadLooks.Count} road looks, {PrefabPaths.Count} prefabs");
    }

    public Prefab? Prefab(UInt128 tok)
    {
        if (prefabCache.TryGetValue(tok, out var c)) return c;
        Prefab? pf = null;
        if (PrefabPaths.TryGetValue(tok, out var path))
        {
            var data = fs.Read(path);
            if (data != null)
            {
                try { pf = PrefabParser.Parse(data); }
                catch (Exception e) { log($"  ! prefab {path}: {e.Message}"); }
            }
        }
        prefabCache[tok] = pf;
        return pf;
    }

    /// <summary>key -> text from every locale/{lang}/*.sii|sui (later mounts override).</summary>
    public static Dictionary<string, string> LoadLocale(FileSystem fs, string lang = "en_us")
    {
        var output = new Dictionary<string, string>();
        var pat = new Regex("key\\[\\]:\\s*\"([^\"]*)\"[^\\n]*\\n\\s*val\\[\\]:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        foreach (var f in fs.ListDir("locale/" + lang).Files)
        {
            if (!f.EndsWith(".sii") && !f.EndsWith(".sui")) continue;
            var d = fs.Read($"locale/{lang}/{f}");
            if (d == null || d.Length == 0) continue;
            foreach (Match m in pat.Matches(Sii.Decode(d))) output[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return output;
    }
}

public sealed class Prefab
{
    public int Version, NavCount, NavOff;
    public List<(double X, double Z, double Rx, double Rz, int Lanes)> Nodes = new();
    /// <summary>Height (metres) of each node in the prefab's own frame, and of each nav curve's start and end.</summary>
    public List<double> NodeY = new();
    public List<(double X, double Z, uint Type)> Spawns = new();
    public List<(uint Id, ulong Action, double X, double Z)> Triggers = new();
    /// <summary>The filled shapes the game draws for this prefab on its map (yards, lots, buildings, grass), in the prefab's own frame,
    /// bottom first. Color: the map area classes (0 road grey, 1 light, 2 dark, 3 green).</summary>
    public List<(byte Color, List<(double X, double Z)> Pts)> Polys = new();
    public byte[] Raw = Array.Empty<byte>();
}

public static class PrefabParser
{
    public static Prefab Parse(byte[] d)
    {
        int ver = Bin.I32(d, 0);
        if (ver < 0x15) throw new InvalidDataException($"ppd version {ver} too old");
        var pf = new Prefab { Version = ver };
        int nodeCount = Bin.I32(d, 4), navCount = Bin.I32(d, 8);
        int o = 0x08;
        o += 0x0C;
        int spawnCount = Bin.I32(d, o);
        o += 0x0C;
        int mapCount = Bin.I32(d, o);
        o += 0x04;
        int trigCount = Bin.I32(d, o);
        if (ver > 0x15) o += 0x04;
        o += 0x08;
        int nodeOff = Bin.I32(d, o), navOff = Bin.I32(d, o + 4);
        o += 0x10;
        int spawnOff = Bin.I32(d, o);
        o += 0x10;
        int mapOff = Bin.I32(d, o), trigOff = Bin.I32(d, o + 4);
        pf.NavCount = navCount; pf.NavOff = navOff;
        for (int i = 0; i < nodeCount; i++)
        {
            int b = nodeOff + i * 0x68;
            double x = Bin.F32(d, b + 0x10), z = Bin.F32(d, b + 0x18), rx = Bin.F32(d, b + 0x1C), rz = Bin.F32(d, b + 0x24);
            int lanes = 0, p = b + 0x24;
            for (int k = 0; k < 16; k++) { p += 4; if (Bin.I32(d, p) != -1) lanes++; }
            pf.Nodes.Add((x, z, rx, rz, lanes));
            pf.NodeY.Add(Bin.F32(d, b + 0x14));
        }
        int sbs = ver >= 24 ? 0x24 : 0x20;
        for (int i = 0; i < spawnCount; i++)
        {
            int b = spawnOff + i * sbs;
            pf.Spawns.Add((Bin.F32(d, b), Bin.F32(d, b + 8), Bin.U32(d, b + 0x1C)));
        }
        for (int i = 0; i < trigCount; i++)
        {
            int b = trigOff + i * 0x30;
            if (b + 0x30 > d.Length) break;
            pf.Triggers.Add((Bin.U32(d, b), Bin.U64(d, b + 4), Bin.F32(d, b + 0x1C), Bin.F32(d, b + 0x24)));
        }
        ParsePolys(pf, d, mapOff, mapCount);
        pf.Raw = d;
        return pf;
    }

    /// <summary>
    /// Map points (0x30 bytes each): byte 1 low nibble 13 marks a polygon corner, byte 2 its colour flags (2 light, 4 dark, 8 green, else road
    /// grey; 1 = drawn over the others), the position at 0x08 / 0x10 and up to six neighbours at 0x14 with their count at 0x2C. Each polygon is
    /// a ring of corners linked to their neighbours.
    /// </summary>
    static void ParsePolys(Prefab pf, byte[] d, int off, int count)
    {
        if (count <= 0 || off <= 0 || off + count * 0x30 > d.Length) return;
        var used = new bool[count];
        var polys = new List<(int Z, int Seq, byte Color, List<(double X, double Z)> Pts)>();
        bool Corner(int i) => (d[off + i * 0x30 + 1] & 0x0F) == 13;
        for (int s = 0; s < count; s++)
        {
            if (used[s] || !Corner(s)) continue;
            var ring = new List<(double X, double Z)>();
            int prev = -1, cur = s;
            while (cur >= 0 && !used[cur] && ring.Count < 200)
            {
                used[cur] = true;
                int b = off + cur * 0x30;
                ring.Add((Bin.F32(d, b + 0x08), Bin.F32(d, b + 0x10)));
                int nn = Math.Clamp(Bin.I32(d, b + 0x2C), 0, 6), next = -1;
                for (int k = 0; k < nn; k++)
                {
                    int q = Bin.I32(d, b + 0x14 + 4 * k);
                    if (q >= 0 && q < count && q != prev && !used[q] && Corner(q)) { next = q; break; }
                }
                prev = cur; cur = next;
            }
            if (ring.Count < 3) continue;
            byte f = d[off + s * 0x30 + 2];
            byte color = (f & 2) != 0 ? (byte)1 : (f & 4) != 0 ? (byte)2 : (f & 8) != 0 ? (byte)3 : (byte)0;
            int z = (f & 1) != 0 ? 4 : color == 1 ? 0 : color == 0 ? 1 : color == 2 ? 2 : 3;   // the light yard at the bottom, then grey, dark, green
            polys.Add((z, polys.Count, color, ring));
        }
        foreach (var p in polys.OrderBy(p => p.Z).ThenBy(p => p.Seq)) pf.Polys.Add((p.Color, p.Pts));
    }
}

public sealed class Sector
{
    public List<(ulong Uid, ulong Su, ulong Eu, ulong Look, bool Secret, byte Dlc, bool Hidden)> Roads = new();
    public List<(ulong Uid, ulong PrefabTok, List<ulong> Nodes, int Origin, bool Secret, byte Dlc, bool Hidden)> Prefabs = new();
    public List<(ulong Tok, double X, double Z)> Cities = new();
    public List<(ulong Company, ulong City, double X, double Z)> Companies = new();
    public List<(ulong Port, ulong Link, ulong Node, bool Train, double X, double Z)> Ferries = new();
    public List<(int Type, double X, double Z)> Overlays = new();
    /// <summary>Road-number overlays (token such as al_sh2) and the map areas (lots, yards, green patches) with their colour class.</summary>
    public List<(ulong Tok, double X, double Z)> RoadLabels = new();
    public List<(List<ulong> Nodes, uint Color)> Areas = new();
    /// <summary>Rest-area triggers placed in the map (action hud_parking): their corner nodes.</summary>
    public List<List<ulong>> ParkingTriggers = new();
    public Dictionary<ulong, (double X, double Z, double Rot)> Nodes = new();
    public Dictionary<ulong, double> NodeY = new();
    public bool Ok;
    public int Version;
}

public static class SectorParser
{
    const int Veg = 0x14;
    const int TTerrain = 1, TBuilding = 2, TRoad = 3, TPrefab = 4, TModel = 5, TCompany = 6, TService = 7, TCutPlane = 8,
        TCity = 12, TMapOverlay = 18, TFerry = 19, TGarage = 22, TTrigger = 34, TFuelPump = 35, TRoadside = 36,
        TBusStop = 37, TTrafficRule = 38, TBezier = 39, TTrajectory = 41, TMapArea = 42, TCurve = 44, TCutScene = 46, TVisArea = 48;

    static int Quad(byte[] d, int o)
    {
        int mc = Bin.U16(d, o);
        int p = o + 2 + 0x0A * mc;
        int cc = Bin.U16(d, p);
        p += 2 + 4 * cc + 4;
        int sc = Bin.I32(d, p);
        p += 4 + 4 * sc;
        int oc = Bin.I32(d, p);
        p += 4 + 0x10 * oc;
        int nc = Bin.I32(d, p);
        return p + 4 + 0x10 * nc - o;
    }

    public static Sector Parse(byte[] d, Defs defs)
    {
        var sec = new Sector();
        int ver = Bin.I32(d, 0);
        sec.Version = ver;
        if (ver < 884) return sec;
        uint count = Bin.U32(d, 0x10);
        int o = 0x14;
        for (uint item = 0; item < count; item++)
        {
            int typ = (int)Bin.U32(d, o);
            int f = o + 0x34;
            ulong uid = Bin.U64(d, o + 4);
            double x = Bin.F32(d, o + 0x0C), z = Bin.F32(d, o + 0x14);
            int e, p, n, nc;
            switch (typ)
            {
                case TTerrain:
                    {
                        int toff = ver >= 903 ? 0xEE : 0xEA;
                        n = Bin.I32(d, f + 0x05 + toff);
                        e = f + 0x05 + toff + 4 + Veg * n;
                        e += Quad(d, e);
                        e += Quad(d, e);
                        e += 0x20;
                        break;
                    }
                case TBuilding:
                    n = Bin.I32(d, f + 0x05 + 0x2C);
                    e = f + 0x05 + 0x2C + 4 + 4 * n;
                    break;
                case TRoad:
                    {
                        bool hidden = (d[f + 3] & 2) != 0, secret = (d[f + 2] & 1) != 0;
                        ulong look = Bin.U64(d, f + 9);
                        p = f + 9 + 8 + (ver >= 895 ? 0xB4 : 0xA4);
                        ulong su = Bin.U64(d, p), eu = Bin.U64(d, p + 8);
                        e = p + 8 + 8 + 4;
                        if (defs.RoadLooks.ContainsKey(look)) sec.Roads.Add((uid, su, eu, look, secret, d[f + 6], hidden));
                        break;
                    }
                case TPrefab:
                    {
                        bool hidden = (d[f + 2] & 2) != 0, secret = ((d[f] >> 5) & 1) != 0;
                        ulong ptok = Bin.U64(d, f + 5);
                        p = f + 5 + 0x10;
                        int apc = Bin.I32(d, p);
                        p += 4 + 8 * apc;
                        nc = Bin.I32(d, p);
                        p += 4;
                        var nodes = new List<ulong>();
                        for (int k = 0; k < nc; k++) nodes.Add(Bin.U64(d, p + 8 * k));
                        p += 8 * nc;
                        int ci = Bin.I32(d, p);
                        p += 4 + 8 * ci + 8;
                        int origin = d[p];
                        e = p + 2 + nc * 0x0C + 8;
                        sec.Prefabs.Add((uid, ptok, nodes, origin, secret, d[f + 1], hidden));
                        break;
                    }
                case TModel:
                    if (ver < 895) throw new InvalidDataException($"model item layout for sector v{ver} not supported");
                    n = Bin.I32(d, f + 0x05 + 0x18);
                    e = f + 0x05 + 0x18 + 4 + 8 * n + 0x24;
                    break;
                case TCompany:
                    if (ver >= 903) sec.Companies.Add((Bin.U64(d, f + 0x15), Bin.U64(d, f + 5), x, z));
                    else sec.Companies.Add((Bin.U64(d, f + 5), Bin.U64(d, f + 0xD), x, z));
                    p = f + 0x25;
                    n = Bin.I32(d, p);
                    if (ver >= 900) e = p + 4 + 0x0C * n;
                    else
                    {
                        for (int k = 0; k < 5; k++) { p += 4 + 8 * n; n = Bin.I32(d, p); }
                        e = p + 4 + 8 * n;
                    }
                    break;
                case TService:
                case TFuelPump:
                    n = Bin.I32(d, f + 0x05 + 0x10);
                    e = f + 0x05 + 0x10 + 4 + 8 * n;
                    break;
                case TGarage:
                    n = Bin.I32(d, f + 0x05 + 0x1C);
                    e = f + 0x05 + 0x1C + 4 + 8 * n;
                    break;
                case TCutPlane:
                    n = Bin.I32(d, f + 5);
                    e = f + 5 + 4 + 8 * n;
                    break;
                case TCity:
                    {
                        bool hidden = (d[f] & 1) != 0;
                        ulong ctok = Bin.U64(d, f + 5);
                        e = f + 0x1D;
                        if (!hidden && defs.Cities.ContainsKey(ctok)) sec.Cities.Add((ctok, x, z));
                        break;
                    }
                case TMapOverlay:
                    {
                        ulong tok = Bin.U64(d, f + 5);
                        bool isParking = ver >= 898 ? (d[f] & 0x0F) == 1 : ((d[f + 2] & 1) != 0 && tok == 0);
                        if (isParking && d[f] != 255) sec.Overlays.Add((3, x, z));
                        else if (ver >= 898 && (d[f] & 0x0F) == 0 && d[f] != 255 && tok != 0) sec.RoadLabels.Add((tok, x, z));
                        e = f + 0x15;
                        break;
                    }
                case TFerry:
                    sec.Ferries.Add((Bin.U64(d, f + 5), Bin.U64(d, f + 0xD), Bin.U64(d, f + 0x15), d[f] != 0, x, z));
                    e = f + 0x29;
                    break;
                case TTrigger:
                    e = Trigger(d, f, sec);
                    break;
                case TRoadside:
                    e = ver >= 906 ? Roadside(d, f) : Roadside875(d, f);
                    break;
                case TBusStop:
                    e = f + 0x1D;
                    break;
                case TTrafficRule:
                    {
                        int tc = Bin.I32(d, f + 5);
                        p = f + 5 + 4 + 8 * tc;
                        nc = Bin.I32(d, p);
                        e = p + 4 + 8 * nc + 0x0C;
                        break;
                    }
                case TBezier:
                    n = Bin.I32(d, f + 0x05 + 0xF1);
                    e = f + 0x05 + 0xF1 + 4 + Veg * n;
                    e += Quad(d, e);
                    break;
                case TTrajectory:
                    nc = Bin.I32(d, f + 5);
                    if (ver >= 895)
                    {
                        p = f + 5 + 4 + 8 * nc + 8;
                        int rr = Bin.I32(d, p);
                        p += 4 + 0x1C * rr;
                        int cp = Bin.I32(d, p);
                        p += 4 + 0x10 * cp;
                        int tg = Bin.I32(d, p);
                        e = p + 4 + 8 * tg;
                    }
                    else
                    {
                        p = f + 5 + 4 + 8 * nc + 0x0C;
                        int rr = Bin.I32(d, p);
                        p += 4 + 0x1C * rr;
                        int cp = Bin.I32(d, p);
                        e = p + 4 + 0x10 * cp + 4;
                    }
                    break;
                case TMapArea:
                    nc = Bin.I32(d, f + 5);
                    e = f + 5 + 4 + 8 * nc + 4;
                    if (nc >= 3 && nc < 1000)
                    {
                        var an = new List<ulong>(nc);
                        for (int k = 0; k < nc; k++) an.Add(Bin.U64(d, f + 9 + 8 * k));
                        sec.Areas.Add((an, Bin.U32(d, e - 4)));
                    }
                    break;
                case TCurve when ver < 907:
                    {
                        int hc = Bin.I32(d, f + 0x05 + 0x6C);
                        e = f + 0x05 + 0x6C + 4 + 4 * hc;
                        break;
                    }
                case TCurve:
                    {
                        int mask = Bin.I32(d, f + 0x05 + 0x20 + 0x04);
                        p = f + 0x05 + 0x20 + 0x04 + 4;
                        int bits = System.Numerics.BitOperations.PopCount((uint)mask);
                        for (int k = 0; k < bits; k++)
                        {
                            p += 0x4C;
                            int hc = Bin.I32(d, p);
                            p += 4 + 4 * hc + 0x14;
                        }
                        e = p;
                        break;
                    }
                case TCutScene:
                    e = CutScene(d, f);
                    break;
                case TVisArea:
                    if (ver < 891) throw new InvalidDataException($"visibility area for sector v{ver} not supported");
                    n = Bin.I32(d, f + 0x05 + 0x10);
                    e = f + 0x05 + 0x10 + 4 + 8 * n;
                    break;
                default:
                    throw new InvalidDataException($"unknown item type {typ} at 0x{o:x}");
            }
            o = e;
        }
        int ncount = Bin.I32(d, o);
        for (int i = 0; i < ncount; i++)
        {
            int b = o + 4 + 0x38 * i;
            ulong nuid = Bin.U64(d, b);
            double nx = Bin.I32(d, b + 8) / 256.0, nz = Bin.I32(d, b + 0x10) / 256.0;
            double rx = Bin.F32(d, b + 0x14), rz = Bin.F32(d, b + 0x1C);
            double rot = ((Math.PI - Math.Atan2(rz, rx)) % Math.PI) * 2; // C fmod, like Python's math.fmod
            sec.Nodes[nuid] = (nx, nz, rot);
            sec.NodeY[nuid] = Bin.I32(d, b + 0x0C) / 256.0;
        }
        o += 0x38 * ncount + 4;
        if (ver >= 891)
        {
            int n2 = Bin.I32(d, o);
            o += 4 + 8 * n2;
        }
        sec.Ok = o == d.Length;
        return sec;
    }

    static readonly ulong HudParking = (ulong)Sii.StrToToken("hud_parking");

    /// <summary>Skips a trigger item; a rest-area trigger (action hud_parking) is kept with its corner nodes.</summary>
    static int Trigger(byte[] d, int f, Sector sec)
    {
        int tc = Bin.I32(d, f + 5);
        int p = f + 5 + 4 + 8 * tc;
        int nc = Bin.I32(d, p);
        int nodesAt = p + 4;
        p += 4 + 8 * nc;
        int ac = Bin.I32(d, p);
        p += 4;
        bool parking = false;
        for (int i = 0; i < ac; i++)
        {
            if (Bin.U64(d, p) == HudParking) parking = true;
            p += 8;
            int ho = Bin.I32(d, p);
            p += 4;
            if (ho < 0) continue;
            p += 4 * ho;
            int pc = Bin.I32(d, p);
            p += 4;
            for (int j = 0; j < pc; j++) { int ln = Bin.I32(d, p); p += 8 + ln; }
            int tt = Bin.I32(d, p);
            p += 4 + tt * 8 + 8;
        }
        if (nc == 1) p += 4;
        if (parking && nc > 0 && nc < 64)
        {
            var ns = new List<ulong>(nc);
            for (int k = 0; k < nc; k++) ns.Add(Bin.U64(d, nodesAt + 8 * k));
            sec.ParkingTriggers.Add(ns);
        }
        return p;
    }

    static int Roadside(byte[] d, int f)
    {
        int p = f + 0x05 + 0x20;
        int bc = (sbyte)d[p];
        p += 1 + bc * 0x18;
        int otl = Bin.I32(d, p);
        p += 8;
        if (otl != 0)
        {
            p += otl;
            int boc = Bin.I32(d, p);
            p += 4;
            for (int i = 0; i < boc; i++)
            {
                p += 8;
                int fl = d[p];
                p += 1;
                if ((fl & 1) != 0) p += 2;
                if ((fl & 2) != 0) p += 8;
            }
            int soc = Bin.I32(d, p);
            p += 4;
            for (int i = 0; i < soc; i++)
            {
                p += 0x0C;
                int ac = Bin.I32(d, p);
                p += 4;
                for (int j = 0; j < ac; j++)
                {
                    int it = Bin.I16(d, p);
                    p += 6;
                    if (it == 5) { int tl = Bin.I32(d, p); p += 8 + tl; }
                    else if (it == 6) p += 8;
                    else if (it == 1) p += 1;
                    else p += 4;
                }
            }
        }
        return p;
    }

    static int Roadside875(byte[] d, int f)
    {
        int p = f + 0x05 + 0x20;
        int bc = (sbyte)d[p];
        p += 1 + bc * 0x18;
        int otl = Bin.I32(d, p);
        if (otl != 0) p += 4 + otl;
        int soc = Bin.I32(d, p + 4);
        p += 8;
        for (int i = 0; i < soc; i++)
        {
            p += 0x0C;
            int ac = Bin.I32(d, p);
            p += 4;
            for (int j = 0; j < ac; j++)
            {
                int it = Bin.I16(d, p);
                p += 6;
                if (it == 5) { int tl = Bin.I32(d, p); p += 8 + tl; }
                else if (it == 6) p += 8;
                else if (it == 1) p += 1;
                else p += 4;
            }
        }
        return p;
    }

    static int CutScene(byte[] d, int f)
    {
        int tc = Bin.I32(d, f + 5);
        int p = f + 5 + 4 + 8 * tc + 8;
        int ac = Bin.I32(d, p);
        p += 4;
        for (int i = 0; i < ac; i++)
        {
            int npc = Bin.I32(d, p);
            p += 4 + 4 * npc;
            int spc = Bin.I32(d, p);
            p += 4;
            for (int j = 0; j < spc; j++) { int tl = Bin.I32(d, p); p += 8 + tl; }
            int tt = Bin.I32(d, p);
            p += 4 + 8 * tt + 8;
        }
        return p;
    }
}
