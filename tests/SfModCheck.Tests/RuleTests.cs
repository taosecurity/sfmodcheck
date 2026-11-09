// One test per rule: a copy of the clean test plugin with one fault, and the exact finding it must give.
using System.Buffers.Binary;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Xunit;
using static SfModCheck.Tests.Bytes;

namespace SfModCheck.Tests;

public class RuleTests
{
    // masters: TestFull (full), TestMedium (medium), TestLight (light) -> the plugin itself is full index 1
    const uint SelfIndex = 1;

    [Fact]
    public void Clean_plugin_has_no_findings()
    {
        using var w = new World();
        Expect.Clean(w.Check(w.Build("clean")));
    }

    // ---- links ----

    [Fact]
    public void Link_to_missing_record_in_full_master()
    {
        using var w = new World();
        var p = w.Build("missing", m => m.MiscItems.First().Keywords!.Add(new FormKey(w.FullKw.ModKey, 0xFFFFF0).ToLink<IKeywordGetter>()));
        Expect.Finding(w.Check(p), "ERROR", "links", "FFFFF0:TestFull.esm, which doesn't exist");
    }

    [Fact]
    public void Link_to_missing_record_in_medium_master()
    {
        using var w = new World();
        var p = w.Build("medmissing", m => m.FormLists.First().Items.Add(new FormKey(w.MediumKw.ModKey, 0xFFF0).ToLink<IStarfieldMajorRecordGetter>()));
        Expect.Finding(w.Check(p), "ERROR", "links", "00FFF0:TestMedium.esm, which doesn't exist");
    }

    [Fact]
    public void Link_to_missing_record_in_light_master()
    {
        using var w = new World();
        var p = w.Build("lightmissing", m => m.FormLists.First().Items.Add(new FormKey(w.LightKw.ModKey, 0xFF0).ToLink<IStarfieldMajorRecordGetter>()));
        Expect.Finding(w.Check(p), "ERROR", "links", "000FF0:TestLight.esm, which doesn't exist");
    }

    [Fact]
    public void Link_to_missing_own_record()
    {
        using var w = new World();
        var p = w.Build("ownmissing", m => m.FormLists.First().Items.Add(new FormKey(m.ModKey, 0xFF0).ToLink<IStarfieldMajorRecordGetter>()));
        Expect.Finding(w.Check(p), "ERROR", "links", "000FF0:ownmissing.esm, which doesn't exist");   // each copy is named after its case
    }

    [Fact]
    public void Link_to_wrong_record_type()
    {
        using var w = new World();
        var p = w.Build("wrongtype", m => m.MiscItems.First().Keywords!.Add(w.FullGlob.ToLink<IKeywordGetter>()));
        Expect.Finding(w.Check(p), "ERROR", "links", "a GLOB, where it expects Keyword");
    }

    [Fact]
    public void Link_to_deleted_master_record()
    {
        using var w = new World(deletedKeyword: true);
        Expect.Finding(w.Check(w.Build("deletedtarget")), "WARNING", "links", $"links to {w.FullKw} (KYWD), which is deleted");
    }

    [Fact]
    public void Override_of_record_the_master_lacks_is_injected()
    {
        using var w = new World();
        var p = w.Build("injected", m => m.Keywords.Add(new Keyword(new FormKey(w.FullKw.ModKey, 0xFFFFF1), StarfieldRelease.Starfield) { EditorID = "SFM_Injected" }));
        Expect.Finding(w.Check(p), "WARNING", "links", "injected record");
    }

    [Fact]
    public void Override_that_changes_record_type()
    {
        using var w = new World();
        var p = w.Build("overridetype", m => m.Keywords.Add(new Keyword(w.FullGlob, StarfieldRelease.Starfield) { EditorID = "SFM_GlobAsKeyword" }));
        Expect.Finding(w.Check(p), "ERROR", "links", "which is a GLOB in TestFull.esm");
    }

    // ---- ids ----

    static Item Kw(World w) => Items(w.CleanBytes).First(i => i.Sig == "KYWD");
    static Item Misc(World w) => Items(w.CleanBytes).First(i => i.Sig == "MISC");

    [Fact]
    public void Own_id_past_light_range()
    {
        using var w = new World();
        var kw = Kw(w);
        var p = w.Patch("lightrange", b => { SetU32(b, kw.Off + 12, (SelfIndex << 24) | 0x1000); return b; });
        Expect.Finding(w.Check(p), "ERROR", "ids", "outside the light range");
    }

