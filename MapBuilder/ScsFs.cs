using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Ets2Gps.MapBuilder;

/// <summary>CityHash64, the hash SCS uses for paths in its archives (port of the Python reader / ts-map).</summary>
public static class CityHash
{
    const ulong K0 = 0xC3A5C85C97CB3127, K1 = 0xB492B66FBE98F273, K2 = 0x9AE16A3B2F90404F, K3 = 0xC949D7C7509E6557;

    static ulong Rot(ulong v, int s) => s == 0 ? v : (v >> s) | (v << (64 - s));
    static ulong F64(byte[] s, int p) => BinaryPrimitives.ReadUInt64LittleEndian(s.AsSpan(p));
    static ulong F32(byte[] s, int p) => BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(p));

    static ulong H128(ulong u, ulong v)
    {
        const ulong mul = 0x9DDFEA08EB382D69;
        ulong a = (u ^ v) * mul; a ^= a >> 47;
        ulong b = (v ^ a) * mul; b ^= b >> 47;
        return b * mul;
    }

    static ulong SMix(ulong v) => v ^ (v >> 47);

    static (ulong, ulong) Weak(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rot(b + a + z, 21);
        ulong c = a;
        a += x + y;
        b += Rot(a, 44);
        return (a + z, b + c);
    }

    static (ulong, ulong) WeakS(byte[] s, int p, ulong a, ulong b) => Weak(F64(s, p), F64(s, p + 8), F64(s, p + 16), F64(s, p + 24), a, b);

    public static ulong Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    public static ulong Hash(byte[] s)
    {
        int n = s.Length;
        if (n <= 16)
        {
            if (n > 8) { ulong a = F64(s, 0), b = F64(s, n - 8); return H128(a, Rot(b + (ulong)n, n)) ^ b; }
            if (n >= 4) { ulong a = F32(s, 0); return H128((ulong)n + (a << 3), F32(s, n - 4)); }
            if (n > 0)
            {
                ulong y = (ulong)s[0] + ((ulong)s[n >> 1] << 8), z = (ulong)n + ((ulong)s[n - 1] << 2);
                return SMix((y * K2) ^ (z * K3)) * K2;
            }
            return K2;
        }
        if (n <= 32)
        {
            ulong a = F64(s, 0) * K1, b = F64(s, 8), c = F64(s, n - 8) * K2, d = F64(s, n - 16) * K0;
            return H128(Rot(a - b, 43) + Rot(c, 30) + d, a + Rot(b ^ K3, 20) - c + (ulong)n);
        }
        if (n <= 64)
        {
            ulong z = F64(s, 24);
            ulong a = F64(s, 0) + ((ulong)n + F64(s, n - 16)) * K0;
            ulong b = Rot(a + z, 52), c = Rot(a, 37);
            a += F64(s, 8); c += Rot(a, 7); a += F64(s, 16);
            ulong vf = a + z, vs = b + Rot(a, 31) + c;
            a = F64(s, 16) + F64(s, n - 32);
            z = F64(s, n - 8);
            b = Rot(a + z, 52); c = Rot(a, 37);
            a += F64(s, n - 24); c += Rot(a, 7); a += F64(s, n - 16);
            ulong wf = a + z, ws = b + Rot(a, 31) + c;
            ulong r = SMix((vf + ws) * K2 + (wf + vs) * K0);
            return SMix(r * K0 + vs) * K2;
        }
        {
            ulong x = F64(s, n - 40);
            ulong y = F64(s, n - 16) + F64(s, n - 56);
            ulong z = H128(F64(s, n - 48) + (ulong)n, F64(s, n - 24));
            var v = WeakS(s, n - 64, (ulong)n, z);
            var w = WeakS(s, n - 32, y + K1, x);
            x = x * K1 + F64(s, 0);
            int pos = 0;
            long ln = (n - 1) & ~63;
            for (;;)
            {
                x = Rot(x + y + v.Item1 + F64(s, pos + 8), 37) * K1;
                y = Rot(y + v.Item2 + F64(s, pos + 48), 42) * K1;
                x ^= w.Item2;
                y += v.Item1 + F64(s, pos + 40);
                z = Rot(z + w.Item1, 33) * K1;
                v = WeakS(s, pos, v.Item2 * K1, x + w.Item1);
                w = WeakS(s, pos + 32, z + w.Item2, y + F64(s, pos + 16));
                (z, x) = (x, z);
                pos += 64; ln -= 64;
                if (ln == 0) break;
            }
            return H128(H128(v.Item1, w.Item1) + SMix(y) * K1 + z, H128(v.Item2, w.Item2) + x);
        }
    }
}

