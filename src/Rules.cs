// Content rules added in 0.2.0 (2026-11-07, from the xDump comparison, COMPARE-XDUMP.md "Proposed rules"):
//   conditions  a condition's form parameter is empty where the official masters always fill it (Mutagen's
//               per-function condition classes say which parameters are forms; data\condition-params-seen.txt says
//               what the masters do)
//   dialogue    an INFO response's Emotion is 00000000 (the masters use FFFFFFFF for "none" or a keyword)
//   scenes      every alias number a scene uses (Actors, actions, player-dialogue target, alias conditions) exists in
//               its parent quest, and the scene has a parent quest (PNAM)
//   navi        NAVI NVMI traversal From/To navmeshes point to existing navmeshes (edge and preferred edge links are
//               ordinary Mutagen links, checked by the link check)
//               (read raw: Mutagen 0.54.4 keeps traversals in an unparsed byte blob)
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using BuildKit;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

/// <summary>Finds every condition inside a record, whatever field it sits in, through Mutagen's getter interfaces.
/// Which types can hold a condition is worked out once per type (reflection), so records that can't are skipped fast.
/// Child records (a quest's topics and scenes) are not entered: they are checked as records of their own.</summary>
public static class ConditionWalker
{
    static readonly Assembly Asm = typeof(Condition).Assembly;
    static readonly Dictionary<Type, bool> reach = new();
    static readonly Dictionary<Type, PropertyInfo[]> propsFor = new();

    public static void ForEach(IMajorRecordGetter root, Action<IConditionGetter> f) => Walk(root, root, f, 0);

    static void Walk(object o, object root, Action<IConditionGetter> f, int depth)
    {
        if (depth > 40) return;
        if (o is IConditionGetter c) { f(c); return; }
        if (o is string) return;
        if (!ReferenceEquals(o, root) && o is IMajorRecordGetter) return;
        if (o is System.Collections.IEnumerable e)
        {
            foreach (var item in e) if (item != null) Walk(item, root, f, depth + 1);
            return;
        }
        foreach (var p in PropsFor(o.GetType()))
        {
            object? v;
            try { v = p.GetValue(o); } catch { continue; }
            if (v != null) Walk(v, root, f, depth + 1);
        }
    }

    static IEnumerable<PropertyInfo> AllProps(Type i) => i.GetProperties().Concat(i.GetInterfaces().SelectMany(x => x.GetProperties()));

    static IEnumerable<Type> GetterInterfaces(Type t) =>
        (t.IsInterface ? t.GetInterfaces().Append(t) : t.GetInterfaces()).Where(i => i.Assembly == Asm && i.Name.EndsWith("Getter"));

    static PropertyInfo[] PropsFor(Type rt)
    {
        if (propsFor.TryGetValue(rt, out var ps)) return ps;
        ps = GetterInterfaces(rt).SelectMany(i => i.GetProperties())
            .Where(p => p.GetIndexParameters().Length == 0 && Reach(p.PropertyType))
            .GroupBy(p => p.Name).Select(g => g.First()).ToArray();
        return propsFor[rt] = ps;
    }

    static Type? ElementType(Type t)
    {
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return t.GetGenericArguments()[0];
        return t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
    }

    static bool Reach(Type t)
    {
        if (reach.TryGetValue(t, out var r)) return r;
        reach[t] = false;   // cycle guard
        bool res;
        if (typeof(IConditionGetter).IsAssignableFrom(t)) res = true;
        else if (t == typeof(string) || t.IsPrimitive || t.IsEnum) res = false;
        else if (typeof(IMajorRecordGetter).IsAssignableFrom(t)) res = false;   // child records are checked on their own
        else if (ElementType(t) is { } el) res = Reach(el);
        else if (t.Assembly != Asm) res = false;
        else
        {
            // the type itself and, for an abstract type or interface, every getter interface that derives from it
            // (scene actions, perk effects... are polymorphic)
            var cands = GetterInterfaces(t).ToList();
            if (t.IsInterface || t.IsAbstract)
                cands.AddRange(Asm.GetTypes().Where(x => x.IsInterface && x.Name.EndsWith("Getter") && x != t && t.IsAssignableFrom(x)));
            res = cands.Distinct().SelectMany(AllProps).Any(p => p.GetIndexParameters().Length == 0 && p.PropertyType != t && Reach(p.PropertyType));
        }
        return reach[t] = res;
    }

    // ---- parameters ----

    public enum Kind { NotForm, Link, Alias, PackageData }

