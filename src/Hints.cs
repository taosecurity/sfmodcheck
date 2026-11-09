// One "how to fix" line per check, in Creation Kit and xEdit terms (step 5a, 2026-11-09). Printed once per check,
// after that check's last finding, as "HINT [check] ...". --no-hints turns them off. Findings never depend on this
// table: it only adds text after them. Every check name the checker can report must have an entry here (a test asserts it).
namespace SfModCheck;

public static class Hints
{
    public static readonly IReadOnlyDictionary<string, string> ByCheck = new Dictionary<string, string>
    {
        ["structure"] =
            "The file itself is damaged (or a tool wrote it wrongly). Open it in xEdit: if it loads, save it again from " +
            "xEdit or the Creation Kit; if it doesn't, rebuild it from your CK project, a backup or your builder.",
        ["header"] =
            "xEdit: select the File Header record. Missing master: install it, or remove it with Clean Masters (only " +
            "when nothing uses it). Master order: right-click > Sort Masters. Flags: an .esm needs the ESM (Master) " +
            "flag, and only one of Light (ESL) or Medium. Next Object ID: edit HEDR > Next Object ID in the File Header so " +
            "it is above the plugin's highest own FormID.",
        ["ids"] =
            "Own FormIDs must fit the plugin type (light 000-FFF, medium 0000-FFFF). xEdit: Compact FormIDs for ESL / " +
            "for Medium, or take the Light or Medium flag off. A master index past the master list is a broken " +
            "FormID: add the missing master in xEdit, or rebuild the record.",
        ["read"] =
            "Mutagen (the library sfmodcheck reads plugins with) can't read this record, so the game may not either. " +
            "Look at the named record in xEdit for errors in red, fields from another game or a wrong field size, " +
            "then fix the field or make the record again in the CK.",
        ["records"] =
            "Deleted records can crash the game when the game or another mod still uses them. xEdit: on placed " +
            "references (REFR, ACHR) use right-click > Undelete and Disable References. For other types, remove " +
            "the deleting override in xEdit (right-click > Remove) unless you really mean it. In the CK, disable " +
            "instead of deleting vanilla objects.",
        ["links"] =
            "A field points at a record that doesn't exist, is the wrong type or is deleted. xEdit: open the named " +
            "record; the field shows the FormID, often in red. Point it at an existing record of the right type, or " +
            "clear it. 'Doesn't exist' often means a master is missing or a master record was removed. CK: re-pick " +
            "the item in that form's drop-down and save. 'Injected record' means the plugin adds a record in a " +
            "master's FormID range: fine only if you meant it.",
        ["navi"] =
            "The navmesh info map (NAVI) points at a navmesh that doesn't exist, isn't a navmesh or is deleted. CK: " +
            "load the cell, select its navmesh and Finalize it, then save. If the plugin deletes a navmesh that others " +
            "link to, restore it instead. xEdit can't safely fix navmesh data by hand.",
        ["conditions"] =
            "A condition leaves its form parameter empty, where the game's own uses of that function always fill it. " +
            "CK: open the record's condition list, edit the condition and pick the form (or remove the condition). " +
            "xEdit: fill in the condition's Parameter #1/#2 field.",
        ["dialogue"] =
            "A dialogue response has an empty Emotion. CK: open the topic info, edit the response and pick an " +
            "emotion (Neutral is fine). xEdit: set the response's TRDA Emotion to a real value.",
        ["scenes"] =
            "A scene uses a quest alias its quest doesn't have, or has no quest. CK: open the scene's quest, check " +
            "the Quest Aliases tab, then fix each scene actor, action and condition that names the alias (or set " +
            "the scene's quest). xEdit: compare the scene's alias IDs with the quest's alias list.",
        ["editorids"] =
            "Give each new record its own Editor ID that no master uses either. CK: open the form and change its " +
            "ID. xEdit: edit the EDID field. This usually comes from duplicating a record or Copy as New Record.",
        ["itm"] =
            "Identical to Master (ITM) records change nothing but can undo other mods' edits to the same record. " +
            "xEdit: right-click the plugin > Remove \"Identical to Master\" records (or run QuickAutoClean). In the " +
            "CK they come from opening a form and pressing OK without changes.",
    };

    /// <summary>The hint for a check, or null if none (never expected: a test checks that every check has one).</summary>
    public static string? For(string check) => ByCheck.TryGetValue(check, out var h) ? h : null;
}