/// <summary>One mounted source of files: a HashFS .scs, a zip mod or an unpacked mod folder.</summary>
public abstract class Archive
{
    public string Path = "";
    public int Version;
    /// <summary>hash -> is directory listing</summary>
    public Dictionary<ulong, bool> Entries = new();
    public abstract byte[] Read(ulong hash);
}

public sealed class HashArchive : Archive, IDisposable
{
    record struct Entry(long Offset, int Size, int CSize, bool IsDir, int Method);
    readonly FileStream f;
    readonly Dictionary<ulong, Entry> entries = new();

    public HashArchive(string path)
    {
        Path = path;
        f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Parse();
        foreach (var kv in entries) Entries[kv.Key] = kv.Value.IsDir;
    }

    byte[] ReadAt(long off, int n) { f.Seek(off, SeekOrigin.Begin); var b = new byte[n]; f.ReadExactly(b, 0, n); return b; }

    static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    void Parse()
    {
        var hdr = ReadAt(0, 0x14);
        uint magic = BitConverter.ToUInt32(hdr, 0); ushort ver = BitConverter.ToUInt16(hdr, 4);
        uint method = BitConverter.ToUInt32(hdr, 8); uint count = BitConverter.ToUInt32(hdr, 12); uint start = BitConverter.ToUInt32(hdr, 16);
        if (magic != 0x23534353 || method != 0x59544943) throw new InvalidDataException("not a CITY hashfs archive");
        Version = ver;
        if (ver == 1)
        {
            var raw = ReadAt(start, (int)count * 0x20);
            for (int i = 0; i < count; i++)
            {
                int o = i * 0x20;
                ulong h = BitConverter.ToUInt64(raw, o); long off = (long)BitConverter.ToUInt64(raw, o + 8);
                uint flags = BitConverter.ToUInt32(raw, o + 16); int size = (int)BitConverter.ToUInt32(raw, o + 24), csize = (int)BitConverter.ToUInt32(raw, o + 28);
                entries[h] = new Entry(off, size, csize, (flags & 1) != 0, (flags & 2) != 0 ? 1 : 0);
            }
        }
        else if (ver == 2)
        {
            var h2 = ReadAt(0x10, 28);
            int etSize = (int)BitConverter.ToUInt32(h2, 0), mdSize = (int)BitConverter.ToUInt32(h2, 8);
            long etOff = BitConverter.ToInt64(h2, 12), mdOff = BitConverter.ToInt64(h2, 20);
            var ent = Inflate(ReadAt(etOff, etSize));
            var md = Inflate(ReadAt(mdOff, mdSize));
            for (int i = 0; i < count; i++)
            {
                int o = i * 0x10;
                ulong h = BitConverter.ToUInt64(ent, o);
                int mstart = BitConverter.ToInt32(ent, o + 8);
                int mcount = BitConverter.ToUInt16(ent, o + 12);
                uint flags = BitConverter.ToUInt32(ent, o + 12);
                bool isDir = ((flags >> 16) & 1) != 0;
                for (int j = 0; j < mcount; j++)
                {
                    uint m0 = BitConverter.ToUInt32(md, (mstart + j) * 4);
                    uint typ = m0 >> 24;
                    if (typ >= 0x80 && typ <= 0x84)
                    {
                        int ro = (int)(m0 & 0xFFFFFF) * 4;
                        uint d0 = BitConverter.ToUInt32(md, ro), d1 = BitConverter.ToUInt32(md, ro + 4), d3 = BitConverter.ToUInt32(md, ro + 12);
                        entries[h] = new Entry((long)d3 * 16, (int)(d1 & 0xFFFFFFF), (int)(d0 & 0xFFFFFFF), isDir, (int)(d0 >> 28));
                    }
                }
            }
        }
        else throw new InvalidDataException("unsupported hashfs version " + ver);
    }

    public override byte[] Read(ulong hash)
    {
        var e = entries[hash];
        var data = ReadAt(e.Offset, e.CSize);
        if (e.Size == e.CSize) return data;
        if (e.Method == 1) return Inflate(data);
        throw new InvalidDataException("unsupported compression " + e.Method);
    }

    public void Dispose() => f.Dispose();
}