    sealed record LinkOrIndex(MethodInfo UsesLink, MethodInfo UsesAlias, PropertyInfo Link, PropertyInfo Index);
    static readonly Dictionary<Type, LinkOrIndex?> loi = new();

    /// <summary>What a condition parameter holds: a form link (empty or not), an alias number, package data, or
    /// something that isn't a form at all.</summary>
    public static (Kind Kind, bool Empty, FormKey Key, uint? Index) Param(object? v)
    {
        if (v == null) return (Kind.NotForm, false, FormKey.Null, null);
        var t = v.GetType();
        if (!loi.TryGetValue(t, out var m))
        {
            var gi = t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition().Name == "IFormLinkOrIndexGetter`1");
            m = gi == null ? null : new LinkOrIndex(gi.GetMethod("UsesLink")!, gi.GetMethod("UsesAlias")!, gi.GetProperty("Link")!, gi.GetProperty("Index")!);
            loi[t] = m;
        }
        if (m != null)
        {
            if ((bool)m.UsesLink.Invoke(v, null)!)
            {
                var l = (IFormLinkGetter)m.Link.GetValue(v)!;
                return (Kind.Link, l.IsNull, l.FormKey, null);
            }
            return ((bool)m.UsesAlias.Invoke(v, null)! ? Kind.Alias : Kind.PackageData, false, FormKey.Null, (uint?)m.Index.GetValue(v));
        }
        if (v is IFormLinkGetter fl) return (Kind.Link, fl.IsNull, fl.FormKey, null);
        return (Kind.NotForm, false, FormKey.Null, null);
    }

    public static string ParamTypeName(Type? t)
    {
        if (t == null) return "?";
        var inner = t.IsGenericType ? t.GetGenericArguments()[0] : t;
        var n = inner.Name;
        if (n.EndsWith("Getter")) n = n[..^6];
        return n.StartsWith('I') ? n[1..] : n;
    }

    /// <summary>Functions whose first parameter is an alias number of the quest the condition runs in (Mutagen's
    /// per-function classes: "Alias" in the name, Int32 first parameter; e.g. GetIsAliasRef, GetIsEditorLocationAlias).</summary>
    public static bool FirstParamIsAlias(IConditionDataGetter d) =>
        d.Function.ToString().Contains("Alias") && ((IConditionParametersGetter)d).Parameter1Type == typeof(int);
}

public static class Rules
{
    // ---- conditions ----

    /// <summary>(function number, parameter 1/2) -> (uses, empty uses) in the official masters.</summary>
    static readonly Lazy<Dictionary<(int, int), (long Uses, long Empty)>> ParamsSeen = new(() =>
    {
        var d = new Dictionary<(int, int), (long, long)>();
        using var s = typeof(Rules).Assembly.GetManifestResourceStream("condition-params-seen.txt");
        if (s == null) return d;
        using var rd = new StreamReader(s);
        while (rd.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            var p = line.Split(' ');
            d[(int.Parse(p[0]), int.Parse(p[1]))] = (long.Parse(p[2]), long.Parse(p[3]));
        }
        return d;
    });

    public static bool HaveConditionData => ParamsSeen.Value.Count > 0;

    public static void CheckConditions(IMajorRecordGetter rec, string name, Action<string, string, string> add)
    {
        // one line per function and parameter in this record, with how many conditions have it empty
        var count = new Dictionary<string, int>();
        var order = new List<string>();
        ConditionWalker.ForEach(rec, c =>
        {
            var d = c.Data;
            var p = (IConditionParametersGetter)d;
            var fn = (int)d.Function;
            for (int slot = 1; slot <= 2; slot++)
            {
                var (kind, empty, _, _) = ConditionWalker.Param(slot == 1 ? p.Parameter1 : p.Parameter2);
                if (kind != ConditionWalker.Kind.Link || !empty) continue;
                if (!ParamsSeen.Value.TryGetValue((fn, slot), out var seen) || seen.Uses == 0 || seen.Empty > 0) continue;
                var text = $"{name}: condition {d.Function} has an empty {ConditionWalker.ParamTypeName(slot == 1 ? p.Parameter1Type : p.Parameter2Type)} parameter {slot} (the official masters always fill it: {seen.Uses} uses), so it tests nothing";
                if (!count.TryGetValue(text, out var n)) order.Add(text);
                count[text] = n + 1;
            }
        });
        foreach (var t in order) add("WARNING", "conditions", count[t] > 1 ? $"{t} ({count[t]} conditions)" : t);
    }

