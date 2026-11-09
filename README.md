# sfmodcheck

**Find out whether your Starfield mod's plugin is broken before you play it or release it.** sfmodcheck reads a
plugin (`.esm`) and reports problems that would break it in the game: damaged files, bad headers, FormIDs outside the
plugin's range, links to records that don't exist, broken conditions, scenes and navmesh links, repeated EditorIDs,
deleted records and "identical to master" records. After each kind of problem it says how to fix it in xEdit or the
Creation Kit. It only reads files: it never changes a plugin. A typical mod takes about 4 seconds.

**First command:** download the exe zip from [Releases](https://github.com/taosecurity/sfmodcheck/releases), unzip it,
and drag `MyMod.esm` onto `sfmodcheck.exe`. Or, from a prompt:

    sfmodcheck check MyMod.esm

For AI assistants: step-by-step install and use instructions are in [AGENTS.md](AGENTS.md).

It does the same kind of job as part of xEdit's "Check for Errors", but from the command line, so it fits a build
script or a release checklist, and it gives each problem one line that says what is wrong and where. It is built on
[Mutagen](https://github.com/Mutagen-Modding/Mutagen) 0.54.4.

Version 0.4.0, the first public release. It has been used on its author's own mods since 2026-11-07; reports from other
mods are welcome (see [Issues](https://github.com/taosecurity/sfmodcheck/issues)).

## Quick start (Creation Kit and xEdit authors)

1. Download `sfmodcheck-<version>-win-x64.zip` from the [Releases](https://github.com/taosecurity/sfmodcheck/releases)
   page and unzip it anywhere. It holds one `sfmodcheck.exe` with .NET built in, so nothing else needs installing.
2. Drag your plugin (`MyMod.esm`) onto `sfmodcheck.exe`. A window opens, prints the result and stays open until you
   press Enter.
3. Read the lines that start with ERROR (the mod is broken) and WARNING (probably a mistake). After each kind of
   problem, a HINT line says how to fix it in xEdit or the Creation Kit. The last line says OK or FAILED.

sfmodcheck looks for the game's masters (Starfield.esm and the others) in Steam's usual Data folder,
`C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data`. If your game is somewhere else (Game Pass, a Steam
library on another drive), either drop a plugin that sits in the game's own Data folder (sfmodcheck then uses that
folder and says so), or run it from a command prompt with `--data`:

    sfmodcheck.exe check MyMod.esm --data "D:\XboxGames\Starfield\Content\Data"

Masters that aren't in the Data folder are also looked for in the plugin's own folder. See
[Where your game's Data folder is](#where-your-games-data-folder-is) for Game Pass, Vortex and Mod Organizer 2.

## Where your game's Data folder is

sfmodcheck needs the game's masters (Starfield.esm, the DLC and Creations masters your plugin lists). It reads them
from the game's Data folder and from the folders given with `--masters-dir`, and never changes them.

- **Steam:** `C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data` is the default, so nothing is needed. A
  Steam library on another drive: `--data "E:\SteamLibrary\steamapps\common\Starfield\Data"`.
- **Game Pass (Xbox app):** the game usually sits in `<drive>:\XboxGames\Starfield\Content`, so use
  `--data "C:\XboxGames\Starfield\Content\Data"` (change the drive letter to the drive the game is installed on).
- **Vortex:** Vortex deploys mods into the game's own Data folder, so the Steam or Game Pass line above applies. To
  check a plugin before deploying it, give its path in Vortex's staging folder and keep `--data` pointing at the
  game's Data folder.
- **Mod Organizer 2:** MO2 keeps each mod in its own folder (`mods\<ModName>\`) and leaves the game's Data folder
  alone. Give the plugin's path inside `mods\`, `--data` the game's real Data folder, and `--masters-dir` the folder of
  any mod whose plugin is one of your masters (once per folder):

      sfmodcheck check "C:\Modding\MO2\mods\MyMod\MyMod.esm" --data "C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data" --masters-dir "C:\Modding\MO2\mods\MainMod"

  (Programs started from MO2's own Run menu see the mods through its virtual Data folder, so adding sfmodcheck there
  as an executable with the arguments `check <plugin>` should also work; we haven't tried it.)

Without `--data`, when Steam's folder has no Starfield.esm, a plugin that sits in a folder holding Starfield.esm uses
that folder (the output says so). When a plugin needs Starfield.esm and none is found, a note on stderr says to use
`--data`.

## Install

Two ways, both from the [Releases](https://github.com/taosecurity/sfmodcheck/releases) page. Each file there has a
`.sha256` file next to it with its SHA-256 checksum.

- **Stand-alone exe** (Windows, no .NET needed): `sfmodcheck-<version>-win-x64.zip`. Unzip it and run `sfmodcheck.exe`,
  or drop plugins on it.
- **dotnet tool** (Windows or Linux, needs the [.NET SDK](https://dotnet.microsoft.com/download) 9 or later):
  download `sfmodcheck.<version>.nupkg` into a folder, then

      dotnet tool install --global sfmodcheck --add-source <that folder> --version <version>

  After that, `sfmodcheck` works from any prompt. `dotnet tool update --global sfmodcheck --add-source <folder>`
  installs a newer one, and `dotnet tool uninstall --global sfmodcheck` removes it.

## Run it

    sfmodcheck check <plugin> [--data <Data folder>] [--masters-dir <folder>]... [--max <lines per check>] [--quiet] [--no-hints]
                              [--verbose] [--log <file>]
    sfmodcheck --version

- `--data`: the game's Data folder (default: Steam's usual one, see above).
- `--masters-dir`: a folder searched for masters before the Data folder; can be given more than once. Use it so an
  add-on is checked against the main plugin you just built, not an older copy installed in Data.
- `--max`: the most lines printed per check (default 100). The counts in the RESULT line are always complete.
- `--quiet`: leave out the first summary line.
- `--no-hints`: leave out the HINT lines.
- `--verbose`: print the troubleshooting log on stderr (lines start with `log: `). See below.
- `--log <file>`: write the troubleshooting log to that file. See below.

`sfmodcheck <plugin>` without the word `check` does the same.

A wrong argument (a missing value, `--max` with something that isn't a number, a plugin or Data folder that doesn't
exist, a log file it can't write) stops with exit code 2 and a message that names the file or option and what to do.

### Troubleshooting log

When something goes wrong, or a result looks odd, run the same check again with a log:

    sfmodcheck check MyMod.esm --log sfmodcheck-log.txt

The log goes only where you ask: `--log <file>` writes it to that file (a new file each run, in the current folder
unless you give a full path), and `--verbose` prints the same lines on stderr instead. Nothing is logged without one
of them. The findings and the RESULT line are the same with or without the log.

What it records, with the time of each line: the sfmodcheck, Windows (or Linux) and .NET versions; the arguments; the
plugin's path, size and date and its header (light, medium or full; its masters); which Data folder was used and why
(`--data`, Steam's usual folder, or the plugin's own folder); for each master, where it was found (or every place
looked when it wasn't); each cache read, rebuilt or not writable, with its path; how long each stage took; the number
of findings per check; and, when sfmodcheck itself fails, the full error with its stack trace.

The log names folders on your PC (your user name may be in them). Read it before you attach it to an
[issue](https://github.com/taosecurity/sfmodcheck/issues).

For a dropped plugin (drag and drop), open a command prompt in the exe's folder and run the command above with the
plugin's full path.

### What the output looks like

    sfmodcheck 0.4.0 check: MyMod.esm (light master, 1 masters, 18 records, 2 overrides (1 ITM), 44 links)
    ERROR [links] MyMod_Misc [MISC:000801:MyMod.esm] links to 000FF0:MyMod.esm, which doesn't exist (expects Keyword)
    HINT [links] A field points at a record that doesn't exist, ...
    WARNING [itm] SomeKeyword [KYWD:0A1B2C:Starfield.esm] is identical to the master version it overrides (ITM)
    HINT [itm] Identical to Master (ITM) records change nothing but can undo other mods' edits ...
    RESULT: FAILED, 1 errors, 1 warnings (MyMod.esm: 18 records, 44 links; 3.9 s)

- `ERROR [check] ...`: the plugin is broken; fix it before release.
- `WARNING [check] ...`: probably a mistake, or a risk; look at it.
- `HINT [check] ...`: how to fix that check's findings, once per check (not part of the result).
- `RESULT: OK|FAILED, <n> errors, <n> warnings (...)`: always the last line. It adds "links not checked" when a
  master is missing, and "official scenes not checked" when the scene list couldn't be learned (see the scenes check).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | no errors (warnings are allowed) |
| 1 | errors |
| 2 | couldn't check: bad arguments, a missing file, or a file it couldn't open (the RESULT line then says `CANNOT CHECK`) |

Scripts should read the exit code and the lines that start with `ERROR `, `WARNING ` and `RESULT: `. Other lines
(the summary, HINT lines, notes on stderr) may change between versions.

## What it checks

Each finding names its check in brackets.

- **structure**: the raw file layout: groups where the official masters put them, sizes that add up, and compressed
  records that really are zlib data of the stored size.
- **header**: the file header (TES4). HEDR is there and 12 bytes; not both the Light and the Medium flag; an .esm
  without the Master flag (WARNING); masters missing, listed twice, or the plugin listing itself (ERROR); a master
  listed before its own master (WARNING); a Next Object ID lower than the IDs in use (WARNING; the Creation Kit's
  800/0 placeholder is fine).
- **ids**: FormIDs. A master index past the end of the master list, the plugin's own IDs past the light (FFF) or
  medium (FFFF) range, and duplicate records.
- **read**: records Mutagen can't read.
- **links**: every link points to a record that exists in the plugin or its masters, of a type that field can hold.
  A link to a deleted record is a WARNING; an override of a record no master has is a WARNING ("injected"); an
  override that changes the record's type is an ERROR. Records the game's engine creates itself (such as PlayerRef)
  count as existing.
- **conditions**: a condition whose form parameter is empty where the official masters always fill it for that
  function (WARNING: the condition tests nothing).
- **dialogue**: a response with Emotion 0, where the official masters use "none" or an emotion keyword (WARNING).
- **scenes**: the quest aliases a scene uses must exist in its quest. Actors, actions and dialogue targets that name a
  missing alias are ERRORs; alias conditions that do are WARNINGs; a scene with no quest is a WARNING because it can't
  run. A few dozen official scenes break this rule on purpose. sfmodcheck learns which ones from the official masters
  in your Data folder the first time it meets an official scene (about 3 seconds, once), and skips them.
- **navi**: the navmesh traversal entries in the navmesh info map (NAVI) point to navmeshes that exist and aren't
  deleted.
- **editorids**: an EditorID used by two or more new records, or a new record whose EditorID a master already uses
  (WARNINGs). Scripts, the console and tools find records by EditorID, so they find only one of them.
- **itm**: an override identical to the master version it overrides ("identical to master", ITM), one WARNING each,
  and the count in the summary line. Fields are compared by value, so field order and unread padding don't count. A
  parent kept only because it holds the plugin's child records (a cell with placed references, a quest with dialogue)
  isn't reported, the same as xEdit's "Remove ITM". Bethesda's own plugins get the count only.
- **records**: deleted records, one WARNING per record type with the count and a few IDs. Bethesda's masters delete
  records too, so it's a WARNING, not an ERROR; the risk is another mod or the game still using a deleted record.

## For Creation Kit and xEdit authors

You don't need a build script: drop a plugin on `sfmodcheck.exe` (see [Quick start](#quick-start-creation-kit-and-xedit-authors)),
or run `sfmodcheck check MyMod.esm --data <your Data folder>` from a prompt for Game Pass and other installs.

What each check's findings mean and how to fix them. These are the same lines sfmodcheck prints as HINT lines:

- **structure**: The file itself is damaged (or a tool wrote it wrongly). Open it in xEdit: if it loads, save it again from xEdit or the Creation Kit; if it doesn't, rebuild it from your CK project, a backup or your builder.
- **header**: xEdit: select the File Header record. Missing master: install it, or remove it with Clean Masters (only when nothing uses it). Master order: right-click > Sort Masters. Flags: an .esm needs the ESM (Master) flag, and only one of Light (ESL) or Medium. Next Object ID: edit HEDR > Next Object ID in the File Header so it is above the plugin's highest own FormID.
- **ids**: Own FormIDs must fit the plugin type (light 000-FFF, medium 0000-FFFF). xEdit: Compact FormIDs for ESL / for Medium, or take the Light or Medium flag off. A master index past the master list is a broken FormID: add the missing master in xEdit, or rebuild the record.
- **read**: Mutagen (the library sfmodcheck reads plugins with) can't read this record, so the game may not either. Look at the named record in xEdit for errors in red, fields from another game or a wrong field size, then fix the field or make the record again in the CK.
- **links**: A field points at a record that doesn't exist, is the wrong type or is deleted. xEdit: open the named record; the field shows the FormID, often in red. Point it at an existing record of the right type, or clear it. 'Doesn't exist' often means a master is missing or a master record was removed. CK: re-pick the item in that form's drop-down and save. 'Injected record' means the plugin adds a record in a master's FormID range: fine only if you meant it.
- **navi**: The navmesh info map (NAVI) points at a navmesh that doesn't exist, isn't a navmesh or is deleted. CK: load the cell, select its navmesh and Finalize it, then save. If the plugin deletes a navmesh that others link to, restore it instead. xEdit can't safely fix navmesh data by hand.
- **conditions**: A condition leaves its form parameter empty, where the game's own uses of that function always fill it. CK: open the record's condition list, edit the condition and pick the form (or remove the condition). xEdit: fill in the condition's Parameter #1/#2 field.
- **dialogue**: A dialogue response has an empty Emotion. CK: open the topic info, edit the response and pick an emotion (Neutral is fine). xEdit: set the response's TRDA Emotion to a real value.
- **scenes**: A scene uses a quest alias its quest doesn't have, or has no quest. CK: open the scene's quest, check the Quest Aliases tab, then fix each scene actor, action and condition that names the alias (or set the scene's quest). xEdit: compare the scene's alias IDs with the quest's alias list.
- **editorids**: Give each new record its own Editor ID that no master uses either. CK: open the form and change its ID. xEdit: edit the EDID field. This usually comes from duplicating a record or Copy as New Record.
- **itm**: Identical to Master (ITM) records change nothing but can undo other mods' edits to the same record. xEdit: right-click the plugin > Remove "Identical to Master" records (or run QuickAutoClean). In the CK they come from opening a form and pressing OK without changes.
- **records**: Deleted records can crash the game when the game or another mod still uses them. xEdit: on placed references (REFR, ACHR) use right-click > Undelete and Disable References. For other types, remove the deleting override in xEdit (right-click > Remove) unless you really mean it. In the CK, disable instead of deleting vanilla objects.

Each finding names the record by its EditorID (where it has one) and `[TYPE:FormID:Plugin]`, where FormID is the
record's ID within the plugin that defines it, so you can find it with xEdit's FormID search or the Creation Kit's
filter.

## Memory, time and caches

- A typical mod takes about 4 seconds and under 1 GB of memory: memory follows the size of the plugin being checked,
  not of its masters.
- Checking Bethesda's own masters is possible but heavy: Starfield.esm itself (3.8 million records) takes about 4
  minutes and over 30 GB of memory. sfmodcheck prints a note on stderr before it starts on an official master or any
  plugin with more than 500,000 records.
- Caches live in `%LOCALAPPDATA%\sfmodcheck\` (on Linux `~/.local/share/sfmodcheck/`): `edid-cache\` (the masters'
  EditorIDs, about 7 MB for Starfield.esm) and `scene-quirks\` (the learned official scene list, a few KB). Each is
  keyed by the master's name, size and modified time, so a game update rebuilds it on the next check. Delete the
  folder to clear them. Set the environment variable `SFMODCHECK_CACHE_DIR` to keep them somewhere else (for example
  next to the exe for a portable copy).

## Traps (wrong answers with no error)

- **A master changed kind** (full, medium or light) after the plugin was built: its FormIDs then point at other
  records. sfmodcheck sees this only when an ID lands past a master list or past the plugin's own range.
- **Partial forms** (record flag 0x4000) hold only some fields on purpose; the scenes check uses the last full version
  of a quest for that reason.
- **Mutagen 0.54.4's link-type lists are incomplete** for Starfield, so "wrong type" also accepts any type the
  official masters themselves link to (`data\link-types-seen.txt`). After a game update that list can be out of date
  until a new sfmodcheck release relearns it.
- **An add-on's masters:** without `--masters-dir`, an add-on is checked against the main plugin installed in Data,
  which may be an older build than the one you just made.

## Known gaps

- Conditions inside package target data (75 `CCT_*` conditions in the official masters) aren't seen, because Mutagen
  0.54.4 doesn't expose them as conditions.
- When a condition's parameter is a quest alias rather than a form, the alias number isn't checked.
- In NAVI, only the traversal entries are read directly; edge, preferred and door links come through Mutagen and are
  checked as links.
- Not checked: "missing required field" checks, face data, and anything in loose files or BA2 archives.
- It checks one plugin against its masters. It doesn't check conflicts between mods.

## Build from source

Needs the [.NET SDK](https://dotnet.microsoft.com/download) 9 or later.

    dotnet build src -c Release
    dotnet test tests/SfModCheck.Tests -c Release

Package versions are fixed by the lock files (`packages.public.lock.json`): Mutagen comes through
[BuildKit](https://github.com/taosecurity/BuildKit) 0.2.0 from nuget.org. The tool package and the stand-alone exe:

    dotnet pack src -c Release -p:SfModCheckPack=true -o out
    dotnet publish src -c Release -r win-x64 -p:SfModCheckStandalone=true -o out/exe

The tests make every master and plugin they check in a temp folder, so they need no game files. A test also checks
that every option in `src/Program.cs` is named in this README and in AGENTS.md. Five of them need
the real Starfield.esm (engine-made records and the official scene list) and are skipped when it isn't found; set
`SFMODCHECK_TEST_DATA` to your Data folder to run them. The tests never touch your real caches.

### Data files built into the exe

- `data\engine-forms.txt`: records the game's engine creates, which plugins may link to without any master having
  them. From xEdit's hardcoded list; see [NOTICE](NOTICE).
- `data\link-types-seen.txt`: for each Mutagen link type, the record types the official masters link to, and how often.
- `data\condition-params-seen.txt`: for each condition function and parameter, how often the official masters use it
  and how often they leave it empty.

The last two hold counts and record type names only. They are relearned from the official masters after a game update
with the hidden commands `--learn-link-types <out> [Data]` and `--learn-conditions <out> [Data]`, then built in again.

## Credits and licence

sfmodcheck is © 2026 Richard Bejtlich, under the GNU General Public License version 3 ([LICENSE](LICENSE)).
`data\engine-forms.txt` is derived from xEdit's hardcoded Starfield list (TES5Edit, by ElminsterAU and the xEdit team)
and stays under the Mozilla Public License 2.0; see [NOTICE](NOTICE). Mutagen is by Noggog and the Mutagen
contributors (GPL-3.0). See [PRIVACY.md](PRIVACY.md) for what the tool reads and writes (it makes no network
connections), and [CHANGELOG.md](CHANGELOG.md) for changes.
