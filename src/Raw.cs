// Raw access to plugin files: no Mutagen, just the record/group/subrecord layout. Used for the structural pass, the
// header and FormID checks, and a fast index of which records each master holds.
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace SfModCheck;

public static class Flags
{
    public const uint Master = 0x1, Deleted = 0x20, Localized = 0x80, Light = 0x100, Medium = 0x400, Compressed = 0x40000;
}

public enum Style { Full, Medium, Light }

/// <summary>A read-only memory-mapped plugin file.</summary>
public sealed unsafe class RawFile : IDisposable
{
    readonly MemoryMappedFile? map;
    readonly MemoryMappedViewAccessor? view;
    readonly byte* p;
    public long Length { get; }
    public string Path { get; }

    public RawFile(string path)
    {
        Path = path;
        Length = new FileInfo(path).Length;
        if (Length == 0) return;
        map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        p += view.PointerOffset;
    }

    public uint U32(long o) => *(uint*)(p + o);
    public int I32(long o) => *(int*)(p + o);
    public ushort U16(long o) => *(ushort*)(p + o);
    public string Sig(long o) => Encoding.Latin1.GetString(p + o, 4);
    public ReadOnlySpan<byte> Span(long o, int n) => new(p + o, n);

    public void Dispose()
    {
        if (view != null) { view.SafeMemoryMappedViewHandle.ReleasePointer(); view.Dispose(); }
        map?.Dispose();
    }
}

/// <summary>The TES4 header as stored in the file.</summary>
public sealed class RawHeader
{
    public uint Flags;
    public ushort FormVersion;
    public float? Version;
    public uint? NumRecords, NextObjectId;
    public List<string> Masters = new();
    public List<(string Type, int Size)> Fields = new();
    public uint Size;

    public Style Style => (Flags & SfModCheck.Flags.Light) != 0 ? Style.Light
        : (Flags & SfModCheck.Flags.Medium) != 0 ? Style.Medium : Style.Full;

    /// <summary>Reads only the header; null if the file is not a plugin.</summary>
    public static RawHeader? Read(string path)
    {
        using var s = File.OpenRead(path);
        var head = new byte[24];
        if (s.Read(head, 0, 24) != 24 || Encoding.Latin1.GetString(head, 0, 4) != "TES4") return null;
        var h = new RawHeader { Size = BitConverter.ToUInt32(head, 4), Flags = BitConverter.ToUInt32(head, 8), FormVersion = BitConverter.ToUInt16(head, 20) };
        if (h.Size > s.Length - 24) return h;
        var body = new byte[h.Size];
        s.ReadExactly(body);
        foreach (var (type, data) in Subrecords.Parse(body, out _))
        {
            h.Fields.Add((type, data.Length));
            if (type == "MAST") h.Masters.Add(Encoding.UTF8.GetString(data).TrimEnd('\0'));
            if (type == "HEDR" && data.Length >= 12)
            {
                h.Version = BitConverter.ToSingle(data, 0);
                h.NumRecords = BitConverter.ToUInt32(data, 4);
                h.NextObjectId = BitConverter.ToUInt32(data, 8);
            }
        }
        return h;
    }
}

public static class Subrecords
{
    /// <summary>Splits a record body into subrecords (XXXX gives the next one a 32-bit size). problem = why the sizes
    /// don't add up to the body length, or null.</summary>
    public static List<(string Type, byte[] Data)> Parse(ReadOnlySpan<byte> body, out string? problem)
    {
        var list = new List<(string, byte[])>();
        problem = null;
        int i = 0;
        uint? big = null;
        while (i < body.Length)
        {
            if (body.Length - i < 6) { problem = $"{body.Length - i} stray bytes at the end"; break; }
            var type = Encoding.Latin1.GetString(body.Slice(i, 4));
            long size = BitConverter.ToUInt16(body.Slice(i + 4, 2));
            if (type == "XXXX")
            {
                if (size != 4 || body.Length - i < 10) { problem = $"XXXX field of size {size}"; break; }
                big = BitConverter.ToUInt32(body.Slice(i + 6, 4));
                i += 10;
                continue;
            }
            if (big != null) { size = big.Value; big = null; }
            if (i + 6 + size > body.Length) { problem = $"field {type} ({size} bytes) runs {i + 6 + size - body.Length} bytes past the record end"; break; }
            list.Add((type, body.Slice(i + 6, (int)size).ToArray()));
            i += 6 + (int)size;
        }
        if (problem == null && big != null) problem = "XXXX at the end with no field after it";
        return list;
    }