    /// <summary>Counts, for every condition function and form parameter, how often the official masters use it and
    /// leave it empty. Writes data\condition-params-seen.txt (numbers and Mutagen names only).</summary>
    public static void LearnConditions(string dataDir, string outPath)
    {
        var sw = Stopwatch.StartNew();
        using var game = new GameData(dataDir);
        foreach (var n in Checker.OfficialMasters(dataDir)) game.Open(n);
        var counts = new Dictionary<(int, int), (long Uses, long Empty)>();
        var names = new Dictionary<int, string>();
        long conds = 0, failed = 0;
        var bySig = new Dictionary<string, long>();
        foreach (var mod in game.LoadOrder)
        {
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                try
                {
                    ConditionWalker.ForEach(rec, c =>
                    {
                        conds++;
                        var sg = rec.Registration.ClassType.Name; bySig[sg] = bySig.GetValueOrDefault(sg) + 1;
                        var d = c.Data;
                        var p = (IConditionParametersGetter)d;
                        names[(int)d.Function] = d.Function.ToString();
                        for (int slot = 1; slot <= 2; slot++)
                        {
                            var (kind, empty, _, _) = ConditionWalker.Param(slot == 1 ? p.Parameter1 : p.Parameter2);
                            if (kind == ConditionWalker.Kind.NotForm) continue;
                            var k = ((int)d.Function, slot);
                            var v = counts.GetValueOrDefault(k);
                            counts[k] = (v.Uses + 1, v.Empty + (kind == ConditionWalker.Kind.Link && empty ? 1 : 0));
                        }
                    });
                }
                catch { failed++; }
            }
            Console.WriteLine($"  {mod.ModKey}: {conds} conditions so far ({sw.Elapsed.TotalSeconds:F0} s)");
        }
        using var w = new StreamWriter(outPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
        w.WriteLine("# Condition form parameters in the official masters: <function number> <parameter 1|2> <uses> <empty uses>  # Mutagen function name.");
        w.WriteLine($"# Made by sfmodcheck --learn-conditions (Mutagen 0.54.4) from {string.Join(", ", game.LoadOrder.Select(m => m.ModKey.FileName.String))}.");
        w.WriteLine($"# {conds} conditions, {failed} records Mutagen couldn't read. Uses include alias and package-data parameters; 'empty' = a null form link.");
        foreach (var ((fn, slot), (uses, empty)) in counts.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
            w.WriteLine($"{fn} {slot} {uses} {empty}  # {names[fn]}");
        foreach (var (sg, n) in bySig.OrderByDescending(x => x.Value)) Console.WriteLine($"    {sg} {n}");
        Console.WriteLine($"{counts.Count} function parameters -> {outPath} ({sw.Elapsed.TotalSeconds:F0} s)");
    }

    // ---- scenes and quest aliases ----

    /// <summary>Starfield partial form (record flag 0x4000): only the fields it holds replace the earlier version's.</summary>
    public static bool IsPartial(IMajorRecordGetter r) => (r.MajorRecordFlagsRaw & 0x4000) != 0;

    /// <summary>The quest as the game builds it for its aliases: the winning version that isn't a partial form (e.g.
    /// ShatteredSpace.esm overrides COM_Companion_SarahMorgan with only EDID + VMAD, and the aliases come from
    /// Starfield.esm).</summary>
    public static IQuestGetter? FullQuest(ILinkCache cache, FormKey fk) =>
        cache.ResolveAll<IQuestGetter>(fk).FirstOrDefault(q => !IsPartial(q));

    public static HashSet<uint> AliasIds(IQuestGetter q)
    {
        var s = new HashSet<uint>();
        foreach (var a in q.Aliases ?? [])
        {
            if (a is IQuestReferenceAliasGetter r) s.Add(r.ID);
            else if (a is IQuestLocationAliasGetter l) s.Add(l.ID);
        }
        return s;
    }

    /// <summary>Alias numbers a condition uses: run on a quest alias, or an alias-number parameter (GetIsAliasRef etc.).
    /// (Form parameters flagged "use aliases" are not read: in the official masters many of them hold values like -2 or
    /// 17063529 that can't be alias numbers, so Mutagen's reading of that flag isn't reliable enough for a rule.)</summary>
    public static IEnumerable<(uint Id, string Where)> ConditionAliases(IConditionGetter c)
    {
        var d = c.Data;
        if (d.RunOnType == Condition.RunOnType.QuestAlias && d.Unknown3 >= 0) yield return ((uint)d.Unknown3, $"condition {d.Function} runs on alias");
        var p = (IConditionParametersGetter)d;
        if (ConditionWalker.FirstParamIsAlias(d) && p.Parameter1 is int a && a >= 0) yield return ((uint)a, $"condition {d.Function} tests alias");
    }

    /// <summary>Official scenes whose alias numbers don't match their quest, as built into the private build
    /// (data\scene-alias-quirks.txt, made by --learn-scene-quirks; embedded only when EmbedSceneQuirks is on). The list
    /// learned from the Data folder (LearnedCache) wins; this one only fills in when Data lacks the masters.</summary>
    static readonly Lazy<HashSet<string>> BuiltInSceneQuirks = new(() =>
    {
        var d = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(Rules).Assembly.GetManifestResourceStream("scene-alias-quirks.txt");
        if (s == null) return d;
        using var rd = new StreamReader(s);
        while (rd.ReadLine() is { } line)
            if (!line.StartsWith('#') && line.Length > 0) d.Add(line.Split(' ')[0]);
        return d;
    });

    public static bool HaveBuiltInSceneQuirks => BuiltInSceneQuirks.Value.Count > 0;

    public static bool IsBuiltInSceneQuirk(FormKey fk) => BuiltInSceneQuirks.Value.Contains(fk.ToString());

    public static readonly string[] SceneQuirkHeader =
    [
        "# Official scenes whose alias numbers don't match their parent quest (sfmodcheck --learn-scene-quirks, Mutagen 0.54.4).",
        "# The scenes rule skips exactly these records. <FormKey> <EditorID>: <first finding>",
    ];

    /// <summary>Runs the scene rule on every scene of the official masters in Data and returns one line per scene it
    /// flags: "FormKey EditorID: first finding".</summary>
    public static List<string> LearnSceneQuirkLines(string dataDir)
    {
        using var game = new GameData(dataDir);
        foreach (var n in Checker.OfficialMasters(dataDir)) game.Open(n);
        var lines = new List<string>();
        foreach (var mod in game.LoadOrder)
            foreach (var s in mod.EnumerateMajorRecords<ISceneGetter>())
            {
                var found = new List<string>();
                CheckScene(s, "", fk => FullQuest(game.LinkCache, fk), (_, _, t) => found.Add(t));
                if (found.Count == 0) continue;
                lines.Add($"{s.FormKey} {s.EditorID}: {found[0].TrimStart(':', ' ')}" + (found.Count > 1 ? $" (+{found.Count - 1} more)" : ""));
            }
        return lines;
    }

    /// <summary>--learn-scene-quirks: writes the list to a file (the private build's data\scene-alias-quirks.txt).</summary>
    public static void LearnSceneQuirks(string dataDir, string outPath)
    {
        var lines = LearnSceneQuirkLines(dataDir);
        File.WriteAllText(outPath, string.Join("\n", SceneQuirkHeader.Concat(lines)) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"{lines.Count} scenes -> {outPath}");
    }

    /// <summary>The scene rule itself. Quirks are not consulted here: the caller decides which official scenes to
    /// skip (Checker.LinkPass).</summary>
    public static void CheckScene(ISceneGetter s, string name, Func<FormKey, IQuestGetter?> quest, Action<string, string, string> add)
    {
        if (s.Quest.IsNull) { add("WARNING", "scenes", $"{name} has no parent quest (PNAM), so it can't run"); return; }
        var q = quest(s.Quest.FormKey);
        if (q == null) return;   // the link check reports a missing quest
        var ids = AliasIds(q);
        var qn = $"{q.EditorID ?? "(no EditorID)"} [QUST:{q.FormKey}]";
        var said = new HashSet<string>();
        void Need(long id, string what, string sev = "ERROR")
        {
            if (id < 0 || ids.Contains((uint)id)) return;
            var t = $"{name}: {what} {id}, which its quest {qn} doesn't have";
            if (said.Add(t)) add(sev, "scenes", t);
        }
        foreach (var a in s.Actors) Need((int)a.ID, "actor slot uses alias");   // stored unsigned; negative = no alias (-2 in the masters)
        foreach (var act in s.Actions ?? [])
        {
            if (act.AliasID is { } id) Need(id, $"action {act.Index?.ToString() ?? "?"} ({act.GetType().Name.Replace("BinaryOverlay", "").Replace("SceneAction", "")}) uses alias");
            if (act is IPlayerDialogueSceneActionGetter pd && pd.DialogueTargetActor is { } t) Need(t, $"player-dialogue action {act.Index?.ToString() ?? "?"} targets (DTGT) alias");
        }
        // Alias numbers in conditions: WARNING only. The official masters have 75 scenes whose GetIsAliasRef / run-on-alias
        // conditions name aliases the quest lacks (radiant StartQuest and ship-hail scenes), so it is sometimes by design.
        ConditionWalker.ForEach(s, c => { foreach (var (id, w) in ConditionAliases(c)) Need(id, w, "WARNING"); });
    }

    // ---- raw: INFO Emotion and NAVI NVMI lists ----

    public sealed class RawFindings
    {
        public readonly List<string> Emotion0 = new();
        /// <summary>(record text, what, stored FormID) for every navmesh ID in a NAVI's NVMI lists.</summary>
        public readonly List<(string Rec, string What, uint Fid)> NavLinks = new();
        public readonly List<string> NaviUnreadable = new();
        public int NaviEntries;
    }

    static string RawName(RawRecord rec, List<(string Type, byte[] Data)> fields, IdMap ids)
    {
        var (file, local, _) = ids.Decode(rec.FormId);
        var edid = fields.FirstOrDefault(f => f.Type == "EDID").Data;
        var e = edid == null ? "(no EditorID)" : Encoding.UTF8.GetString(edid).TrimEnd('\0');
        return $"{e} [{rec.Sig}:{local:X6}:{file ?? "?"}]";
    }

    public static void OnRawFields(RawRecord rec, List<(string Type, byte[] Data)> fields, IdMap ids, RawFindings rf)
    {
        if (rec.Sig == "INFO")
        {
            int n = 0;
            foreach (var (t, d) in fields)
            {
                if (t != "TRDA") continue;
                n++;
                if (d.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(d) == 0)
                    rf.Emotion0.Add($"{RawName(rec, fields, ids)}: response {n} has Emotion 00000000 (the official masters use FFFFFFFF for none, or an emotion keyword)");
            }
        }
        else if (rec.Sig == "NAVI")
        {
            foreach (var (t, d) in fields)
            {
                if (t != "NVMI") continue;
                rf.NaviEntries++;
                if (!ParseNvmi(d, out var navmesh, out var links, out var why))
                {
                    rf.NaviUnreadable.Add($"{RawName(rec, fields, ids)}: NVMI entry {rf.NaviEntries} can't be read ({why})");
                    continue;
                }
                foreach (var (what, fid) in links) rf.NavLinks.Add(($"{RawName(rec, fields, ids)} NVMI for navmesh {navmesh:X8}", what, fid));
            }
        }
    }

    /// <summary>NVMI layout (Starfield, as xEdit's definitions describe it): navmesh, flags, approx location (3
    /// floats), preferred %, edge links (count + IDs), preferred edge links (count + IDs), door links (count + 8-byte
    /// CRC/door ref), traversals (count + 60 bytes: type, cell or object, traversal, from position, to position, 3
    /// floats, flags, from navmesh, to navmesh), then island and pathing-cell data (not needed here).</summary>
    public static bool ParseNvmi(ReadOnlySpan<byte> d, out uint navmesh, out List<(string What, uint Fid)> links, out string why)
    {
        links = new();
        navmesh = 0;
        why = "";
        int o = 0;
        bool Need(int n, ReadOnlySpan<byte> span) => o + n <= span.Length;
        if (!Need(24, d)) { why = $"{d.Length} bytes"; return false; }
        navmesh = BinaryPrimitives.ReadUInt32LittleEndian(d);
        o = 24;
        foreach (var (what, size, idAt) in new[] { ("edge link", 4, Array.Empty<int>()), ("preferred edge link", 4, Array.Empty<int>()), ("door link", 8, Array.Empty<int>()), ("traversal", 60, new[] { 52, 56 }) })
        {
            if (!Need(4, d)) { why = $"{what} count past the end"; return false; }
            var count = BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
            o += 4;
            if (count > 100000 || !Need((int)count * size, d)) { why = $"{count} {what}s don't fit"; return false; }
            for (int i = 0; i < count; i++, o += size)
                foreach (var at in idAt)
                    links.Add((what + (what == "traversal" ? (at == 52 ? " from" : " to") : ""), BinaryPrimitives.ReadUInt32LittleEndian(d[(o + at)..])));
        }
        return true;
    }
}
