// The checks. Read-only: nothing is written anywhere.
//   structure  raw pass over every group, record and field (sizes, group layout, compressed records decode)
//   header     HEDR, Master/Light/Medium flags, masters present and in a valid order, record count, NextFormID
//   ids        each record's FormID: master index inside the lists, own IDs inside the plugin's range, no duplicates
//   read       Mutagen can read every record (and list its links)
//   links      every link points to an existing record of a type the field allows (medium/light aware), not to a
//              deleted record; every override overrides a record that exists, with the same type
using System.Diagnostics;
using System.Reflection;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Records.Mapping;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

public sealed record Finding(string Severity, string Check, string Text);

public sealed class Options
{
    public required string Plugin;
    public string DataDir = @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
    /// <summary>Searched for masters before the Data folder (by default the plugin's own folder).</summary>
    public List<string> MastersDirs = new();
    /// <summary>Most lines printed per check (the counts in RESULT are always complete).</summary>
    public int MaxPerCheck = 100;
    /// <summary>Let the private build's built-in scene-quirk list fill in when Data lacks an official master (SelfTest
    /// turns it off to act like the public build).</summary>
    public bool UseBuiltInSceneQuirks = true;
}

public sealed class Result
{
    public readonly List<Finding> Findings = new();
    public int Records, Groups, Links, Overrides, Itms;
    public TimeSpan Elapsed;
    public bool CannotCheckLinks;
    /// <summary>Official scenes in the plugin the scenes rule skipped because no quirk list covers their master
    /// (the master isn't in the Data folder), and why.</summary>
    public int ScenesNotChecked;
    public string ScenesNotCheckedWhy = "";
    public string Summary = "";
    public int Errors => Findings.Count(f => f.Severity == "ERROR");
    public int Warnings => Findings.Count(f => f.Severity == "WARNING");
    public bool Has(string severity, string check) => Findings.Any(f => f.Severity == severity && f.Check == check);
}

