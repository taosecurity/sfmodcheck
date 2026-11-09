// sfmodcheck: read-only checks of Starfield mods. Command "check" covers the part of xEdit's "Check for Errors" our
// build gates use: it prints ERROR / WARNING lines, a HINT line per check that found something, and one RESULT line.
// Exit 0 = no errors, 1 = errors, 2 = could not run.
//   sfmodcheck check <plugin> [--data <Data folder>] [--masters-dir <folder>]... [--max <lines per check>] [--quiet] [--no-hints]
//   sfmodcheck <plugin> [...]                  (same as check: older callers, and a plugin dropped on the exe)
//   sfmodcheck --version
// Hidden maintenance modes (run on the official masters after a game update):
//   sfmodcheck --census <plugin>...      (raw layout counts; used to set the structure rules)
//   sfmodcheck --learn-link-types <out> [Data]     (record types each Mutagen link type points to)
//   sfmodcheck --learn-conditions <out> [Data]     (condition form parameters in the official masters)
//   sfmodcheck --learn-scene-quirks <out> [Data]   (official scenes the scenes rule must skip)
// Until 2026-11-09 (0.3.0) this program was called StarfieldPluginCheck.
using System.Diagnostics;
using SfModCheck;

const string Version = "0.4.0";
const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
const string Usage =
    "usage: sfmodcheck check <plugin> [--data <Data folder>] [--masters-dir <folder>]... [--max <lines per check>] [--quiet] [--no-hints]\n" +
    "       sfmodcheck --version";
// A plugin past this many records (the header's count) gets the memory note on stderr. Mods stay far below it; the
// official Starfield.esm has about 3.8 million and needs over 30 GB of memory to check.
const int LargeRecordCount = 500_000;

var code = Dispatch(args);
if (Interactive.OwnsConsole())
{
    // Started by dropping a plugin on the exe (or by double-clicking it): keep the window open so the result can be read.
    Console.Error.WriteLine();
    Console.Error.Write("Press Enter to close this window.");
    Console.ReadLine();
}
return code;

int Dispatch(string[] args)
{
    if (args.Length == 1 && args[0] == "--version") { Console.WriteLine($"sfmodcheck {Version}"); return 0; }

    if (args.Length >= 2 && args[0] == "--learn-scene-quirks")
    {
        Rules.LearnSceneQuirks(args.Length > 2 ? args[2] : DefaultData, args[1]);
        return 0;
    }

    if (args.Length >= 2 && args[0] == "--learn-conditions")
    {
        Rules.LearnConditions(args.Length > 2 ? args[2] : DefaultData, args[1]);
        return 0;
    }

    if (args.Length >= 2 && args[0] == "--census")
    {
        foreach (var path in args.Skip(1))
        {
            var sw = Stopwatch.StartNew();
            using var f = new RawFile(path);
            int problems = 0;
            var w = new RawWalker(f, deep: true, (sev, off, text) => { if (problems++ < 20) Console.WriteLine($"  {sev} @{off:X}: {text}"); });
            w.Walk();
            Console.WriteLine($"{Path.GetFileName(path)}: {w.Records} records, {w.Groups} groups, {problems} problems, {sw.Elapsed.TotalSeconds:F1} s");
            foreach (var (k, v) in w.Census.OrderBy(k => k.Key))
                if (!k.Contains("in type 0") || k.StartsWith("group")) Console.WriteLine($"    {k}: {v}");
        }
        return 0;
    }

    if (args.Length >= 2 && args[0] == "--learn-link-types")
    {
        Checker.LearnLinkTypes(args.Length > 2 ? args[2] : DefaultData, args[1]);
        return 0;
    }

    // Commands. "check" is the only one so far; "conflict" and "release" come later. A first argument that isn't a
    // command is read as the plugin, so callers written for StarfieldPluginCheck keep working.
    var rest = args.Length > 0 && args[0] == "check" ? args.Skip(1).ToArray() : args;
    return Check(rest);
}

