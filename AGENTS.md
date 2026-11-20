# AGENTS.md: sf-save-reader

Instructions for AI assistants (and people) using sf-save-reader. Every command below was run once before release.

## 1. What it is for

sf-save-reader reads Starfield `.sfs` save files, read-only. Use it to answer questions about a save: which plugins (mods) it depends on, the game version that wrote it, the player's level and location, the state of each quest (status, current stage, stages done, objectives), and which change records belong to a plugin. It is also a C# library (namespace `SfsReader`) for tools that check saves.

Don't use it to edit or repair a save: it never writes to a save.

## 2. Prerequisites

1. Windows 10 or 11 (Linux works for the commands; `--data auto` then checks `~/.steam/steam` only).
2. The .NET SDK 9.0 or later (tested with 10.0.404, building for net9.0). Check with `dotnet --version`.
3. Git, to clone the repository.
4. For names (`--data`): an installed copy of Starfield (Steam or Game Pass).

## 3. Install

```
git clone https://github.com/taosecurity/sf-save-reader
cd sf-save-reader
dotnet build -c Release
```

The program is `bin\Release\net9.0\sf-save-reader.exe`. The commands below call it `sf-save-reader`; use the full path, or add the folder to PATH.

## 4. Prove the install works

```
sf-save-reader --version
```

Expected output: `sf-save-reader 0.1.2`, exit code 0.

To run the fragment tests (made-up data only, no saves needed): `dotnet test tests\SfsReader.Tests -c Release` (expect all tests passed).

## 5. Common tasks

Steam saves are in `%USERPROFILE%\Documents\My Games\Starfield\Saves`. Quote every path.

1. What a save holds (character, game version, plugin list):
   `sf-save-reader dump "<save.sfs>"`
2. The same as JSON (adds every change record for save version 153):
   `sf-save-reader dump "<save.sfs>" --json`
3. Check the save's change records read cleanly:
   `sf-save-reader walk "<save.sfs>"`
4. Quest state with names from the game (finds the Data folder itself):
   `sf-save-reader quests "<save.sfs>" --data`
5. One quest line's details (stages and objectives):
   `sf-save-reader quests "<save.sfs>" --data --filter "<quest name or plugin>" --details`
6. A plugin's change records (what the save remembers about one mod):
   `sf-save-reader records "<save.sfs>" --plugin "<Plugin.esm>" --data`
7. What changed between two saves:
   `sf-save-reader compare "<a.sfs>" "<b.sfs>"`
8. Output that is safe to share (no player name or file name):
   add `--redact` to any command.
9. A log for troubleshooting:
   add `--verbose` (log to stderr) or `--log "<file.log>"` to any command.

## 6. Inputs and outputs

- Input: one `.sfs` file (two for `compare`). Game Pass saves must be exported to `.sfs` first (README, "Where saves and plugins are").
- `--data [DIR]`: the game's Data folder. With no folder: `STARFIELD_DATA`, then the Steam libraries, then `X:\XboxGames\Starfield\Content\Data`.
- Output: text on stdout; with `--json`, one JSON object with `schemaVersion` 1, `tool`, `file` and the data. Warnings and the quest count line go to stderr.
- Files written: only the ones you name (`--log <file>`, which is replaced, but never a `.sfs` file, a file given as input or a file that starts like a save; `extract <save> <out.bin>`, which refuses an existing file).

## 7. Exit codes

| Code | Meaning |
|---|---|
| 0 | Done. |
| 1 | Done, with warnings (or some quest records didn't decode). |
| 2 | Usage error (unknown command, missing argument, `--log` without a file or pointing at a save). |
| 3 | Error: the file is missing or unreadable, isn't a Starfield save, is cut off, or its save version isn't supported. The `error:` line names the file and the `fix:` line says what to do. |

## 8. Log

No log file unless asked for. `--verbose` writes `[verbose]` lines to stderr; `--log <file>` writes the same lines with times to that file. The log holds the save's sizes, header and plugin counts, the record walk result, the Data folder used, each plugin scanned, where names came from and timings. With `--redact` the log and the error messages leave out the save's file name and path.

## 9. Limits

1. It never changes, moves or locks a save, and never uploads anything. It doesn't use the network.
2. Change records and quests are read for save version 153 (Starfield 1.16.244) only. Older saves: `dump` works (header and plugin list); `walk`, `records` and `quests` exit 3. Newer save versions are refused by `dump`, `walk`, `records`, `quests` and `compare` (`extract` only unpacks the container).
3. A quest with no done stage has no current stage (`stage -` in text, `"stage": null` in JSON).
4. It doesn't decode global data, inventories, ships or other record contents.
5. Under Mod Organizer 2, run it from MO2 so `--data` sees the mods' plugins (README).
6. The format description is [FORMAT.md](FORMAT.md). Don't add facts to it from guesses.
