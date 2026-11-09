# sfmodcheck: instructions for AI assistants

These steps are for an AI assistant (or anyone) helping a Starfield player or mod author use sfmodcheck. Every command
here was run once on sfmodcheck 0.4.0 before release. The [README](README.md) has the full description.

## 1. What it is for

sfmodcheck reads one Starfield plugin (`.esm`) and reports problems that would break it in the game: damaged files,
bad headers, FormIDs outside the plugin's range, links to records that don't exist, broken conditions and scenes,
repeated EditorIDs, deleted records and "identical to master" (ITM) records. It is a command-line cousin of part of
xEdit's "Check for Errors".

Use it when someone has made or changed a mod and wants to know whether the plugin is sound before they play it or
release it, or when a mod misbehaves and you want to rule out a broken plugin.

Don't use it to check conflicts between several mods (it checks one plugin against its masters), to fix a plugin
(it never changes files), or on Bethesda's own masters (Starfield.esm needs over 30 GB of memory).

## 2. Prerequisites

1. Windows 10 or 11 (x64) for the stand-alone exe: nothing else needed, .NET is built in.
2. Or, for the dotnet tool on Windows or Linux: the .NET SDK 9.0 or later (`dotnet --version` prints 9.x or higher).
3. Starfield installed, so its masters (Starfield.esm and the others in the game's `Data` folder) can be read.
   sfmodcheck only reads them.

## 3. Install

Pick one. Files come from https://github.com/taosecurity/sfmodcheck/releases (version 0.4.0 shown here).

1. **Stand-alone exe (Windows):** download `sfmodcheck-0.4.0-win-x64.zip` and its `.sha256` file, check the
   checksum, and unzip it to a folder of your choice:

       certutil -hashfile sfmodcheck-0.4.0-win-x64.zip SHA256
       mkdir C:\Tools\sfmodcheck
       tar -xf sfmodcheck-0.4.0-win-x64.zip -C C:\Tools\sfmodcheck

   Compare the printed hash with the one in `sfmodcheck-0.4.0-win-x64.zip.sha256`. The program is then
   `C:\Tools\sfmodcheck\sfmodcheck.exe`.

2. **dotnet tool (Windows or Linux):** download `sfmodcheck.0.4.0.nupkg` into a folder (here `C:\Downloads`), then
   either install it for the user:

       dotnet tool install --global sfmodcheck --add-source C:\Downloads --version 0.4.0

   or into a folder of its own (nothing global changes):

       dotnet tool install sfmodcheck --tool-path C:\Tools\sfmodcheck-tool --add-source C:\Downloads --version 0.4.0

   On Linux, `sha256sum -c sfmodcheck.0.4.0.nupkg.sha256` checks the download.

## 4. Prove the install works

    sfmodcheck --version

It prints `sfmodcheck 0.4.0` and exits with code 0. (Use the full path, such as `C:\Tools\sfmodcheck\sfmodcheck.exe
--version`, when the folder isn't on PATH.)

## 5. Common tasks

1. Check a plugin with a Steam install in the usual place:

       sfmodcheck check "C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data\MyMod.esm"

2. Game Pass, or a Steam library on another drive: say where the game's Data folder is.

       sfmodcheck check MyMod.esm --data "D:\XboxGames\Starfield\Content\Data"

3. An add-on built next to the main plugin it needs: look for masters in the build folder first.

       sfmodcheck check out\MyMod-Addon.esm --masters-dir out

4. Mod Organizer 2: the plugin lives in `mods\<ModName>\`. Point `--data` at the real game Data folder and
   `--masters-dir` at the folder of any mod whose plugin is a master:

       sfmodcheck check "C:\Modding\MO2\mods\MyMod\MyMod.esm" --data "C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data" --masters-dir "C:\Modding\MO2\mods\MainMod"

5. Something went wrong, or the result looks odd: write a troubleshooting log.

       sfmodcheck check MyMod.esm --log sfmodcheck-log.txt

   `--verbose` prints the same log lines on the screen (stderr) instead.

6. Fewer lines: `--max 20` (at most 20 lines per check), `--no-hints` (no HINT lines), `--quiet` (no summary line).

## 6. Inputs and outputs

- **Input:** one plugin file per run, plus its masters, found in this order: `--masters-dir` folders (or the plugin's
  own folder when none is given), then the Data folder (`--data`, else Steam's usual
  `C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data`; with no `--data` and no game there, a plugin that
  sits in a folder holding Starfield.esm uses that folder).
- **Output (stdout):** a summary line; `ERROR [check] ...` lines (broken: fix before release); `WARNING [check] ...`
  lines (probably a mistake); `HINT [check] ...` lines (how to fix that check's findings in xEdit or the Creation Kit);
  and always one last line `RESULT: OK|FAILED|CANNOT CHECK ...`. Scripts should read only the exit code and the lines
  starting with `ERROR `, `WARNING ` and `RESULT: `.
- **stderr:** notes (very large plugin, no Starfield.esm found), error messages for bad arguments, and the
  `--verbose` log lines (`log: ...`).
- **Exit codes:** 0 = no errors (warnings allowed); 1 = errors; 2 = couldn't check (bad arguments, a missing file or
  folder, or a file it couldn't open).
- **Log:** only when asked: `--log <file>` writes it to that file (overwritten each run), `--verbose` to stderr. It
  holds the version, the arguments, the plugin, the Data folder and why it was chosen, where each master was found (or
  every place looked), the caches used or rebuilt, the time per stage, a count of findings per check, and on a crash
  the full error. It names folders on your PC: read it before posting it anywhere.
- **Caches:** `%LOCALAPPDATA%\sfmodcheck\` on Windows, `~/.local/share/sfmodcheck/` on Linux (the masters' EditorIDs and
  the learned list of official scenes; rebuilt by themselves after a game update). The environment variable
  `SFMODCHECK_CACHE_DIR` moves them. Deleting the folder is safe.

## 7. Limits

1. It never changes, moves or deletes a plugin or anything in the game folder. It writes only its caches and the
   `--log` file.
2. It makes no network connections and uploads nothing.
3. It checks one plugin against its masters: not conflicts between mods, not loose files or BA2 archives, not
   scripts.
4. A missing master stops the link checks (the RESULT line says "links not checked"): install the master or point
   `--data` / `--masters-dir` at it, then run again.
5. It isn't meant for Bethesda's own masters (Starfield.esm needs over 30 GB of memory and about 4 minutes).
6. Known gaps are in the README ("Known gaps" and "Traps").
