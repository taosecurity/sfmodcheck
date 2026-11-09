// Lists learned from the official masters on the user's own PC (0.4.0 step 3, 2026-11-09). The public build doesn't
// ship data\scene-alias-quirks.txt (it names official FormIDs and EditorIDs), so the first check that meets an official
// scene runs the --learn-scene-quirks pass on the official masters in the Data folder and caches the result in
// %LOCALAPPDATA%\sfmodcheck\scene-quirks. The cache is keyed by each official master's name, size and modified time
// (plus a rule version): any change, such as a game update, means the list is learned again.
using System.Security.Cryptography;
using System.Text;

namespace SfModCheck;

public static class LearnedCache
{
    /// <summary>Raise when the scenes rule changes in a way that changes which official scenes it flags.</summary>
    public const int SceneRuleVersion = 1;

    public static string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sfmodcheck");
    public static string SceneDir => Path.Combine(Root, "scene-quirks");

    /// <summary>The scene quirks for the official masters in one Data folder: the quirk FormKeys and the masters
    /// the list covers.</summary>
    public sealed record SceneList(HashSet<string> Quirks, HashSet<string> Masters, string Path, bool Learned);

    static readonly Dictionary<string, SceneList?> loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the Data folder holds no official masters.</summary>
    public static SceneList? Scenes(string dataDir)
    {
        var full = System.IO.Path.GetFullPath(dataDir);
        if (loaded.TryGetValue(full, out var have)) return have;
        var masters = Directory.Exists(full) ? Checker.OfficialMasters(full) : new List<string>();
        if (masters.Count == 0) return loaded[full] = null;

        var key = new List<string> { $"# key: scenes rule {SceneRuleVersion}, Mutagen 0.54.4" };
        foreach (var m in masters)
        {
            var fi = new FileInfo(System.IO.Path.Combine(full, m));
            key.Add($"# master: {fi.Name} {fi.Length} {fi.LastWriteTimeUtc.Ticks}");
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", key).ToLowerInvariant())))[..12].ToLowerInvariant();
        var file = System.IO.Path.Combine(SceneDir, $"scene-alias-quirks.{hash}.txt");
        var set = new HashSet<string>(masters, StringComparer.OrdinalIgnoreCase);

        if (File.Exists(file))
        {
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            var keyLines = lines.Where(l => l.StartsWith("# key: ") || l.StartsWith("# master: "));
            if (keyLines.SequenceEqual(key, StringComparer.OrdinalIgnoreCase))
            {
                var list = new SceneList(Entries(lines), set, file, false);
                Log.Write($"scene-quirks: read {file} ({list.Quirks.Count} official scenes skipped; covers {masters.Count} masters)");
                return loaded[full] = list;
            }
            Log.Write($"scene-quirks: {file} is for other master sizes or dates (a game update?), so the list is learned again");
        }
        else Log.Write($"scene-quirks: no cache for these masters ({file}), so the list is learned now");

        Console.Error.WriteLine($"sfmodcheck: learning the official scene quirks from {masters.Count} masters in {full} (once; cached in {SceneDir})");
        var learned = Rules.LearnSceneQuirkLines(full);
        var text = new List<string>(Rules.SceneQuirkHeader) { $"# Learned from {full} on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC." };
        text.AddRange(key);
        text.AddRange(learned);
        try
        {
            Directory.CreateDirectory(SceneDir);
            foreach (var old in Directory.GetFiles(SceneDir, "scene-alias-quirks.*.txt")) File.Delete(old);   // other game versions
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, string.Join("\n", text) + "\n", new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
            Log.Write($"scene-quirks: learned {learned.Count(l => l.Length > 0 && !l.StartsWith('#'))} official scenes, wrote {file}");
        }
        // a cache we can't write only costs time next run
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"scene-quirks: learned the list but couldn't write {file}: {e.Message}");
        }
        return loaded[full] = new SceneList(Entries(learned), set, file, true);
    }

    static HashSet<string> Entries(IEnumerable<string> lines) =>
        new(lines.Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(' ')[0]), StringComparer.OrdinalIgnoreCase);
}
