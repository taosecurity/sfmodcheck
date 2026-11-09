// The few cases that need the game's own Starfield.esm: the engine-made records (the list is keyed to that file name)
// and the official scene list (learned only from official masters). Skipped when Starfield.esm isn't there.
// SFMODCHECK_TEST_DATA = the Data folder to use (default: the Steam one). No test checks a whole official master.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public sealed class NeedsStarfieldFactAttribute : FactAttribute
{
    public static string Data => Environment.GetEnvironmentVariable("SFMODCHECK_TEST_DATA") is { Length: > 0 } d
        ? d : @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";

    public NeedsStarfieldFactAttribute()
    {
        if (!File.Exists(Path.Combine(Data, "Starfield.esm")))
            Skip = $"needs Starfield.esm: not found in {Data} (set SFMODCHECK_TEST_DATA to the game's Data folder)";
    }
}

public class NeedsStarfieldTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };
    static readonly FormKey QuirkScene = FormKey.Factory("28B66E:Starfield.esm");   // MQ104A_011_LodgeReturn

    /// <summary>A light test plugin on Starfield.esm, written to &lt;sandbox&gt;\&lt;file&gt;.esm.</summary>
    static string Build(Sandbox s, GameData game, string file, Action<StarfieldMod> fill)
    {
        var mod = Plugin.New("SfmTest.esm", PluginStyle.Light, "test", "sfmodcheck test plugin");
        fill(mod);
        var dir = s.Sub("build-" + file);
        PluginWriter.Write(mod, dir, game, NoGap);
        var final = Path.Combine(s.Dir, file + ".esm");
        File.Move(Path.Combine(dir, "SfmTest.esm"), final);
        return final;
    }

    static Result Check(Sandbox s, string plugin, string data, params string[] mastersDirs) =>
        Checker.Run(new Options { Plugin = plugin, DataDir = data, MastersDirs = [s.Dir, .. mastersDirs], UseBuiltInSceneQuirks = false });

    static string Sf(uint id) => $"{id:X6}:Starfield.esm";

    [NeedsStarfieldFact]
    public void Links_to_engine_made_records_are_fine()
    {
        using var s = new Sandbox();
        using var game = new GameData(NeedsStarfieldFactAttribute.Data);
        var sf = game.Open("Starfield.esm");
        var p = Build(s, game, "engine", m =>
        {
            var fl = m.FormLists.AddNew("SFM_List");
            fl.Items.Add(sf.Keywords.First().FormKey.ToLink<IStarfieldMajorRecordGetter>());
            fl.Items.Add(FormKey.Factory(Sf(0x14)).ToLink<IStarfieldMajorRecordGetter>());    // PlayerRef
            fl.Items.Add(FormKey.Factory(Sf(0x38A)).ToLink<IStarfieldMajorRecordGetter>());   // an actor value
        });
        var r = Check(s, p, NeedsStarfieldFactAttribute.Data);
        Assert.False(r.Has("ERROR", "links"), string.Join("\n", r.Findings.Select(f => f.Text)));
        Assert.False(r.CannotCheckLinks);
        Assert.Equal(3, r.Links);
    }

    [NeedsStarfieldFact]
    public void Engine_actor_value_where_a_keyword_belongs()
    {
        using var s = new Sandbox();
        using var game = new GameData(NeedsStarfieldFactAttribute.Data);
        game.Open("Starfield.esm");
        var p = Build(s, game, "enginetype", m =>
            m.MiscItems.AddNew("SFM_Misc").Keywords = [FormKey.Factory(Sf(0x38A)).ToLink<IKeywordGetter>()]);
        Expect.Finding(Check(s, p, NeedsStarfieldFactAttribute.Data), "ERROR", "links", "a AVIF, where it expects Keyword");
    }

    /// <summary>An override of an official scene under an alias-less override of its quest.</summary>
    static string SceneOverride(Sandbox s, GameData game, string file, ISceneGetter scene) => Build(s, game, file, m =>
    {
        var q = new Quest(scene.Quest.FormKey, StarfieldRelease.Starfield) { EditorID = "SFM_QuestOverride" };
        q.Scenes.Add((Scene)scene.DeepCopy());
        m.Quests.Add(q);
    });

    [NeedsStarfieldFact]
    public void Official_quirk_scene_is_skipped_by_the_learned_list()
    {
        using var s = new Sandbox();
        using var game = new GameData(NeedsStarfieldFactAttribute.Data);
        game.Open("Starfield.esm");
        var p = SceneOverride(s, game, "officialscene", game.LinkCache.Resolve<ISceneGetter>(QuirkScene));
        var r = Check(s, p, NeedsStarfieldFactAttribute.Data);   // learns the list into the sandbox's cache folder
        Expect.No(r, "scenes");
        Assert.Equal(0, r.ScenesNotChecked);
        Assert.True(Directory.EnumerateFiles(LearnedCache.SceneDir, "scene-alias-quirks.*.txt").Any(), "the learned list wasn't cached in the sandbox");
    }

    [NeedsStarfieldFact]
    public void Ordinary_official_scene_is_still_checked()
    {
        using var s = new Sandbox();
        using var game = new GameData(NeedsStarfieldFactAttribute.Data);
        var sf = game.Open("Starfield.esm");
        var plain = sf.EnumerateMajorRecords<ISceneGetter>().First(sc => sc.FormKey.ModKey == sf.ModKey && sc.FormKey != QuirkScene && !sc.Quest.IsNull && sc.Actors.Any(a => (int)a.ID >= 0));
        var r = Check(s, SceneOverride(s, game, "officialscene2", plain), NeedsStarfieldFactAttribute.Data);
        Assert.True(r.Has("ERROR", "scenes"), "wanted a scenes ERROR: " + string.Join(" | ", r.Findings.Select(f => f.Text)));
        Assert.Equal(0, r.ScenesNotChecked);
    }

    [NeedsStarfieldFact]
    public void Official_scene_without_official_masters_in_data_is_reported_not_checked()
    {
        using var s = new Sandbox();
        using var game = new GameData(NeedsStarfieldFactAttribute.Data);
        game.Open("Starfield.esm");
        var p = SceneOverride(s, game, "officialscene3", game.LinkCache.Resolve<ISceneGetter>(QuirkScene));
        var r = Check(s, p, s.Sub("empty-data"), NeedsStarfieldFactAttribute.Data);   // masters found through MastersDirs
        Assert.Equal(1, r.ScenesNotChecked);
        Assert.Contains("Starfield.esm not in the Data folder", r.ScenesNotCheckedWhy);
        Expect.No(r, "scenes");
    }
}