int Check(string[] a)
{
    string? plugin = null;
    var o = new Options { Plugin = "" };
    bool quiet = false, hints = true, dataGiven = false;
    for (int i = 0; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--data": o.DataDir = a[++i]; dataGiven = true; break;
            case "--masters-dir": o.MastersDirs.Add(a[++i]); break;
            case "--max": o.MaxPerCheck = int.Parse(a[++i]); break;
            case "--quiet": quiet = true; break;
            case "--no-hints": hints = false; break;
            case "-h": case "--help": plugin = null; i = a.Length; break;
            default:
                if (a[i].StartsWith("--") || plugin != null) { Console.Error.WriteLine($"unknown argument {a[i]}"); return 2; }
                plugin = a[i];
                break;
        }
    }
    if (plugin == null)
    {
        Console.Error.WriteLine(Usage);
        if (a.Length == 0) Console.Error.WriteLine("To check a plugin, drag its .esm file onto sfmodcheck.exe, or run: sfmodcheck check MyMod.esm");
        return 2;
    }
    if (!File.Exists(plugin)) { Console.Error.WriteLine($"no such file: {plugin}"); return 2; }
    o.Plugin = plugin;

    // No --data and no game in Steam's usual place: a plugin sitting in a game Data folder (Game Pass and other
    // installs) uses that folder.
    string dataNote = "";
    if (!dataGiven && !File.Exists(Path.Combine(o.DataDir, "Starfield.esm")))
    {
        var own = Path.GetDirectoryName(Path.GetFullPath(plugin))!;
        if (File.Exists(Path.Combine(own, "Starfield.esm"))) { o.DataDir = own; dataNote = $"Data folder: {own} (the plugin's own folder)"; }
    }

    if (Checker.IsOfficial(Path.GetFileName(plugin)) || Interactive.HeaderRecordCount(plugin) > LargeRecordCount)
        Console.Error.WriteLine($"note: {Path.GetFileName(plugin)} is very large; checking it can take 30 GB of memory or more. " +
                                "sfmodcheck is meant for mods, not Bethesda's own masters.");

    Result r;
    try { r = Checker.Run(o); }
    catch (Exception e) { Console.WriteLine($"RESULT: CANNOT CHECK {Path.GetFileName(plugin)} ({e.GetType().Name}: {e.Message})"); return 2; }

    if (!quiet) Console.WriteLine($"sfmodcheck {Version} check: {Path.GetFileName(plugin)} ({r.Summary})");
    if (dataNote != "") Console.WriteLine(dataNote);
    var groups = r.Findings.GroupBy(f => (f.Severity, f.Check)).OrderBy(g => g.Key.Severity == "ERROR" ? 0 : 1).ToList();
    for (int gi = 0; gi < groups.Count; gi++)
    {
        var g = groups[gi];
        foreach (var f in g.Take(o.MaxPerCheck)) Console.WriteLine($"{f.Severity} [{f.Check}] {f.Text}");
        if (g.Count() > o.MaxPerCheck) Console.WriteLine($"{g.Key.Severity} [{g.Key.Check}] ... and {g.Count() - o.MaxPerCheck} more");
        // The hint comes once, after the check's last group (a check can have an ERROR group and a WARNING group).
        if (hints && !groups.Skip(gi + 1).Any(x => x.Key.Check == g.Key.Check) && Hints.For(g.Key.Check) is { } hint)
            Console.WriteLine($"HINT [{g.Key.Check}] {hint}");
    }
    var note = r.CannotCheckLinks ? "; links not checked" : "";
    if (r.ScenesNotChecked > 0) note += $"; {r.ScenesNotChecked} official scene{(r.ScenesNotChecked == 1 ? "" : "s")} not checked ({r.ScenesNotCheckedWhy})";
    var verdict = r.Errors > 0 ? "FAILED" : "OK";
    Console.WriteLine($"RESULT: {verdict}, {r.Errors} errors, {r.Warnings} warnings{note} ({Path.GetFileName(plugin)}: {r.Records} records, {r.Links} links; {r.Elapsed.TotalSeconds:F1} s)");
    return r.Errors > 0 ? 1 : 0;
}
