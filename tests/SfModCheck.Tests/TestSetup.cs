// Test-run setup: tests run one at a time (the checker keeps static lookup tables), and sfmodcheck's two caches
// (EditorIDs and the learned official scene list) never point at the user's %LOCALAPPDATA%\sfmodcheck.
using System.Runtime.CompilerServices;
using SfModCheck;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SfModCheck.Tests;

static class TestSetup
{
    /// <summary>A run-wide temp folder the caches use until a test points them at its own folder.</summary>
    public static readonly string RunDir = Directory.CreateTempSubdirectory("sfmodcheck-run-").FullName;

    [ModuleInitializer]
    internal static void Init()
    {
        PointCaches(RunDir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(RunDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
    }

    public static void PointCaches(string dir)
    {
        LearnedCache.Root = Path.Combine(dir, "cache");
        EditorIds.CacheDir = Path.Combine(dir, "cache", "edid-cache");
    }
}

/// <summary>A test's own temp folder; the caches point into it while the test runs.</summary>
public class Sandbox : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("sfmodcheck-test-").FullName;

    public Sandbox() => TestSetup.PointCaches(Dir);

    public string Sub(string name) { var p = Path.Combine(Dir, name); Directory.CreateDirectory(p); return p; }

    public virtual void Dispose()
    {
        TestSetup.PointCaches(TestSetup.RunDir);
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class Expect
{
    static string List(Result r) => r.Findings.Count == 0 ? "(no findings)" : string.Join("\n", r.Findings.Select(f => $"  {f.Severity} [{f.Check}] {f.Text}"));

    /// <summary>The result has a finding with this severity and check whose text contains the fragment.</summary>
    public static void Finding(Result r, string severity, string check, string fragment) =>
        Assert.True(r.Findings.Any(f => f.Severity == severity && f.Check == check && f.Text.Contains(fragment)),
            $"wanted {severity} [{check}] ~'{fragment}', got:\n{List(r)}");

    /// <summary>No finding of this check (other findings are fine), and the links were checked.</summary>
    public static void No(Result r, string check)
    {
        Assert.False(r.CannotCheckLinks, "links not checked:\n" + List(r));
        Assert.True(r.Findings.All(f => f.Check != check), $"wanted no [{check}] finding, got:\n{List(r)}");
    }

    /// <summary>No findings at all, links checked, and there were links to check.</summary>
    public static void Clean(Result r)
    {
        Assert.True(r.Findings.Count == 0, "wanted no findings, got:\n" + List(r));
        Assert.False(r.CannotCheckLinks, "links not checked");
        Assert.True(r.Links > 0, "no links were checked");
    }
}
