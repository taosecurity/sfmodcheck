// The built program: exit codes 0/1/2, the RESULT line and the troubleshooting log. It runs as a separate process, so
// SFMODCHECK_CACHE_DIR points its caches into the test run's temp folder, never the user's real caches.
using System.Diagnostics;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class ExeTests
{
    static (int Code, string Out) Run(params string[] args)
    {
        var (code, stdout, stderr) = RunSplit(args);
        return (code, stdout + stderr);
    }

    static (int Code, string Stdout, string Stderr) RunSplit(params string[] args)
    {
        var dir = AppContext.BaseDirectory;
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "sfmodcheck.exe" : "sfmodcheck");
        var psi = File.Exists(exe) ? new ProcessStartInfo(exe) : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(dir, "sfmodcheck.dll") } };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["SFMODCHECK_CACHE_DIR"] = Path.Combine(TestSetup.RunDir, "exe-cache");   // never the user's real caches
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "sfmodcheck didn't finish in 2 minutes");
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Solo.esm with two misc items that each link a keyword the plugin doesn't have: two [links] ERRORs.</summary>
    static string TwoBadLinks(Sandbox s)
    {
        var mod = Plugin.New("Solo.esm", PluginStyle.Light, "test", "no masters");
        mod.MiscItems.AddNew("SOLO_MiscA").Keywords = [new FormKey(mod.ModKey, 0xFF0).ToLink<IKeywordGetter>()];
        mod.MiscItems.AddNew("SOLO_MiscB").Keywords = [new FormKey(mod.ModKey, 0xFF1).ToLink<IKeywordGetter>()];
        var dir = s.Sub("plugin");
        using (var g = new GameData(dir)) PluginWriter.Write(mod, dir, g, new WriteOptions { GapCheck = false });
        return Path.Combine(dir, "Solo.esm");
    }

    static List<string> Lines(string output) => output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l != "").ToList();

    /// <summary>Solo.esm: a light master with no masters, one keyword and a misc item linking it.</summary>
    static string Solo(Sandbox s)
    {
        var mod = Plugin.New("Solo.esm", PluginStyle.Light, "test", "no masters");
        var kw = mod.Keywords.AddNew("SOLO_Keyword");
        mod.MiscItems.AddNew("SOLO_Misc").Keywords = [kw.ToLink<IKeywordGetter>()];
        var dir = s.Sub("plugin");
        using (var g = new GameData(dir)) PluginWriter.Write(mod, dir, g, new WriteOptions { GapCheck = false });
        return Path.Combine(dir, "Solo.esm");
    }

    static string ResultLine(string output) => output.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("RESULT: ")) ?? "(no RESULT line)\n" + output;

    [Fact]
    public void Clean_plugin_exits_0()
    {
        using var s = new Sandbox();
        var (code, output) = Run("check", Solo(s), "--data", s.Sub("data"));
        Assert.Equal(0, code);
        Assert.StartsWith("RESULT: OK, 0 errors, 0 warnings (Solo.esm: ", ResultLine(output));
    }

    [Fact]
    public void Plugin_with_an_error_exits_1()
    {
        using var s = new Sandbox();
        var p = Solo(s);
        var b = File.ReadAllBytes(p);
        Bytes.SetU32(b, 8, Bytes.U32(b, 8) | Flags.Medium);   // Light + Medium
        File.WriteAllBytes(p, b);
        var (code, output) = Run(p, "--data", s.Sub("data"));   // no "check": older callers leave it out
        Assert.Equal(1, code);
        Assert.Contains("ERROR [header] both the Light and the Medium flag are set", output);
        // (Mutagen can't read a plugin with both flags either, so there's a [read] error too and the links aren't checked)
        Assert.StartsWith("RESULT: FAILED, 2 errors, 0 warnings; links not checked (Solo.esm: ", ResultLine(output));
    }

    [Fact]
    public void Missing_plugin_exits_2()
    {
        using var s = new Sandbox();
        var (code, output) = Run("check", Path.Combine(s.Dir, "NoSuch.esm"));
        Assert.Equal(2, code);
        Assert.Contains("no such file", output);
    }

    [Fact]
    public void Unknown_argument_exits_2()
    {
        using var s = new Sandbox();
        var (code, output) = Run("check", Solo(s), "--bogus");
        Assert.Equal(2, code);
        Assert.Contains("unknown option --bogus", output);
        Assert.Contains("usage: sfmodcheck check <plugin>", output);
    }

    // ---- 0.4.0 step 6: the troubleshooting log (--verbose, --log) and errors that name the fix ----

    /// <summary>Addon.esm (light) needs Solo.esm, which sits in its own folder: a master found through --masters-dir.</summary>
    static (string Addon, string MastersDir) AddonWithMaster(Sandbox s)
    {
        var master = Solo(s);
        var mm = Plugin.New("Addon.esm", PluginStyle.Light, "test", "needs Solo.esm");
        var dir = s.Sub("addon");
        using (var g = new GameData(s.Sub("data"), [Path.GetDirectoryName(master)!]))
        {
            var solo = g.Open("Solo.esm");
            mm.MiscItems.AddNew("ADDON_Misc").Keywords = [solo.Keywords.First().ToLink<IKeywordGetter>()];
            PluginWriter.Write(mm, dir, g, new WriteOptions { GapCheck = false });
        }
        return (Path.Combine(dir, "Addon.esm"), Path.GetDirectoryName(master)!);
    }

    [Fact]
    public void Log_file_records_the_data_folder_masters_and_result()
    {
        using var s = new Sandbox();
        var (addon, mdir) = AddonWithMaster(s);
        var log = Path.Combine(s.Dir, "run.log");
        var (code, stdout, stderr) = RunSplit("check", addon, "--data", s.Sub("data"), "--masters-dir", mdir, "--log", log);
        Assert.Equal(0, code);
        Assert.DoesNotContain("log: ", stdout + stderr);   // --log alone writes only the file
        var text = File.ReadAllText(log);
        Assert.Contains($"plugin: {Path.GetFullPath(addon)} (", text);
        Assert.Contains($"Data folder: {Path.GetFullPath(s.Sub("data"))}: from --data; Starfield.esm NOT there", text);
        Assert.Contains($"masters are looked for in: {Path.GetFullPath(mdir)} (--masters-dir)", text);
        Assert.Contains($"master Solo.esm: {Path.Combine(Path.GetFullPath(mdir), "Solo.esm")} (", text);
        Assert.Contains($"cache folder: {Path.Combine(TestSetup.RunDir, "exe-cache")} (from SFMODCHECK_CACHE_DIR)", text);
        Assert.Contains("edid-cache Solo.esm: ", text);   // read or built, depending on the test order
        Assert.Contains("stage itm: ", text);
        Assert.Contains("result: OK, 0 errors, 0 warnings, exit code 0", text);
    }

    [Fact]
    public void Verbose_goes_to_stderr_and_leaves_stdout_alone()
    {
        using var s = new Sandbox();
        var p = TwoBadLinks(s);
        var (_, plain, plainErr) = RunSplit("check", p, "--data", s.Sub("data"));
        var (code, stdout, stderr) = RunSplit("check", p, "--data", s.Sub("data"), "--verbose");
        Assert.Equal(1, code);
        static List<string> NoTiming(string o) => Lines(o).Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"; [0-9.]+ s\)$", ")")).ToList();
        Assert.Equal(NoTiming(plain), NoTiming(stdout));
        Assert.DoesNotContain("log: ", plainErr);
        Assert.Contains("log: sfmodcheck ", stderr);
        Assert.Contains("log: findings: 2 ERROR [links]", stderr);
        Assert.Contains("log: result: FAILED, 2 errors, 0 warnings, exit code 1", stderr);
    }

    [Fact]
    public void Missing_master_is_logged_with_every_place_looked()
    {
        using var s = new Sandbox();
        var (addon, mdir) = AddonWithMaster(s);
        File.Delete(Path.Combine(mdir, "Solo.esm"));
        var (code, stdout, stderr) = RunSplit("check", addon, "--data", s.Sub("data"), "--masters-dir", mdir, "--verbose");
        Assert.Equal(1, code);
        Assert.Contains("ERROR [header] master Solo.esm is not installed", stdout);
        Assert.Contains($"log: master Solo.esm: NOT FOUND (looked in {mdir}, {s.Sub("data")})", stderr);
        Assert.Contains("log: links not checked", stderr);
    }

    [Theory]
    [InlineData("--max", "abc", "--max needs a whole number")]
    [InlineData("--max", null, "--max needs a number after it")]
    [InlineData("--data", null, "--data needs the game's Data folder after it")]
    [InlineData("--log", null, "--log needs a file name after it")]
    public void Bad_option_value_exits_2_with_the_fix(string option, string? value, string expected)
    {
        using var s = new Sandbox();
        var args = new List<string> { "check", Solo(s), option };
        if (value != null) args.Add(value);
        var (code, output) = Run(args.ToArray());
        Assert.Equal(2, code);
        Assert.Contains(expected, output);
        Assert.Contains("for example: " + option, output);
        Assert.DoesNotContain("   at ", output);   // no stack trace
    }

    [Fact]
    public void Log_in_a_missing_folder_exits_2_naming_the_folder()
    {
        using var s = new Sandbox();
        var log = Path.Combine(s.Dir, "no-such-folder", "run.log");
        var (code, output) = Run("check", Solo(s), "--data", s.Sub("data"), "--log", log);
        Assert.Equal(2, code);
        Assert.Contains($"can't write the log file {log}: the folder {Path.GetDirectoryName(log)} doesn't exist", output);
    }

    [Fact]
    public void Missing_plugin_names_the_path_and_the_fix()
    {
        using var s = new Sandbox();
        var p = Path.Combine(s.Dir, "NoSuch.esm");
        var (code, output) = Run("check", p, "--log", Path.Combine(s.Dir, "run.log"));
        Assert.Equal(2, code);
        Assert.Contains($"can't check {p}: no such file. Check the path", output);
        Assert.Contains($"stopped: plugin {p}: no such file", File.ReadAllText(Path.Combine(s.Dir, "run.log")));
    }

    [Fact]
    public void Every_option_is_in_the_usage_readme_and_agents()
    {
        // Program.cs's option switch, README.md and AGENTS.md must name the same options (CLAUDE.md: AGENTS.md changes
        // in the same commit as any change to the options).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Program.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\Program.cs not found above " + AppContext.BaseDirectory);
        var program = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Program.cs"));
        var options = System.Text.RegularExpressions.Regex.Matches(program, @"case ""(--[a-z-]+)"":").Select(m => m.Groups[1].Value)
            .Where(o => o != "--help").ToHashSet();
        Assert.Equal(new[] { "--data", "--log", "--masters-dir", "--max", "--no-hints", "--quiet", "--verbose" }, options.Order());
        var (_, usage) = Run("check");
        foreach (var file in new[] { "README.md", "AGENTS.md" })
        {
            var text = File.ReadAllText(Path.Combine(dir.FullName, file));
            Assert.All(options, o => Assert.True(text.Contains("`" + o) || text.Contains(" " + o + " "), $"{file} doesn't mention {o}"));
        }
        Assert.All(options, o => Assert.Contains(o, usage));
    }

    [Fact]
    public void Missing_data_folder_exits_2_with_cannot_check()
    {
        using var s = new Sandbox();
        var (code, output) = Run("check", Solo(s), "--data", Path.Combine(s.Dir, "no-such-data"));
        Assert.Equal(2, code);
        Assert.StartsWith("RESULT: CANNOT CHECK Solo.esm (", ResultLine(output));
    }

    [Fact]
    public void Hint_comes_once_after_the_checks_last_finding()
    {
        using var s = new Sandbox();
        var (code, stdout, _) = RunSplit("check", TwoBadLinks(s), "--data", s.Sub("data"));
        Assert.Equal(1, code);
        var lines = Lines(stdout);
        var links = lines.FindAll(l => l.StartsWith("ERROR [links] "));
        Assert.Equal(2, links.Count);
        var hints = lines.FindAll(l => l.StartsWith("HINT "));
        Assert.Equal(new[] { "HINT [links] " + Hints.ByCheck["links"] }, hints);
        Assert.Equal(lines.LastIndexOf(links[^1]) + 1, lines.IndexOf(hints[0]));   // right after the last [links] line
        Assert.StartsWith("RESULT: FAILED, 2 errors, 0 warnings (Solo.esm: ", lines[^1]);
    }

    [Fact]
    public void No_hints_leaves_only_the_hint_lines_out()
    {
        using var s = new Sandbox();
        var p = TwoBadLinks(s);
        var (_, with, _) = RunSplit("check", p, "--data", s.Sub("data"));
        var (code, without, _) = RunSplit("check", p, "--data", s.Sub("data"), "--no-hints");
        Assert.Equal(1, code);
        static List<string> Findings(string o) => Lines(o).Where(l => !l.StartsWith("RESULT: ")).ToList();   // RESULT has a timing
        Assert.DoesNotContain(Findings(without), l => l.StartsWith("HINT "));
        Assert.Equal(Findings(with).Where(l => !l.StartsWith("HINT ")), Findings(without));
    }

    [Fact]
    public void Clean_plugin_has_no_hints()
    {
        using var s = new Sandbox();
        var (_, stdout, _) = RunSplit("check", Solo(s), "--data", s.Sub("data"));
        Assert.DoesNotContain(Lines(stdout), l => l.StartsWith("HINT "));
    }

    [Fact]
    public void Very_large_plugin_gets_the_memory_note_on_stderr_only()
    {
        using var s = new Sandbox();
        var p = Solo(s);
        var (_, plainOut, plainErr) = RunSplit("check", p, "--data", s.Sub("data"));
        Assert.DoesNotContain("very large", plainErr);
        var b = File.ReadAllBytes(p);
        Assert.Equal("HEDR", System.Text.Encoding.ASCII.GetString(b, 24, 4));
        Bytes.SetU32(b, 34, 600_000);   // the header's record count
        File.WriteAllBytes(p, b);
        var (_, stdout, stderr) = RunSplit("check", p, "--data", s.Sub("data"));
        Assert.Contains("note: Solo.esm is very large", stderr);
        Assert.DoesNotContain("very large", stdout);
    }

    [Fact]
    public void Every_check_has_a_hint()
    {
        // The check names the checker can report, read from its source: Add("ERROR", "links", ...), add(sev, "scenes", ...).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Hints.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\Hints.cs not found above " + AppContext.BaseDirectory);
        var re = new System.Text.RegularExpressions.Regex(@"\b[Aa]dd\((?:""(?:ERROR|WARNING)""|sev), ""([a-z]+)""");
        var used = Directory.GetFiles(Path.Combine(dir!.FullName, "src"), "*.cs")
            .SelectMany(f => re.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.Equal(12, used.Count);   // structure, header, ids, records, read, links, navi, conditions, dialogue, scenes, editorids, itm
        Assert.Equal(used.OrderBy(x => x), Hints.ByCheck.Keys.OrderBy(x => x));
        Assert.All(Hints.ByCheck.Values, h => Assert.True(h.Length > 40 && !h.Contains('\n'), "hint too short or not one line: " + h));
        // README's "For Creation Kit and xEdit authors" section repeats each hint word for word.
        var readme = File.ReadAllText(Path.Combine(dir.FullName, "README.md"));
        Assert.All(Hints.ByCheck, kv => Assert.True(readme.Contains($"- **{kv.Key}**: {kv.Value}\n") || readme.Contains($"- **{kv.Key}**: {kv.Value}\r\n"),
            $"README.md doesn't have the hint for {kv.Key} word for word"));
    }
}