public sealed class ZipArchiveSource : Archive, IDisposable
{
    readonly ZipArchive z;
    readonly Dictionary<ulong, ZipArchiveEntry> byHash = new();
    public readonly List<string> Names = new();

    public ZipArchiveSource(string path)
    {
        Path = path;
        z = ZipFile.OpenRead(path);
        foreach (var e in z.Entries)
        {
            string n = e.FullName;
            Names.Add(n);
            ulong h = CityHash.Hash(n.Trim('/'));
            byHash[h] = e; Entries[h] = false;
        }
    }

    public override byte[] Read(ulong hash)
    {
        using var s = byHash[hash].Open();
        using var o = new MemoryStream();
        s.CopyTo(o);
        return o.ToArray();
    }

    public void Dispose() => z.Dispose();
}

public sealed class DirArchive : Archive
{
    readonly Dictionary<ulong, string> files = new();
    public readonly List<string> Rel = new();

    public DirArchive(string path)
    {
        Path = path;
        foreach (var full in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            string rel = System.IO.Path.GetRelativePath(path, full).Replace('\\', '/');
            ulong h = CityHash.Hash(rel);
            files[h] = full; Entries[h] = false; Rel.Add(rel);
        }
    }

    public override byte[] Read(ulong hash) => File.ReadAllBytes(files[hash]);
}

/// <summary>One folder of a mounted archive seen as a mod of its own: "base/map/usa/x.base" is read as "map/usa/x.base".</summary>
public sealed class SubdirArchive : Archive
{
    readonly Archive inner;
    readonly Dictionary<ulong, ulong> toInner = new();
    /// <summary>The file names with the folder taken off (zip and folder mods), null for a HashFS archive.</summary>
    public List<string>? Names;

    SubdirArchive(Archive inner) { this.inner = inner; Path = inner.Path; Version = inner.Version; }

    public override byte[] Read(ulong hash) => inner.Read(toInner[hash]);

    void Add(string rel, string full, bool isDir) { ulong h = CityHash.Hash(rel.Trim('/')); toInner[h] = CityHash.Hash(full.Trim('/')); Entries[h] = isDir; }

    /// <summary>The archive's folder as an archive, or null when it has no such folder.</summary>
    public static SubdirArchive? TryCreate(Archive arc, string dir)
    {
        var s = new SubdirArchive(arc);
        string pre = dir + "/";
        if (arc is ZipArchiveSource or DirArchive)
        {
            var names = arc is ZipArchiveSource zs ? zs.Names : ((DirArchive)arc).Rel;
            s.Names = new();
            foreach (var n in names)
                if (n.StartsWith(pre, StringComparison.Ordinal) && n.Length > pre.Length)
                {
                    string rel = n[pre.Length..];
                    s.Names.Add(rel);
                    if (!rel.EndsWith('/')) s.Add(rel, n, false);
                }
            return s.Names.Count > 0 ? s : null;
        }
        // HashFS: walk the folder's directory listings
        if (!arc.Entries.TryGetValue(CityHash.Hash(dir), out bool isDir) || !isDir) return null;
        var todo = new Stack<string>(); todo.Push("");
        while (todo.Count > 0)
        {
            string rel = todo.Pop(), full = rel.Length == 0 ? dir : pre + rel;
            byte[] d;
            try { d = arc.Read(CityHash.Hash(full)); } catch { continue; }
            s.Add(rel, full, true);
            var sub = new SortedSet<string>(StringComparer.Ordinal); var files = new SortedSet<string>(StringComparer.Ordinal);
            FileSystem.ParseDir(d, arc.Version, sub, files);
            string p = rel.Length == 0 ? "" : rel + "/";
            foreach (var f in files) if (arc.Entries.ContainsKey(CityHash.Hash(full + "/" + f))) s.Add(p + f, full + "/" + f, false);
            foreach (var sd in sub) if (arc.Entries.ContainsKey(CityHash.Hash(full + "/" + sd))) todo.Push(p + sd);
        }
        return s.Entries.Count > 1 ? s : null;
    }
}

/// <summary>Union file system: later-mounted archives override earlier ones.</summary>
public sealed class FileSystem
{
    public readonly Dictionary<ulong, Archive> Files = new();
    readonly Dictionary<ulong, (SortedSet<string> Sub, SortedSet<string> Files)> dirs = new();
    public readonly List<string> Mounted = new();
    public Action<string> Log = _ => { };

