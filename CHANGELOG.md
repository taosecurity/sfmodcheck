# Changelog

## 0.4.0 (unreleased)

First public release. Built on Mutagen.Bethesda.Starfield 0.54.4 (through BuildKit 0.2.0).

- `sfmodcheck check <plugin>`: structure, header, ids, read, links, conditions, dialogue, scenes, navi, editorids,
  itm and records checks, with ERROR / WARNING lines, one RESULT line and exit codes 0, 1 and 2.
- HINT lines: after each check's findings, one line on how to fix them in xEdit or the Creation Kit. `--no-hints`
  turns them off.
- The official scene list is learned from your own Data folder and cached in `%LOCALAPPDATA%\sfmodcheck\`.
- A note on stderr before checking an official master or a plugin with more than 500,000 records (Starfield.esm
  needs over 30 GB of memory).
- Drag and drop: a plugin dropped on `sfmodcheck.exe` is checked in its own window, which stays open at the end. With
  no `--data` and no game in Steam's usual folder, a plugin inside a Data folder uses that folder.
- Two downloads: a dotnet tool package and a stand-alone `sfmodcheck.exe` (win-x64, .NET built in).

Earlier versions (0.1.0 to 0.3.0, 2026-11-06 to 2026-11-08) were private, under the name StarfieldPluginCheck.
