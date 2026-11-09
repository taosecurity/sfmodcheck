// EditorID rules (0.3.0, 2026-11-08; ideas from SFSearch's Check): scripts (GetFormFromEditorID-style lookups), the
// console and tools find records by EditorID, so a name used twice finds only one of them.
//   - two or more new records in the plugin share an EditorID (case-insensitive)
//   - a new record reuses an EditorID that one of its masters (any depth) already has
// The masters' EditorIDs come from a raw read (record headers + the EDID field, decompressing only when needed) and are
// cached per file in %LOCALAPPDATA%\sfmodcheck\edid-cache, keyed by file name, size and modified time
// (size and time together: a mod manager can install an update with an older file date than the copy it replaces).
using System.Text;

namespace SfModCheck;

public static class EditorIds
{
    public static string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sfmodcheck", "edid-cache");

    static readonly Dictionary<string, HashSet<string>> loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every EditorID in one plugin file (lower case), from the cache when the file is unchanged.</summary>
    public static HashSet<string> Of(string path)
    {
        var fi = new FileInfo(path);
        var key = $"{fi.Name}.{fi.Length}.{fi.LastWriteTimeUtc.Ticks}";
        if (loaded.TryGetValue(key, out var have)) return have;
        var cache = Path.Combine(CacheDir, key + ".txt");
        HashSet<string> set;
        if (File.Exists(cache))
        {
            set = new HashSet<string>(File.ReadLines(cache, Encoding.UTF8));
            Log.Write($"edid-cache {fi.Name}: read {cache} ({set.Count} EditorIDs)");
        }
        else
        {
            set = Read(path);
            try
            {
                Directory.CreateDirectory(CacheDir);
                foreach (var old in Directory.GetFiles(CacheDir, fi.Name + ".*.txt")) File.Delete(old);  // other copies of this file
                var tmp = cache + ".tmp";
                File.WriteAllLines(tmp, set, new UTF8Encoding(false));
                File.Move(tmp, cache, overwrite: true);
                Log.Write($"edid-cache {fi.Name}: none for this size and date, built {cache} ({set.Count} EditorIDs)");
            }
            // a cache we can't write only costs time
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write($"edid-cache {fi.Name}: read the plugin ({set.Count} EditorIDs) but couldn't write {cache}: {e.Message}");
            }
        }
        loaded[key] = set;
        return set;
    }

    /// <summary>Raw read: walks every record (groups are entered in place) and takes its EDID field.</summary>
    static HashSet<string> Read(string path)
    {
        var set = new HashSet<string>();
        using var f = new RawFile(path);
        if (f.Length < 24 || f.Sig(0) != "TES4") return set;
        long off = 24 + f.U32(4);
        while (off + 24 <= f.Length)
        {
            var sig = f.Sig(off);
            var size = f.U32(off + 4);
            if (sig == "GRUP") { off += 24; continue; }   // a group's records follow its header directly
            if (off + 24 + size > f.Length) break;
            var flags = f.U32(off + 8);
            if (size > 0)
            {
                var body = f.Span(off + 24, (int)size);
                byte[]? un = null;
                if ((flags & Flags.Compressed) != 0) un = Subrecords.Decompress(body, out _);
                var e = un != null ? FirstEdid(un) : FirstEdid(body);
                if (e != null) set.Add(e);
            }
            off += 24 + size;
        }
        return set;
    }

    /// <summary>The EDID field's text (lower case), looking at the first few fields only (EDID comes first in practice).</summary>
    static string? FirstEdid(ReadOnlySpan<byte> body)
    {
        int i = 0, n = 0;
        uint? big = null;
        while (i + 6 <= body.Length && n++ < 6)
        {
            var type = Encoding.Latin1.GetString(body.Slice(i, 4));
            long size = BitConverter.ToUInt16(body.Slice(i + 4, 2));
            if (type == "XXXX") { if (i + 10 > body.Length) return null; big = BitConverter.ToUInt32(body.Slice(i + 6, 4)); i += 10; continue; }
            if (big != null) { size = big.Value; big = null; }
            if (i + 6 + size > body.Length) return null;
            if (type == "EDID")
            {
                var s = body.Slice(i + 6, (int)size);
                var z = s.IndexOf((byte)0);
                if (z >= 0) s = s[..z];
                return s.Length == 0 ? null : Encoding.Latin1.GetString(s).ToLowerInvariant();
            }
            i += 6 + (int)size;
        }
        return null;
    }

    /// <summary>Runs both rules. own = the plugin's new records as (EditorID, display name); masterPaths = every master
    /// file the plugin needs, any depth.</summary>
    public static void Check(IEnumerable<(string EditorId, string Name)> own, IEnumerable<string> masterPaths, Action<string, string, string> add)
    {
        var mine = own.Where(x => !string.IsNullOrEmpty(x.EditorId)).ToList();
        foreach (var g in mine.GroupBy(x => x.EditorId.ToLowerInvariant()).Where(g => g.Count() > 1).OrderBy(g => g.Key))
            add("WARNING", "editorids", $"EditorID {g.First().EditorId} is used by {g.Count()} new records: {string.Join(", ", g.Select(x => x.Name))} "
                                        + "(a lookup by EditorID finds only one)");
        if (mine.Count == 0) return;
        var byMaster = masterPaths.Select(p => (Name: Path.GetFileName(p), Ids: Of(p))).ToList();
        foreach (var (edid, name) in mine)
        {
            var lower = edid.ToLowerInvariant();
            var hit = byMaster.FirstOrDefault(m => m.Ids.Contains(lower));
            if (hit.Ids != null)
                add("WARNING", "editorids", $"{name}: new record with EditorID {edid}, which {hit.Name} already uses for another record "
                                            + "(the console and EditorID lookups may find the master's)");
        }
    }
}