    static readonly StringComparer Ord = StringComparer.Ordinal;

    static void ListDirsFromNames(IEnumerable<string> names, Dictionary<string, (HashSet<string>, HashSet<string>)> output)
    {
        foreach (var n in names)
        {
            var parts = n.Trim('/').Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                string d = string.Join("/", parts.Take(i));
                if (!output.TryGetValue(d, out var e)) output[d] = e = (new HashSet<string>(), new HashSet<string>());
                if (i == parts.Length - 1 && !n.EndsWith('/')) e.Item2.Add(parts[i]); else e.Item1.Add(parts[i]);
            }
        }
    }

    /// <summary>Mounts a game archive, or with <paramref name="mod"/> a mod (an archive or a folder).</summary>
    public bool Mount(string path, bool mod = false)
    {
        Archive arc;
        try
        {
            if (Directory.Exists(path)) arc = new DirArchive(path);
            else
            {
                var magic = new byte[4];
                using (var fh = File.OpenRead(path)) fh.ReadExactly(magic, 0, 4);
                arc = magic[0] == 'S' && magic[1] == 'C' && magic[2] == 'S' && magic[3] == '#' ? new HashArchive(path) : new ZipArchiveSource(path);
            }
        }
        catch (Exception e) { Log($"  ! could not mount {System.IO.Path.GetFileName(path)}: {e.Message}"); return false; }
        MountArchive(arc);
        // the game also mounts a mod's "base" folder as if it were the mod's top level ("requested subdirectory: base" in game.log);
        // some map mods (ATS Expansion) keep all their files there
        if (mod && SubdirArchive.TryCreate(arc, "base") is { } sub) { MountArchive(sub); Log($"  {System.IO.Path.GetFileName(path)}: its base folder too"); }
        Mounted.Add(path);
        return true;
    }

    void MountArchive(Archive arc)
    {
        if (arc is ZipArchiveSource or DirArchive or SubdirArchive { Names: not null })
        {
            var names = arc is ZipArchiveSource zs ? zs.Names : arc is DirArchive da ? da.Rel : ((SubdirArchive)arc).Names!;
            var tmp = new Dictionary<string, (HashSet<string>, HashSet<string>)>();
            ListDirsFromNames(names, tmp);
            foreach (var (d, (sub, fl)) in tmp)
            {
                ulong h = CityHash.Hash(d);
                if (!dirs.TryGetValue(h, out var e)) dirs[h] = e = (new SortedSet<string>(Ord), new SortedSet<string>(Ord));
                e.Sub.UnionWith(sub); e.Files.UnionWith(fl);
            }
        }
        foreach (var (h, isDir) in arc.Entries)
        {
            if (isDir)
            {
                byte[] d;
                try { d = arc.Read(h); } catch { continue; }
                if (!dirs.TryGetValue(h, out var e)) dirs[h] = e = (new SortedSet<string>(Ord), new SortedSet<string>(Ord));
                ParseDir(d, arc.Version, e.Sub, e.Files);
            }
            else Files[h] = arc;
        }
    }

    internal static void ParseDir(byte[] d, int version, SortedSet<string> sub, SortedSet<string> fl)
    {
        if (version == 1)
        {
            foreach (var line in Encoding.UTF8.GetString(d).Split('\n'))
            {
                if (line.StartsWith('*')) sub.Add(line[1..]);
                else if (line.Length > 0) fl.Add(line);
            }
            return;
        }
        if (d.Length < 4) return;
        int n = (int)BitConverter.ToUInt32(d, 0);
        int p = 4 + n;
        for (int j = 0; j < n; j++)
        {
            int ln = d[4 + j];
            string name = Encoding.UTF8.GetString(d, p, ln);
            p += ln;
            if (name.StartsWith('/')) sub.Add(name[1..]); else fl.Add(name);
        }
    }

    public bool Exists(string path) => Files.ContainsKey(CityHash.Hash(path.Trim('/')));

    public byte[]? Read(string path)
    {
        ulong h = CityHash.Hash(path.Trim('/'));
        return Files.TryGetValue(h, out var arc) ? arc.Read(h) : null;
    }

    public (List<string> Dirs, List<string> Files) ListDir(string path)
    {
        if (!dirs.TryGetValue(CityHash.Hash(path.Trim('/')), out var e)) return (new(), new());
        return (e.Sub.ToList(), e.Files.ToList());
    }
}