public static class Checker
{
    /// <summary>Records the engine makes at run time (data\engine-forms.txt, from xEdit's hardcoded list): they count
    /// as Starfield.esm records, so links to them are found and type-checked.</summary>
    static readonly Lazy<Dictionary<(string, uint), string>> EngineForms = new(() =>
    {
        using var s = typeof(Checker).Assembly.GetManifestResourceStream("engine-forms.txt")!;
        using var rd = new StreamReader(s);
        var d = new Dictionary<(string, uint), string>();
        while (rd.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || line.Length < 11) continue;
            var parts = line.Split(' ');
            d[("starfield.esm", Convert.ToUInt32(parts[0], 16))] = parts[1];
        }
        return d;
    });

    public static Result Run(Options o)
    {
        var sw = Stopwatch.StartNew();
        var r = new Result();
        void Add(string sev, string check, string text) => r.Findings.Add(new(sev, check, text));
        var self = Path.GetFileName(o.Plugin);
        var dirs = o.MastersDirs.Count > 0 ? o.MastersDirs : [Path.GetDirectoryName(Path.GetFullPath(o.Plugin))!];
        using var game = new GameData(o.DataDir, dirs);

        var h = RawHeader.Read(o.Plugin);
        if (h == null) { Log.Write("plugin header: none (the file doesn't start with TES4)"); Add("ERROR", "structure", "not a plugin: the file does not start with a TES4 header"); return Done(); }
        Log.Write($"plugin header: {h.Style.ToString().ToLower()}{((h.Flags & Flags.Master) != 0 ? " master" : "")}, flags {h.Flags:X8}, " +
                  $"{h.Masters.Count} masters{(h.Masters.Count > 0 ? ": " + string.Join(", ", h.Masters) : "")}");

        // ---- header ----
        if (h.Fields.All(f => f.Type != "HEDR")) Add("ERROR", "header", "no HEDR field");
        else if (h.Fields.First(f => f.Type == "HEDR").Size != 12) Add("ERROR", "header", $"HEDR is {h.Fields.First(f => f.Type == "HEDR").Size} bytes, not 12");
        if ((h.Flags & Flags.Light) != 0 && (h.Flags & Flags.Medium) != 0) Add("ERROR", "header", "both the Light and the Medium flag are set");
        if (self.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) && (h.Flags & Flags.Master) == 0) Add("WARNING", "header", ".esm file without the Master flag");

        // masters: present (with their own masters), no duplicates, each one after its own masters
        var styleOf = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        var headers = new Dictionary<string, RawHeader?>(StringComparer.OrdinalIgnoreCase);
        RawHeader? HeaderOf(string name)
        {
            if (headers.TryGetValue(name, out var hh)) return hh;
            var p = game.Locate(name);
            hh = p == null ? null : RawHeader.Read(p);
            headers[name] = hh;
            if (p == null) Log.Write($"master {name}: NOT FOUND (looked in {string.Join(", ", game.ExtraDirs.Append(game.DataDir))})");
            else Log.Write($"master {name}: {Log.Describe(p)}{(hh == null ? ", but it has no TES4 header" : $", {hh.Style.ToString().ToLower()}, {hh.Masters.Count} masters")}");
            if (hh != null) styleOf[name] = hh.Style;
            return hh;
        }
        var missing = new List<string>();
        var seenMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Need(string name, string? by)
        {
            var mh = HeaderOf(name);
            if (mh == null)
            {
                if (seenMissing.Add(name)) missing.Add(by == null ? $"master {name} is not installed (looked in {string.Join(", ", game.ExtraDirs.Append(game.DataDir))})" : $"{by} needs {name}, which is not installed");
                return;
            }
            foreach (var m in mh.Masters) Need(m, name);
        }
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in h.Masters)
        {
            if (!listed.Add(m)) Add("ERROR", "header", $"master {m} is listed twice");
            if (m.Equals(self, StringComparison.OrdinalIgnoreCase)) Add("ERROR", "header", "the plugin lists itself as a master");
            Need(m, null);
        }
        foreach (var m in missing) Add("ERROR", "header", m);
        for (int i = 0; i < h.Masters.Count; i++)
            foreach (var mm in HeaderOf(h.Masters[i])?.Masters ?? [])
            {
                var j = h.Masters.FindIndex(x => x.Equals(mm, StringComparison.OrdinalIgnoreCase));
                // WARNING, not ERROR: the game loads by plugins.txt, and working mods do this (2026-11-07 calibration).
                if (j > i) Add("WARNING", "header", $"master {h.Masters[i]} comes before its own master {mm}");
            }

        // ---- structure (raw) ----
        var ids = new IdMap(self, h.Style, h.Masters.Select(m => (m, styleOf.GetValueOrDefault(m, Style.Full))));
        var recs = new List<RawRecord>();
        var rf = new Rules.RawFindings();
        using (var f = new RawFile(o.Plugin))
        {
            var w = new RawWalker(f, deep: true, (sev, off, text) => Add(sev, "structure", $"at 0x{off:X}: {text}"))
            {
                SameId = (a, b) => a == b || ids.Decode(a) is var x && ids.Decode(b) is var y && x.File == y.File && x.File != null && x.Local == y.Local,
                OnRecord = recs.Add,
                OnFields = (rec, fields) => Rules.OnRawFields(rec, fields, ids, rf),
            };
            w.Walk();
            r.Records = w.Records;
            r.Groups = w.Groups;
        }
        Log.Write($"stage raw read: {r.Records} records, {r.Groups} groups ({sw.Elapsed.TotalSeconds:F1} s so far)");
        foreach (var t in rf.Emotion0) Add("WARNING", "dialogue", t);
        foreach (var t in rf.NaviUnreadable) Add("WARNING", "navi", t);

        // (HEDR's record count is not checked: official masters store records + 1, Mutagen and the Creation Kit
        // count groups too, in different ways, and the game doesn't rely on it.)

        // ---- ids (raw) ----
        var (first, last) = Plugin.IdRange(h.Style switch { Style.Light => PluginStyle.Light, Style.Medium => PluginStyle.Medium, _ => PluginStyle.Full });
        uint maxOwn = 0;
        bool anyOwn = false;
        var seen = new Dictionary<(string, uint), RawRecord>();
        var deleted = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var rec in recs)
        {
            var (file, local, written) = ids.Decode(rec.FormId);
            var what = $"{rec.Sig} {rec.FormId:X8}";
            if (file == null) { Add("ERROR", "ids", $"{what}: master index past the end of the {written.ToString().ToLower()} master list ({ids.List(written).Count} {written.ToString().ToLower()} masters)"); continue; }
            // 0.3.0 (idea from SFSearch's Check): deleted records, summed up per record type below.
            if ((rec.Flags & Flags.Deleted) != 0)
            {
                if (!deleted.TryGetValue(rec.Sig, out var dl)) deleted[rec.Sig] = dl = new();
                dl.Add($"{local:X6}:{file}");
            }
            if (!seen.TryAdd((file.ToLowerInvariant(), local), rec)) Add("ERROR", "ids", $"{what}: the same record as {seen[(file.ToLowerInvariant(), local)].Sig} {seen[(file.ToLowerInvariant(), local)].FormId:X8} (duplicate FormID)");
            if (file != self) continue;
            anyOwn = true;
            maxOwn = Math.Max(maxOwn, local);
            if (written != Style.Full && written != h.Style) Add("WARNING", "ids", $"{what}: own record written {written.ToString().ToLower()}-style in a {h.Style.ToString().ToLower()} plugin");
            // (Own IDs below 0x800 are not flagged: Creation Kit light plugins use them and work in game.)
            if (local > last) Add("ERROR", "ids", $"{what}: own FormID {local:X6} is outside the {h.Style.ToString().ToLower()} range 000-{last:X}");
        }
        // Deleted records: one WARNING per record type. Calibration 2026-11-08: Bethesda's own masters delete records
        // routinely (SFBGS050 5,721 placed references, sfta01 169 navmeshes, SFBGS003 129 navmeshes), so it isn't an
        // error by itself; the risk is another mod still using the deleted record (the game can crash on a deleted
        // placed reference or navmesh). xEdit's "Undelete and Disable" is the safe way for placed references.
        foreach (var (sig, list) in deleted)
            Add("WARNING", "records", $"{list.Count} deleted {sig} record(s), e.g. {string.Join(", ", list.Take(3))}"
                + (sig is "REFR" or "ACHR" ? "; a mod that still uses one can crash the game (prefer Undelete and Disable)"
                   : sig == "NAVM" ? "; the Creation Kit does this when it replaces navmeshes; make sure no other mod uses them" : ""));

        // NextFormID: the Creation Kit often leaves the placeholder 800 (or 0), so only other values are compared.
        var next = (h.NextObjectId ?? 0) & 0xFFFFFF;
        if (anyOwn && next is not (0 or 0x800) && next <= maxOwn)
            Add("WARNING", "header", $"HEDR next FormID {next:X6} is not above the highest own FormID {maxOwn:X6}");

        // ---- read + links (Mutagen) ----
        if (missing.Count > 0) { Log.Write($"stopped before the Mutagen stages: {missing.Count} master(s) missing; install them, or point --data / --masters-dir at them"); r.CannotCheckLinks = true; return Done(); }
        try { foreach (var m in h.Masters) game.Open(m); }
        catch (Exception e) { Log.Write($"Mutagen can't open the masters: {e}"); Add("ERROR", "read", $"Mutagen can't open the masters: {e.GetType().Name}: {e.Message}"); r.CannotCheckLinks = true; return Done(); }
        Log.Write($"stage masters opened with Mutagen: load order {string.Join(", ", game.LoadOrder.Select(m => m.ModKey.FileName.String))} ({sw.Elapsed.TotalSeconds:F1} s so far)");
        // NAVI NVMI navmesh IDs (read raw above), decoded with the plugin's own master lists
        var navKeys = new List<(string Rec, string What, uint Fid, (string, uint)? Key)>();
        foreach (var (rec, what, fid) in rf.NavLinks)
        {
            var (file, local, _) = ids.Decode(fid);
            var st = file == null ? Style.Full : file.Equals(self, StringComparison.OrdinalIgnoreCase) ? h.Style : styleOf.GetValueOrDefault(file, Style.Full);
            navKeys.Add((rec, what, fid, file == null ? null : (file.ToLowerInvariant(), IdMap.Mask(local, st))));
        }
        LinkPass(o, game, self, styleOf, r, Add, navKeys, sw);
        return Done();

        Result Done()
        {
            r.Elapsed = sw.Elapsed;
            Log.Write($"stage done: {sw.Elapsed.TotalSeconds:F1} s");
            r.Summary = $"{h?.Style.ToString().ToLower() ?? "?"}{((h?.Flags ?? 0) & Flags.Master) switch { 0 => "", _ => " master" }}, {h?.Masters.Count ?? 0} masters, {r.Records} records, {r.Overrides} overrides ({r.Itms} ITM), {r.Links} links";
            return r;
        }
    }

    sealed class Target
    {
        public string Sig = "", DefinedIn = "";
        public bool Deleted;
    }

    static void LinkPass(Options o, GameData game, string self, Dictionary<string, Style> styleOf, Result r, Action<string, string, string> add,
        List<(string Rec, string What, uint Fid, (string, uint)? Key)> navKeys, Stopwatch sw)
    {
        IModGetter mod;
        try { mod = game.OpenPlugin(o.Plugin); }
        catch (Exception e) { Log.Write($"Mutagen can't open the plugin: {e}"); add("ERROR", "read", $"Mutagen can't open the plugin: {e.GetType().Name}: {e.Message}"); r.CannotCheckLinks = true; return; }
        void Stage(string what) => Log.Write($"stage {what} ({sw.Elapsed.TotalSeconds:F1} s so far)");
        using var _ = mod as IDisposable;
        var selfKey = mod.ModKey;
        static (string, uint) K(FormKey fk) => (fk.ModKey.FileName.String.ToLowerInvariant(), fk.ID);

        // 1. every record and its links, as Mutagen reads them
        var links = new List<(IMajorRecordGetter From, IFormLinkGetter Link)>();
        var overrides = new List<IMajorRecordGetter>();
        var targets = new HashSet<(string, uint)>();
        // A container record (quest, cell, worldspace...) also lists its child records' links, and the children are
        // enumerated on their own too. Report each link once, on the innermost record: subtract the children's links
        // from the container's (multiset, keyed by target and type). (2026-11-07: a test mod's links were counted twice, 460 = 2 x 230.)
        var perRecord = new List<(IMajorRecordGetter Rec, List<IFormLinkGetter> Links)>();
        var byKey = new Dictionary<FormKey, List<IFormLinkGetter>>();
        try
        {
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                if (rec.FormKey.ModKey != selfKey) { overrides.Add(rec); targets.Add(K(rec.FormKey)); }
                try
                {
                    var ls = rec.EnumerateFormLinks().Where(l => !l.IsNull).ToList();
                    perRecord.Add((rec, ls));
                    byKey.TryAdd(rec.FormKey, ls);
                }
                catch (Exception e) { add("ERROR", "read", $"{Name(rec)}: Mutagen can't read this record ({e.GetType().Name}: {e.Message})"); }
            }
            foreach (var (rec, ls) in perRecord)
            {
                var own = ls;
                if (rec is IMajorRecordGetterEnumerable container)
                {
                    var inner = new Dictionary<(FormKey, Type), int>();
                    foreach (var child in container.EnumerateMajorRecords())
                        if (child.FormKey != rec.FormKey && byKey.TryGetValue(child.FormKey, out var cl))
                            foreach (var l in cl) inner[(l.FormKey, l.Type)] = inner.GetValueOrDefault((l.FormKey, l.Type)) + 1;
                    if (inner.Count > 0)
                    {
                        own = new List<IFormLinkGetter>();
                        foreach (var l in ls)
                        {
                            var k = (l.FormKey, l.Type);
                            if (inner.TryGetValue(k, out var n) && n > 0) inner[k] = n - 1;
                            else own.Add(l);
                        }
                    }
                }
                foreach (var l in own) { links.Add((rec, l)); targets.Add(K(l.FormKey)); }
            }
        }
        catch (Exception e) { add("ERROR", "read", $"Mutagen stopped reading the plugin: {e.GetType().Name}: {e.Message}"); }
        r.Links = links.Count;
        r.Overrides = overrides.Count;
        Stage($"Mutagen read: {perRecord.Count} records, {overrides.Count} overrides, {links.Count} links");

        // 1b. content rules (0.2.0): conditions with an empty form parameter, scene alias numbers
        var ownQuests = new Dictionary<FormKey, IQuestGetter>();
        foreach (var (rec, _) in perRecord) if (rec is IQuestGetter q && !Rules.IsPartial(q)) ownQuests.TryAdd(q.FormKey, q);   // a partial override holds no aliases
        IQuestGetter? QuestOf(FormKey fk) =>
            ownQuests.TryGetValue(fk, out var q) ? q : Rules.FullQuest(game.LinkCache, fk);
        // Official scenes (overrides, or an official master checked itself): the ones that break the rule on purpose
        // are skipped. Their list is learned from Data's official masters and cached (LearnedCache); the private
        // build's built-in list only fills in when Data lacks the master. With neither, the scene isn't checked and
        // the RESULT line says so. New scenes never need the list.
        LearnedCache.SceneList? learned = null;
        bool asked = false;
        string learnFailed = "";
        var notChecked = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool SkipOfficialScene(ISceneGetter s)
        {
            var m = s.FormKey.ModKey.FileName.String;
            if (!IsOfficial(m)) return false;
            if (!asked)
            {
                asked = true;
                try { learned = LearnedCache.Scenes(o.DataDir); }
                catch (Exception e) { learnFailed = $"{e.GetType().Name}: {e.Message}"; Log.Write($"scene-quirks: couldn't learn the official scene list: {e}"); }
                if (learned == null && learnFailed == "") Log.Write($"scene-quirks: no official masters in {o.DataDir}, so no learned list" +
                    (o.UseBuiltInSceneQuirks && Rules.HaveBuiltInSceneQuirks ? " (the built-in list fills in)" : "; official scenes are not checked"));
            }
            if (learned != null && learned.Masters.Contains(m)) return learned.Quirks.Contains(s.FormKey.ToString());
            if (o.UseBuiltInSceneQuirks && Rules.HaveBuiltInSceneQuirks) return Rules.IsBuiltInSceneQuirk(s.FormKey);
            notChecked[m] = notChecked.GetValueOrDefault(m) + 1;
            return true;
        }
        foreach (var (rec, _) in perRecord)
        {
            try
            {
                Rules.CheckConditions(rec, Name(rec), add);
                if (rec is ISceneGetter s && !SkipOfficialScene(s)) Rules.CheckScene(s, Name(rec), QuestOf, add);
            }
            // (the name is built without the EditorID here: on a damaged record reading it is what throws)
            catch (Exception e) { add("ERROR", "read", $"[{SigOf(rec) ?? rec.GetType().Name}:{rec.FormKey}]: Mutagen can't read this record's conditions, scene data or EditorID ({e.GetType().Name}: {e.Message})"); }
        }
        if (notChecked.Count > 0)
        {
            r.ScenesNotChecked = notChecked.Values.Sum();
            var names = string.Join(", ", notChecked.Keys.Take(3)) + (notChecked.Count > 3 ? $" and {notChecked.Count - 3} more" : "");
            r.ScenesNotCheckedWhy = learnFailed != "" ? $"the official scene list couldn't be learned: {learnFailed}"
                : $"{names} not in the Data folder";
        }
        Stage("conditions, dialogue and scenes");
        // 1c. EditorIDs (0.3.0): repeated in the plugin, or reusing a master's (masters at any depth)
        try
        {
            var ownNames = new List<(string, string)>();
            foreach (var (rec, _) in perRecord)
                if (rec.FormKey.ModKey == selfKey && rec.EditorID is { Length: > 0 } ed) ownNames.Add((ed, Name(rec)));
            var masterPaths = game.LoadOrder.Select(m => game.Locate(m.ModKey.FileName)).Where(p => p != null).Cast<string>().ToList();
            EditorIds.Check(ownNames, masterPaths, add);
        }
        catch (Exception e) { Log.Write($"editorids: {e}"); add("WARNING", "editorids", $"EditorIDs not checked: {e.GetType().Name}: {e.Message}"); }
        Stage("editorids");

        foreach (var n in navKeys) if (n.Key is { } nk) targets.Add(nk);

        // 2. which of those records exist, raw headers only: masters in load order, then the plugin
        var found = new Dictionary<(string, uint), Target>();
        foreach (var (key, sig) in EngineForms.Value)
            if (targets.Contains(key)) found[key] = new Target { Sig = sig, DefinedIn = "the engine" };
        var files = game.LoadOrder.Select(m => (Name: m.ModKey.FileName.String, Path: game.Locate(m.ModKey.FileName)!)).ToList();
        files.Add((self, o.Plugin));
        foreach (var (name, path) in files)
        {
            var hh = RawHeader.Read(path)!;
            styleOf[name] = hh.Style;
            var map = new IdMap(name, hh.Style, hh.Masters.Select(m => (m, styleOf.GetValueOrDefault(m, Style.Full))));
            using var f = new RawFile(path);
            var w = new RawWalker(f, deep: false, (_, _, _) => { })
            {
                OnRecord = rec =>
                {
                    var (file, local, _) = map.Decode(rec.FormId);
                    if (file == null) return;
                    var key = (file.ToLowerInvariant(), IdMap.Mask(local, styleOf.GetValueOrDefault(file, Style.Full)));
                    if (!targets.Contains(key)) return;
                    if (!found.TryGetValue(key, out var t)) found[key] = t = new Target { Sig = rec.Sig, DefinedIn = name };
                    t.Deleted = (rec.Flags & Flags.Deleted) != 0;
                },
            };
            w.Walk();
        }
        Stage($"link targets looked up: {targets.Count} wanted, {found.Count} found in {files.Count} files and the engine list");

        // 3. links
        var notTyped = new Dictionary<string, int>();
        foreach (var (from, l) in links)
        {
            var key = K(l.FormKey);
            if (!found.TryGetValue(key, out var t))
            {
                // FFFFFFFF in the file means "none"; low IDs vanilla also "links" to are values, not links.
                if (l.FormKey.ID == 0xFFFFFF || (l.FormKey.ID < 0x800 && SeenInMasters(l.Type, "~" + l.FormKey.ID.ToString("X6")))) continue;
                add("ERROR", "links", $"{Name(from)} links to {l.FormKey}, which doesn't exist (expects {TypeName(l.Type)})");
                continue;
            }
            var allowed = AllowedSigs(l.Type);
            if (allowed == null) notTyped[TypeName(l.Type)] = notTyped.GetValueOrDefault(TypeName(l.Type)) + 1;
            else if (!allowed.Contains(t.Sig) && !SeenInMasters(l.Type, t.Sig))
                // Where the official masters already use types outside Mutagen's list, Mutagen's list is known to be
                // incomplete for this link type, so an unusual type is only a WARNING there.
                add(MutagenListIncomplete(l.Type, allowed) ? "WARNING" : "ERROR", "links",
                    $"{Name(from)} links to {l.FormKey}, a {t.Sig}, where it expects {TypeName(l.Type)} ({Short(allowed)}"
                    + (MutagenListIncomplete(l.Type, allowed) ? $" or what the official masters use there: {Short(Seen.Value[l.Type.Name].Where(s => !s.StartsWith('~')).ToHashSet())})" : ")"));
            if (t.Deleted) add("WARNING", "links", $"{Name(from)} links to {l.FormKey} ({t.Sig}), which is deleted");
        }

        // 3b. NAVI NVMI traversal From/To navmeshes (raw: Mutagen 0.54.4 keeps traversals in NavigationMapInfo.Unknown;
        // the edge links and preferred edge links it does read, as MergedTo / PreferredMerges, so step 3 checks those)
        foreach (var (rec, what, fid, key) in navKeys)
        {
            if (key is not { } k) { add("ERROR", "navi", $"{rec}: {what} {fid:X8} has a master index past the master list"); continue; }
            if (!found.TryGetValue(k, out var t)) add("ERROR", "navi", $"{rec}: {what} {k.Item2:X6}:{k.Item1} doesn't exist (stored {fid:X8})");
            // like the link check: any type the official masters use for navmesh links is fine (NAVM, REFR of pack-ins and
            // ships, and a few odd vanilla values: ARMA, CELL, DIAL, INFO, NPC_, RFGP; data\link-types-seen.txt)
            else if (t.Sig != "NAVM" && !SeenInMasters(typeof(INavigationMeshGetter), t.Sig)) add("ERROR", "navi", $"{rec}: {what} {k.Item2:X6}:{k.Item1} is a {t.Sig} in {t.DefinedIn}, not a navmesh");
            else if (t.Deleted) add("WARNING", "navi", $"{rec}: {what} {k.Item2:X6}:{k.Item1} is a deleted navmesh");
        }

        // 4. overrides
        foreach (var ov in overrides)
        {
            var sig = SigOf(ov);
            if (!found.TryGetValue(K(ov.FormKey), out var t) || t.DefinedIn.Equals(self, StringComparison.OrdinalIgnoreCase))
                add("WARNING", "links", $"{Name(ov)} overrides {ov.FormKey}, which isn't in {ov.FormKey.ModKey} (injected record)");
            else if (sig != null && t.Sig != sig)
                add("ERROR", "links", $"{Name(ov)} overrides {ov.FormKey}, which is a {t.Sig} in {t.DefinedIn}");
        }

        // 5. itm (0.4.0, from an earlier stand-alone itm-check): overrides identical to the master version they override
        var itmSw = Stopwatch.StartNew();
        var (itms, notCompared) = Overrides.FindItms(mod, overrides, game.LinkCache);
        r.Itms = itms.Count;
        // Official plugins: the count only (user, 2026-11-09). Bethesda's DLC and Creations masters carry hundreds of
        // real ITMs (1,961 over 18 masters) that nobody can act on.
        if (!IsOfficial(self))
            foreach (var ov in itms) add("WARNING", "itm", $"{Name(ov)} is identical to the master version it overrides (ITM)");
        if (notCompared > 0) add("WARNING", "itm", $"{notCompared} override(s) couldn't be compared with their master version");
        Stage($"itm: {itms.Count} of {overrides.Count} overrides identical to master{(IsOfficial(self) ? " (official plugin: count only)" : "")}");
        if (Environment.GetEnvironmentVariable("SFMODCHECK_TIMING") == "1") Console.Error.WriteLine($"itm: {overrides.Count} overrides, {itms.Count} ITM, {itmSw.Elapsed.TotalSeconds:F2} s");
    }

    static string Short(HashSet<string> s) => s.Count <= 8 ? string.Join("/", s.Order()) : string.Join("/", s.Order().Take(8)) + $"/... {s.Count} types";

    // Mutagen 0.54.4's Starfield link types are incomplete in places (for example IPlaceableObject lists only TXST,
    // IAliasVoiceType only FACT), so a target type the official masters themselves use for the same Mutagen link type
    // is accepted too. data\link-types-seen.txt is made by --learn-link-types (type names and counts only).
    static readonly Lazy<Dictionary<string, HashSet<string>>> Seen = new(() =>
    {
        var d = new Dictionary<string, HashSet<string>>();
        using var s = typeof(Checker).Assembly.GetManifestResourceStream("link-types-seen.txt");
        if (s == null) return d;
        using var rd = new StreamReader(s);
        while (rd.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            var p = line.Split(' ');
            if (!d.TryGetValue(p[0], out var set)) d[p[0]] = set = new();
            set.Add(p[1]);
        }
        return d;
    });

    static bool MutagenListIncomplete(Type linkType, HashSet<string> allowed) =>
        Seen.Value.TryGetValue(linkType.Name, out var s) && s.Any(x => !x.StartsWith('~') && !allowed.Contains(x));

    static bool SeenInMasters(Type linkType, string sig) => Seen.Value.TryGetValue(linkType.Name, out var s) && s.Contains(sig);

    static readonly System.Text.RegularExpressions.Regex Official = new(@"^(Starfield|ShatteredSpace|SFBGS\w+|sfta\d+|BlueprintShips-\w+|Constellation|OldMars)\.esm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Bethesda's own masters (by file name).</summary>
    public static bool IsOfficial(string fileName) => Official.IsMatch(fileName);

    /// <summary>The official masters installed in Data, Starfield.esm first (what the learn modes read).</summary>
    public static List<string> OfficialMasters(string dataDir)
    {
        return Directory.GetFiles(dataDir, "*.esm").Select(Path.GetFileName).Where(n => Official.IsMatch(n!)).Cast<string>()
            .OrderBy(n => n.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
    }

    /// <summary>Lists, for each Mutagen link type, the record types its links point to in the official masters.</summary>
    public static void LearnLinkTypes(string dataDir, string outPath)
    {
        var sw = Stopwatch.StartNew();
        using var game = new GameData(dataDir);
        foreach (var n in OfficialMasters(dataDir)) game.Open(n);
        var files = game.LoadOrder.Select(m => m.ModKey.FileName.String).ToList();
        var fileIdx = files.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i, StringComparer.OrdinalIgnoreCase);
        var styleOf = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);

        // every record in every official master: (file, local ID) -> record type
        var index = new Dictionary<ulong, string>();
        foreach (var name in files)
        {
            var path = game.Locate(name)!;
            var hh = RawHeader.Read(path)!;
            styleOf[name] = hh.Style;
            var map = new IdMap(name, hh.Style, hh.Masters.Select(m => (m, styleOf.GetValueOrDefault(m, Style.Full))));
            using var f = new RawFile(path);
            new RawWalker(f, false, (_, _, _) => { })
            {
                OnRecord = rec =>
                {
                    var (file, local, _) = map.Decode(rec.FormId);
                    if (file == null || !fileIdx.TryGetValue(file, out var fi)) return;
                    index.TryAdd(((ulong)fi << 32) | IdMap.Mask(local, styleOf.GetValueOrDefault(file, Style.Full)), string.Intern(rec.Sig));
                },
            }.Walk();
        }
        foreach (var (k, sig) in EngineForms.Value) index.TryAdd(((ulong)fileIdx["Starfield.esm"] << 32) | k.Item2, sig);
        Console.WriteLine($"indexed {index.Count} records in {files.Count} masters ({sw.Elapsed.TotalSeconds:F0} s)");

        var counts = new Dictionary<(string, string), long>();
        var nowhere = new Dictionary<(string, string), long>();
        long links = 0, unresolved = 0, failed = 0;
        foreach (var mod in game.LoadOrder)
        {
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                try
                {
                    foreach (var l in rec.EnumerateFormLinks())
                    {
                        if (l.IsNull) continue;
                        links++;
                        if (!fileIdx.TryGetValue(l.FormKey.ModKey.FileName, out var fi) || !index.TryGetValue(((ulong)fi << 32) | l.FormKey.ID, out var sig))
                        {
                            unresolved++;
                            var nk = (l.Type.Name, (l.FormKey.ModKey == mod.ModKey ? "self" : l.FormKey.ModKey.FileName.String) + ":" + l.FormKey.ID.ToString("X6") + " from " + SigOf(rec));
                            nowhere[nk] = nowhere.GetValueOrDefault(nk) + 1;
                            continue;
                        }
                        var key = (l.Type.Name, sig);
                        counts[key] = counts.GetValueOrDefault(key) + 1;
                    }
                }
                catch { failed++; }
            }
            Console.WriteLine($"  {mod.ModKey}: {links} links so far ({sw.Elapsed.TotalSeconds:F0} s)");
        }
        using var w = new StreamWriter(outPath, false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        w.WriteLine("# For each Mutagen link type (getter interface name): a record type its links point to in the official masters, and how often.");
        w.WriteLine($"# Made by sfmodcheck --learn-link-types (Mutagen 0.54.4) from {string.Join(", ", files)}.");
        w.WriteLine($"# {links} links, {unresolved} pointing nowhere, {failed} records Mutagen couldn't read.");
        foreach (var ((type, sig), n) in counts.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
            w.WriteLine($"{type} {sig} {n}");
        // Low IDs (below 800) that official links point to though no record has them: values Mutagen reads as links
        // in fields that aren't (e.g. LGDI and REFR values 8 and 9). Written as "<type> ~<ID>".
        foreach (var g in nowhere.Select(k => (k.Key.Item1, Id: Convert.ToUInt32(k.Key.Item2.Split(':')[1][..6], 16), k.Value))
                     .Where(x => x.Id < 0x800).GroupBy(x => (x.Item1, x.Id)).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Id))
            w.WriteLine($"{g.Key.Item1} ~{g.Key.Id:X6} {g.Sum(x => x.Value)}");
        using (var nw = new StreamWriter(Path.ChangeExtension(outPath, ".nowhere.txt"), false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" })
            foreach (var ((type, what), n) in nowhere.OrderByDescending(k => k.Value))
                nw.WriteLine($"{n,9} {type} {what}");
        Console.WriteLine($"{counts.Count} type pairs -> {outPath} ({sw.Elapsed.TotalSeconds:F0} s)");
    }

    static string Name(IMajorRecordGetter r) => $"{r.EditorID ?? "(no EditorID)"} [{SigOf(r) ?? r.GetType().Name}:{r.FormKey}]";

    static string? SigOf(IMajorRecordGetter r) => SigsOf(r.GetType())?.FirstOrDefault();

    static string TypeName(Type t) => t.Name.EndsWith("Getter") ? t.Name[..^6].TrimStart('I') : t.Name;

    static readonly Dictionary<Type, HashSet<string>?> allowedCache = new();

    /// <summary>The record types a link of this Mutagen type may point to; null = any record.</summary>
    public static HashSet<string>? AllowedSigs(Type t)
    {
        lock (allowedCache)
        {
            if (allowedCache.TryGetValue(t, out var s)) return s;
            var map = LinkInterfaceMapping.Instance.InterfaceToObjectTypes(GameCategory.Starfield);
            if (map.TryGetValue(t, out var res))
            {
                s = new();
                foreach (var reg in res.Registrations)
                    if (SigsOf(reg.GetterType) is { } g) s.UnionWith(g);
                    else { s = null; break; }
            }
            else s = SigsOf(t);
            return allowedCache[t] = s;
        }
    }

    static HashSet<string>? SigsOf(Type t)
    {
        var attrs = t.GetCustomAttributes<AssociatedRecordTypesAttribute>(true)
            .Concat(t.GetInterfaces().SelectMany(i => i.GetCustomAttributes<AssociatedRecordTypesAttribute>(true)))
            .ToList();
        // A concrete class (e.g. Keyword's overlay) carries the attribute only through its getter interface; a broad
        // interface like IStarfieldMajorRecordGetter has none, so any record type is fine for it.
        if (attrs.Count == 0) return null;
        return attrs.SelectMany(a => a.Types).Select(x => x.Type).ToHashSet();
    }
}