    [Fact]
    public void Own_id_past_medium_range()
    {
        using var w = new World();
        var kw = Kw(w);
        var p = w.Patch("mediumrange", b =>
        {
            SetU32(b, 8, (U32(b, 8) & ~Flags.Light) | Flags.Medium);
            SetU32(b, kw.Off + 12, (SelfIndex << 24) | 0x10000);
            return b;
        });
        Expect.Finding(w.Check(p), "ERROR", "ids", "outside the medium range");
    }

    [Fact]
    public void Master_index_past_the_list()
    {
        using var w = new World();
        var kw = Kw(w);
        var p = w.Patch("badindex", b => { SetU32(b, kw.Off + 12, 0x05000800); return b; });
        Expect.Finding(w.Check(p), "ERROR", "ids", "master index past the end");
    }

    [Fact]
    public void Duplicate_form_id()
    {
        using var w = new World();
        var kw = Kw(w);
        var misc = Misc(w);
        var p = w.Patch("dupid", b => { SetU32(b, misc.Off + 12, U32(b, kw.Off + 12)); return b; });
        Expect.Finding(w.Check(p), "ERROR", "ids", "duplicate FormID");
    }

    // ---- header ----

    static int MasterField(List<(string Type, byte[] Data)> f, string name) =>
        f.FindIndex(x => x.Type == "MAST" && Encoding.UTF8.GetString(x.Data).TrimEnd('\0') == name);

    [Fact]
    public void Missing_master()
    {
        using var w = new World();
        var p = w.Patch("nomaster", b => RewriteHeader(b, f => f[MasterField(f, World.Light)] = ("MAST", Encoding.UTF8.GetBytes("NotInstalled.esm\0"))));
        Expect.Finding(w.Check(p), "ERROR", "header", "master NotInstalled.esm is not installed");
    }

    [Fact]
    public void Master_listed_twice()
    {
        using var w = new World();
        var p = w.Patch("dupmaster", b => RewriteHeader(b, f =>
        {
            var i = f.FindIndex(x => x.Type == "MAST");
            f.InsertRange(f.FindLastIndex(x => x.Type == "DATA") + 1, f.GetRange(i, 2));
        }));
        Expect.Finding(w.Check(p), "ERROR", "header", "master TestFull.esm is listed twice");
    }

    [Fact]
    public void Master_before_its_own_master()
    {
        using var w = new World();
        var p = w.Patch("masterorder", b => RewriteHeader(b, f =>
        {
            var i = MasterField(f, World.Medium);
            var pair = f.GetRange(i, 2);   // MAST + DATA
            f.RemoveRange(i, 2);
            f.InsertRange(f.FindIndex(x => x.Type == "MAST"), pair);
        }));
        Expect.Finding(w.Check(p), "WARNING", "header", "master TestMedium.esm comes before its own master TestFull.esm");
    }

    [Fact]
    public void Esm_without_master_flag()
    {
        using var w = new World();
        var p = w.Patch("nomasterflag", b => { SetU32(b, 8, U32(b, 8) & ~Flags.Master); return b; });
        Expect.Finding(w.Check(p), "WARNING", "header", ".esm file without the Master flag");
    }

    [Fact]
    public void Light_and_medium_flags()
    {
        using var w = new World();
        var p = w.Patch("lightmedium", b => { SetU32(b, 8, U32(b, 8) | Flags.Medium); return b; });
        Expect.Finding(w.Check(p), "ERROR", "header", "both the Light and the Medium flag");
    }

    [Fact]
    public void No_hedr()
    {
        using var w = new World();
        var p = w.Patch("nohedr", b => RewriteHeader(b, f => f.RemoveAll(x => x.Type == "HEDR")));
        Expect.Finding(w.Check(p), "ERROR", "header", "no HEDR");
    }