    public static byte[]? Decompress(ReadOnlySpan<byte> stored, out string? problem)
    {
        problem = null;
        if (stored.Length < 4) { problem = "compressed record shorter than its 4-byte size"; return null; }
        var want = BitConverter.ToUInt32(stored[..4]);
        if (want > 512 * 1024 * 1024) { problem = $"compressed record claims {want} bytes"; return null; }
        try
        {
            using var z = new ZLibStream(new MemoryStream(stored[4..].ToArray()), CompressionMode.Decompress);
            var outBuf = new byte[want];
            int got = 0, n;
            while (got < want && (n = z.Read(outBuf, got, (int)want - got)) > 0) got += n;
            var extra = z.Read(new byte[1], 0, 1);
            if (got != want || extra != 0) { problem = $"decompresses to {(extra != 0 ? "more than" : got.ToString())} bytes, header says {want}"; return null; }
            return outBuf;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            problem = "zlib data does not decode (" + e.Message + ")";
            return null;
        }
    }
}

/// <summary>Turns a FormID stored in one plugin into (file, local ID). Starfield keeps a master list per style: full
/// IDs use the top byte as an index into the full masters, medium IDs are FD + 8-bit index + 16 bits, light IDs are
/// FE + 12-bit index + 12 bits. Index == list length means the plugin itself. (Same rules as
/// tools\mutagen-gap-check.py IdMap.)</summary>
public sealed class IdMap
{
    public readonly string Self;
    public readonly Style SelfStyle;
    readonly Dictionary<Style, List<string>> lists = new() { [Style.Full] = new(), [Style.Medium] = new(), [Style.Light] = new() };

    public IdMap(string self, Style selfStyle, IEnumerable<(string Name, Style Style)> masters)
    {
        Self = self;
        SelfStyle = selfStyle;
        foreach (var (n, s) in masters) lists[s].Add(n);
    }

    public IReadOnlyList<string> List(Style s) => lists[s];

    /// <summary>File (null = index past the end of its list, i.e. no such master), local ID, the style the ID is
    /// written in.</summary>
    public (string? File, uint Local, Style Written) Decode(uint fid)
    {
        var top = fid >> 24;
        Style s; int idx; uint local;
        if (top == 0xFE) { s = Style.Light; idx = (int)((fid >> 12) & 0xFFF); local = fid & 0xFFF; }
        else if (top == 0xFD) { s = Style.Medium; idx = (int)((fid >> 16) & 0xFF); local = fid & 0xFFFF; }
        else { s = Style.Full; idx = (int)top; local = fid & 0xFFFFFF; }
        var l = lists[s];
        if (idx < l.Count) return (l[idx], local, s);
        if (idx == l.Count) return (Self, local, s);
        return (null, local, s);
    }

    /// <summary>Local IDs as Mutagen's FormKey has them: light files keep 12 bits, medium 16, full 24.</summary>
    public static uint Mask(uint local, Style fileStyle) => fileStyle switch
    {
        Style.Light => local & 0xFFF,
        Style.Medium => local & 0xFFFF,
        _ => local & 0xFFFFFF,
    };
}

public readonly record struct RawRecord(string Sig, uint Flags, uint FormId, long Offset, uint Size, ushort FormVersion);

/// <summary>Walks every group and record of a file. In deep mode it also decompresses each record and splits it into
/// fields, reporting any layout problem through the problem callback (structural pass). Without deep mode it only
/// reads record headers (fast index of a big master).</summary>
public sealed class RawWalker
{
    readonly RawFile f;
    readonly bool deep;
    readonly Action<string, long, string> problem;   // (severity, offset, text)
    public Action<RawRecord>? OnRecord;
    /// <summary>Whether two stored FormIDs mean the same record (a light plugin may write its own IDs FE-style in the
    /// record but full-style in the group label). The checker sets it from the plugin's IdMap.</summary>
    public Func<uint, uint, bool> SameId = (a, b) => a == b;
    public Action<RawRecord, List<(string Type, byte[] Data)>>? OnFields;
    public readonly Dictionary<string, int> Census = new();
    public int Groups, Records;
    readonly HashSet<string> topLabels = new();

