// A made-up game for one test: three tiny masters with made-up names (no official names, no game files) and a
// clean test plugin built against them, the same shape as the private SelfTest's.
//   TestFull.esm    full master: keywords TF_Keyword and TF_Emotion, global TF_Global, an interior cell with a navmesh
//   TestMedium.esm  medium master on TestFull: keyword TM_Keyword (+ a form list linking TF_Keyword)
//   TestLight.esm   light master on TestFull: keyword TL_Keyword (+ a form list linking TF_Keyword)
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;

namespace SfModCheck.Tests;

public sealed class World : Sandbox
{
    public const string Full = "TestFull.esm", Medium = "TestMedium.esm", Light = "TestLight.esm";
    /// <summary>The test plugin's name inside Mutagen; each written copy is renamed after its test case.</summary>
    public const string PluginName = "SfmTest.esm";
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    public readonly FormKey FullKw, FullEmotion, FullGlob, FullNavm, MediumKw, LightKw;
    public readonly GameData Game;
    public readonly IStarfieldModGetter FullMod, MediumMod, LightMod;
    byte[]? cleanBytes;

    /// <param name="deletedKeyword">mark TestFull's TF_Keyword deleted (for the deleted-target test)</param>
    public World(bool deletedKeyword = false)
    {
        var full = Plugin.New(Full, PluginStyle.Full, "test", "made-up full master");
        var kw = full.Keywords.AddNew("TF_Keyword");
        kw.IsDeleted = deletedKeyword;
        FullKw = kw.FormKey;
        FullEmotion = full.Keywords.AddNew("TF_Emotion").FormKey;
        FullGlob = full.Globals.AddNew("TF_Global").FormKey;
        var cell = new Cell(full) { EditorID = "TF_Cell" };
        var navm = new NavigationMesh(full);
        cell.NavigationMeshes.Add(navm);
        FullNavm = navm.FormKey;
        full.Cells.Add(new CellBlock { SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new ExtendedList<Cell> { cell } } } });
        using (var g = new GameData(Dir)) PluginWriter.Write(full, Dir, g, NoGap);

        using (var g = new GameData(Dir))
        {
            g.Open(Full);
            MediumKw = Dependent(g, Medium, PluginStyle.Medium, "TM_");
            LightKw = Dependent(g, Light, PluginStyle.Light, "TL_");
        }

        Game = new GameData(Dir);
        FullMod = Game.Open(Full);
        MediumMod = Game.Open(Medium);
        LightMod = Game.Open(Light);
    }

    FormKey Dependent(GameData g, string name, PluginStyle style, string prefix)
    {
        var mod = Plugin.New(name, style, "test", "made-up master on " + Full);
        var kw = mod.Keywords.AddNew(prefix + "Keyword");
        mod.FormLists.AddNew(prefix + "List").Items.Add(FullKw.ToLink<IStarfieldMajorRecordGetter>());
        PluginWriter.Write(mod, Dir, g, NoGap);
        return kw.FormKey;
    }

    /// <summary>Builds the clean test plugin, applies one fault, writes it as &lt;file&gt;.esm in the test folder.</summary>
    public string Build(string file, Action<StarfieldMod>? breakIt = null)
    {
        var mod = Plugin.New(PluginName, PluginStyle.Light, "test", "sfmodcheck test plugin");
        var kw = mod.Keywords.AddNew("SFM_Keyword");
        var misc = mod.MiscItems.AddNew("SFM_Misc");
        misc.Keywords = [kw.ToLink<IKeywordGetter>(), FullKw.ToLink<IKeywordGetter>()];
        var fl = mod.FormLists.AddNew("SFM_List");
        fl.Items.Add(FullKw.ToLink<IStarfieldMajorRecordGetter>());
        fl.Items.Add(MediumKw.ToLink<IStarfieldMajorRecordGetter>());
        fl.Items.Add(LightKw.ToLink<IStarfieldMajorRecordGetter>());
        var q = mod.Quests.AddNew("SFM_Quest");
        var topic = new DialogTopic(mod) { EditorID = "SFM_Topic", Quest = q.ToLink<IQuestGetter>() };
        q.DialogTopics.Add(topic);
        (q.Aliases ??= new()).Add(new QuestReferenceAlias { ID = 0, Name = "SFM_Alias" });
        var fl0 = new GetInCurrentLocationFormListConditionData();
        fl0.FirstParameter.Link.SetTo(fl.FormKey);
        var scene = new Scene(mod) { EditorID = "SFM_Scene", Quest = q.ToNullableLink<IQuestGetter>() };
        scene.Actors.Add(new SceneActor { ID = 0 });
        (scene.Actions ??= new()).Add(new DialogueSceneAction { AliasID = 0, Index = 1 });
        scene.Conditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 0 }, ComparisonValue = 1 });
        scene.Conditions.Add(new ConditionFloat { Data = fl0, ComparisonValue = 1 });
        q.Scenes.Add(scene);
        var info = new DialogResponses(mod) { EditorID = "SFM_Info" };
        info.Responses.Add(new DialogResponse { Emotion = FullEmotion.ToLink<IKeywordGetter>(), ResponseText = "hello" });
        topic.Responses.Add(info);
        var navi = mod.NavigationMeshInfoMaps.AddNew("SFM_Navi");
        navi.NavMeshVersion = 15;
        navi.MapInfos.Add(new NavigationMapInfo { NavigationMesh = FullNavm.ToLink<INavigationMeshGetter>(), Data = new byte[20], Unknown = Bytes.Traversal(NavmStored, NavmStored) });
        breakIt?.Invoke(mod);
        var dir = Sub("build-" + file);
        PluginWriter.Write(mod, dir, Game, NoGap);
        var final = Path.Combine(Dir, file + ".esm");
        File.Move(Path.Combine(dir, PluginName), final);
        return final;
    }

    /// <summary>TestFull's navmesh as the test plugin stores it (master index 00 = TestFull).</summary>
    public uint NavmStored => FullNavm.ID;

    /// <summary>The clean plugin's bytes (built once per test).</summary>
    public byte[] CleanBytes => cleanBytes ??= File.ReadAllBytes(Build("clean"));

    /// <summary>A copy of the clean plugin with its bytes changed, written as &lt;name&gt;.esm.</summary>
    public string Patch(string name, Func<byte[], byte[]> change)
    {
        var p = Path.Combine(Dir, name + ".esm");
        File.WriteAllBytes(p, change((byte[])CleanBytes.Clone()));
        return p;
    }

    public Result Check(string path, Action<Options>? configure = null)
    {
        var o = new Options { Plugin = path, DataDir = Dir, MastersDirs = [Dir] };
        configure?.Invoke(o);
        return Checker.Run(o);
    }

    public override void Dispose()
    {
        Game.Dispose();
        base.Dispose();
    }
}