    [Fact]
    public void Next_form_id_not_above_own_records()
    {
        using var w = new World();
        var p = w.Patch("nextid", b => RewriteHeader(b, f =>
        {
            var i = f.FindIndex(x => x.Type == "HEDR");
            var d = f[i].Data.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), 0x801);
            f[i] = ("HEDR", d);
        }));
        Expect.Finding(w.Check(p), "WARNING", "header", "next FormID 000801");
    }

    // ---- structure (byte level) ----

    [Fact]
    public void Not_a_plugin()
    {
        using var w = new World();
        Expect.Finding(w.Check(w.Patch("notes4", b => { b[0] = (byte)'X'; return b; })), "ERROR", "structure", "TES4");
    }

    [Fact]
    public void Record_size_runs_past_its_group()
    {
        using var w = new World();
        var kw = Kw(w);
        Expect.Finding(w.Check(w.Patch("recsize", b => { SetU32(b, kw.Off + 4, U32(b, kw.Off + 4) + 100); return b; })), "ERROR", "structure", "runs 100 bytes past");
    }

    [Fact]
    public void Field_size_runs_past_its_record()
    {
        using var w = new World();
        var o = (int)Kw(w).Off + 24 + 4;   // the first field's size
        Expect.Finding(w.Check(w.Patch("fieldsize", b => { SetU16(b, o, (ushort)(U16(b, o) + 50)); return b; })), "ERROR", "structure", "runs 50 bytes past the record end");
    }

    [Fact]
    public void Group_size_runs_past_the_file()
    {
        using var w = new World();
        var g = Items(w.CleanBytes).First(i => i.Group);
        Expect.Finding(w.Check(w.Patch("groupsize", b => { SetU32(b, g.Off + 4, (uint)b.Length); return b; })), "ERROR", "structure", "group size");
    }

    [Fact]
    public void Stray_bytes_at_the_end()
    {
        using var w = new World();
        Expect.Finding(w.Check(w.Patch("stray", b => b.Concat(new byte[10]).ToArray())), "ERROR", "structure", "10 stray bytes");
    }

    [Fact]
    public void Record_in_the_wrong_top_group()
    {
        using var w = new World();
        var kw = Kw(w);
        Expect.Finding(w.Check(w.Patch("wronggroup", b => { Encoding.ASCII.GetBytes("GLOB").CopyTo(b, kw.Off); return b; })), "ERROR", "structure", "is in the KYWD group");
    }

    [Fact]
    public void Unknown_group_type()
    {
        using var w = new World();
        var kids = Items(w.CleanBytes).First(i => i.Group && i.GroupType == 10);
        Expect.Finding(w.Check(w.Patch("grouptype", b => { SetU32(b, kids.Off + 12, 99); return b; })), "ERROR", "structure", "unknown group type 99");
    }

    [Fact]
    public void Quest_children_group_that_does_not_follow_its_quest()
    {
        using var w = new World();
        var kids = Items(w.CleanBytes).First(i => i.Group && i.GroupType == 10);
        Expect.Finding(w.Check(w.Patch("childlabel", b => { SetU32(b, kids.Off + 8, U32(b, kids.Off + 8) + 7); return b; })), "ERROR", "structure", "QUST children group");
    }

    [Fact]
    public void Compressed_flag_on_data_that_is_not_zlib()
    {
        using var w = new World();
        var misc = Misc(w);
        Expect.Finding(w.Check(w.Patch("badzlib", b => { SetU32(b, misc.Off + 8, U32(b, misc.Off + 8) | Flags.Compressed); return b; })), "ERROR", "structure", "is compressed but");
    }

    [Fact]
    public void Compressed_record_with_wrong_stored_size()
    {
        using var w = new World();
        var misc = Misc(w);
        Expect.Finding(w.Check(w.Patch("zlibsize", b => Compress(b, misc, sizeDelta: 3))), "ERROR", "structure", "header says");
    }

    [Fact]
    public void Properly_compressed_record_is_clean()
    {
        using var w = new World();
        var misc = Misc(w);
        Expect.Clean(w.Check(w.Patch("zlibgood", b => Compress(b, misc, sizeDelta: 0))));
    }

    // ---- content rules ----

    [Fact]
    public void Condition_with_empty_form_list_parameter()
    {
        using var w = new World();
        var p = w.Build("emptyparam", m => m.Quests.First().Scenes.First().Conditions.Add(new ConditionFloat { Data = new GetInCurrentLocationFormListConditionData(), ComparisonValue = 1 }));
        Expect.Finding(w.Check(p), "WARNING", "conditions", "GetInCurrentLocationFormList has an empty FormList parameter 1");
    }

    [Fact]
    public void Response_with_emotion_zero()
    {
        using var w = new World();
        var p = w.Build("emotion0", m => m.Quests.First().DialogTopics.First().Responses.First().Responses.First().Emotion.Clear());
        Expect.Finding(w.Check(p), "WARNING", "dialogue", "has Emotion 00000000");
    }

    [Fact]
    public void Scene_actor_alias_the_quest_lacks()
    {
        using var w = new World();
        var p = w.Build("actoralias", m => m.Quests.First().Scenes.First().Actors.Add(new SceneActor { ID = 29 }));
        Expect.Finding(w.Check(p), "ERROR", "scenes", "actor slot uses alias 29");
    }

    [Fact]
    public void Scene_action_dtgt_and_alias_condition_the_quest_lacks()
    {
        using var w = new World();
        var p = w.Build("actionalias", m =>
        {
            var s = m.Quests.First().Scenes.First();
            s.Actions!.Add(new PlayerDialogueSceneAction { AliasID = 7, Index = 2, DialogueTargetActor = 8 });
            s.Conditions.Add(new ConditionFloat { Data = new GetIsAliasRefConditionData { FirstParameter = 9 }, ComparisonValue = 1 });
        });
        var r = w.Check(p);
        Expect.Finding(r, "ERROR", "scenes", "uses alias 7");
        Expect.Finding(r, "ERROR", "scenes", "targets (DTGT) alias 8");
        Expect.Finding(r, "WARNING", "scenes", "tests alias 9");
    }

    [Fact]
    public void Scene_with_no_parent_quest()
    {
        using var w = new World();
        var p = w.Build("nopnam", m => m.Quests.First().Scenes.First().Quest.Clear());
        Expect.Finding(w.Check(p), "WARNING", "scenes", "has no parent quest (PNAM)");
    }

    [Fact]
    public void Navi_traversal_to_missing_navmesh()
    {
        using var w = new World();
        var p = w.Build("navi", m => m.NavigationMeshInfoMaps.First().MapInfos.First().Unknown = Traversal(w.NavmStored, 0x00FFFFF0));
        Expect.Finding(w.Check(p), "ERROR", "navi", "traversal to FFFFF0:testfull.esm doesn't exist");
    }

    [Fact]
    public void Navi_traversal_between_existing_navmeshes_is_fine()
    {
        using var w = new World();
        Expect.No(w.Check(w.Build("navigood")), "navi");
    }

    [Fact]
    public void Editor_id_used_by_two_new_records()
    {
        using var w = new World();
        var p = w.Build("dupedid", m => m.Keywords.AddNew("SFM_Keyword"));
        Expect.Finding(w.Check(p), "WARNING", "editorids", "EditorID SFM_Keyword is used by 2 new records");
    }

    [Fact]
    public void Editor_id_that_a_master_already_uses()
    {
        using var w = new World();
        var p = w.Build("masteredid", m => m.Keywords.AddNew("TF_Keyword"));
        Expect.Finding(w.Check(p), "WARNING", "editorids", "new record with EditorID TF_Keyword, which TestFull.esm already uses");
    }

    [Fact]
    public void Deleted_own_records_are_counted()
    {
        using var w = new World();
        var p = w.Build("deleted", m => m.Keywords.First().IsDeleted = true);
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "records", "1 deleted KYWD record(s)");
        Expect.Finding(r, "WARNING", "links", "which is deleted");   // the misc item still links to it
    }

    [Fact]
    public void Override_identical_to_master_is_itm()
    {
        using var w = new World();
        var master = w.FullMod.Keywords.First(k => k.EditorID == "TF_Keyword");
        var r = w.Check(w.Build("itm", m => m.Keywords.Add((Keyword)master.DeepCopy())));
        Expect.Finding(r, "WARNING", "itm", $"[KYWD:{master.FormKey}] is identical to the master version");
        Assert.Equal(1, r.Itms);
    }

    [Fact]
    public void Override_with_one_field_changed_is_not_itm()
    {
        using var w = new World();
        var master = w.FullMod.Keywords.First(k => k.EditorID == "TF_Keyword");
        var r = w.Check(w.Build("notitm", m =>
        {
            var k = (Keyword)master.DeepCopy();
            k.EditorID += "_Changed";
            m.Keywords.Add(k);
        }));
        Expect.No(r, "itm");
        Assert.Equal(1, r.Overrides);
    }
}