    public RawWalker(RawFile file, bool deep, Action<string, long, string> problem)
    {
        f = file;
        this.deep = deep;
        this.problem = problem;
    }

    static bool IsSig(string s) => s.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    sealed record Ctx(int Type, uint Label, string LabelSig, Ctx? Parent);

    public void Walk()
    {
        if (f.Length < 24 || f.Sig(0) != "TES4") { problem("ERROR", 0, "the file does not start with a TES4 header"); return; }
        var hsize = f.U32(4);
        if (24 + (long)hsize > f.Length) { problem("ERROR", 0, $"the TES4 header ({hsize} bytes) runs past the end of the file"); return; }
        if (deep)
        {
            Subrecords.Parse(f.Span(24, (int)hsize), out var why);
            if (why != null) problem("ERROR", 0, "TES4 header: " + why);
        }
        Container(24 + hsize, f.Length, null);
    }

    string Where(Ctx? c) => c == null ? "top level" : c.Type == 0 ? $"GRUP {c.LabelSig}" : $"GRUP type {c.Type} {c.Label:X8} (in {Where(c.Parent)})";

    void Container(long start, long end, Ctx? ctx)
    {
        long off = start;
        RawRecord? prev = null;
        while (off < end)
        {
            if (end - off < 24) { problem("ERROR", off, $"{end - off} stray bytes at the end of {Where(ctx)}"); return; }
            var sig = f.Sig(off);
            var size = f.U32(off + 4);
            if (sig == "GRUP")
            {
                Groups++;
                if (size < 24 || off + size > end) { problem("ERROR", off, $"group size {size} does not fit in {Where(ctx)} ({end - off} bytes left)"); return; }
                var label = f.U32(off + 8);
                var type = f.I32(off + 12);
                var labelSig = f.Sig(off + 8);
                var c = new Ctx(type, label, labelSig, ctx);
                Census[$"group type {type} in {(ctx == null ? "top" : "type " + ctx.Type)}"] = Census.GetValueOrDefault($"group type {type} in {(ctx == null ? "top" : "type " + ctx.Type)}") + 1;
                CheckGroup(off, c, prev);
                Container(off + 24, off + size, c);
                prev = null;
                off += size;
                continue;
            }
            if (!IsSig(sig)) { problem("ERROR", off, $"'{Printable(sig)}' is not a record type (in {Where(ctx)}); the rest of this group can't be read"); return; }
            long recEnd = off + 24 + size;
            if (recEnd > end) { problem("ERROR", off, $"{sig} record size {size} runs {recEnd - end} bytes past the end of {Where(ctx)}"); return; }
            var rec = new RawRecord(sig, f.U32(off + 8), f.U32(off + 12), off, size, f.U16(off + 20));
            Records++;
            var key = $"{sig} in {(ctx == null ? "top" : ctx.Type == 0 ? "type 0" : "type " + ctx.Type)}";
            Census[key] = Census.GetValueOrDefault(key) + 1;
            CheckPlacement(rec, ctx);
            if (rec.FormId == 0) problem("ERROR", off, $"{sig} record with FormID 00000000");
            OnRecord?.Invoke(rec);
            if (deep) Fields(rec);
            prev = rec;
            off = recEnd;
        }
    }

    static string Printable(string s) => new(s.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    // Starfield's group layout, as found in all official masters (census 2026-11-07): group type -> (where it may
    // sit, the record it must follow, records it may hold directly). Parent "top" = top-level type 0 group with that label.
    static readonly Dictionary<int, (string[] Parents, string? Follows, string[] Holds)> Layout = new()
    {
        [1] = (["top WRLD"], "WRLD", ["CELL"]),          // world children: the persistent cell + blocks
        [2] = (["top CELL"], null, []),                    // interior cell block
        [3] = (["2"], null, ["CELL"]),                     // interior cell sub-block
        [4] = (["1"], null, []),                           // exterior cell block
        [5] = (["4"], null, ["CELL"]),                     // exterior cell sub-block
        [6] = (["1", "3", "5"], "CELL", []),               // cell children
        [7] = (["10"], "DIAL", ["INFO"]),                  // topic children
        [8] = (["6"], null, []),                           // persistent refs
        [9] = (["6"], null, []),                           // temporary refs
        [10] = (["top QUST"], "QUST", ["DIAL", "DLBR", "SCEN"]),   // quest children
    };

    void CheckGroup(long off, Ctx c, RawRecord? prev)
    {
        var p = c.Parent;
        if (c.Type == 0)
        {
            if (p != null) problem("ERROR", off, $"top-level group {Printable(c.LabelSig)} is nested inside {Where(p)}");
            else if (!IsSig(c.LabelSig)) problem("ERROR", off, $"top-level group label '{Printable(c.LabelSig)}' is not a record type");
            else if (!topLabels.Add(c.LabelSig)) problem("WARNING", off, $"second top-level {c.LabelSig} group");
            return;
        }
        if (!Layout.TryGetValue(c.Type, out var rule)) { problem("ERROR", off, $"unknown group type {c.Type} (label {c.Label:X8}) in {Where(p)}"); return; }
        var at = p == null ? "none" : p.Type == 0 ? "top " + p.LabelSig : p.Type.ToString();
        if (!rule.Parents.Contains(at)) problem("ERROR", off, $"group type {c.Type} {c.Label:X8} is in {Where(p)}");
        if (rule.Follows != null && (prev == null || prev.Value.Sig != rule.Follows || !SameId(prev.Value.FormId, c.Label)))
            problem("ERROR", off, $"{rule.Follows} children group {c.Label:X8} does not follow its {rule.Follows} record"
                + (prev == null ? "" : $" (follows {prev.Value.Sig} {prev.Value.FormId:X8})"));
        if (c.Type is 8 or 9 && p != null && !SameId(p.Label, c.Label))
            problem("ERROR", off, $"cell refs group {c.Label:X8} is inside cell children group {p.Label:X8}");
    }

    // Records directly inside a group of this type: an ERROR when the group can't hold records, a WARNING for record
    // types the official masters never put there.
    static readonly HashSet<string> RefTypes = ["REFR", "ACHR", "NAVM", "PGRE", "PHZD", "PMIS", "PARW", "PBAR", "PBEA", "PCON", "PFLA"];

    void CheckPlacement(RawRecord rec, Ctx? ctx)
    {
        if (ctx == null) { problem("ERROR", rec.Offset, $"{rec.Sig} {rec.FormId:X8} is outside any group"); return; }
        if (ctx.Type == 0)
        {
            if (rec.Sig != ctx.LabelSig) problem("ERROR", rec.Offset, $"{rec.Sig} {rec.FormId:X8} is in the {ctx.LabelSig} group");
            return;
        }
        if (ctx.Type is 8 or 9)
        {
            if (!RefTypes.Contains(rec.Sig)) problem("WARNING", rec.Offset, $"{rec.Sig} {rec.FormId:X8} in a cell refs group (official masters only have placed objects there)");
            return;
        }
        var holds = Layout.TryGetValue(ctx.Type, out var r) ? r.Holds : [];
        if (holds.Length == 0) problem("ERROR", rec.Offset, $"{rec.Sig} {rec.FormId:X8} directly in {Where(ctx)}, which holds only groups");
        else if (!holds.Contains(rec.Sig)) problem("ERROR", rec.Offset, $"{rec.Sig} {rec.FormId:X8} in {Where(ctx)} (holds {string.Join("/", holds)})");
    }

    void Fields(RawRecord rec)
    {
        if (rec.Size > int.MaxValue) { problem("ERROR", rec.Offset, $"{rec.Sig} {rec.FormId:X8} is too big ({rec.Size} bytes)"); return; }
        var stored = f.Span(rec.Offset + 24, (int)rec.Size);
        ReadOnlySpan<byte> body = stored;
        if ((rec.Flags & Flags.Compressed) != 0)
        {
            var d = Subrecords.Decompress(stored, out var why);
            if (d == null) { problem("ERROR", rec.Offset, $"{rec.Sig} {rec.FormId:X8} is compressed but {why}"); return; }
            body = d;
        }
        var fields = Subrecords.Parse(body, out var bad);
        if (bad != null) problem("ERROR", rec.Offset, $"{rec.Sig} {rec.FormId:X8}: {bad}");
        OnFields?.Invoke(rec, fields);
    }
}
