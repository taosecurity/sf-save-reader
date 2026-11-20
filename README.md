# sf-save-reader

sf-save-reader reads a Starfield save (`.sfs`) and tells you what is in it: the character, the game version, the list of plugins (mods) the save depends on, and the state of every quest (running or stopped, current stage, stages done, objectives). It can also list the save's change records, and it names forms from the game's own plugins when you point it at the Data folder. It is useful for checking what a save remembers about a mod you removed, for comparing two saves, and as a library for tools that check saves.

It only reads. It never changes, moves or locks a save, and it never uploads anything.

For AI assistants: see [AGENTS.md](AGENTS.md) for step-by-step instructions.

## First command

Build it (needs the .NET SDK 9 or later):

```
git clone https://github.com/taosecurity/sf-save-reader
cd sf-save-reader
dotnet build -c Release
```

Then show what a save holds:

```
bin\Release\net9.0\sf-save-reader.exe dump "%USERPROFILE%\Documents\My Games\Starfield\Saves\Save1_XXXXXXXX_0_....sfs"
```

Put the path in quotes: save folders and names often have spaces.

## What it reads

- **Every save version:** the container, the header (player name, level, location, play time, game version) and the plugin list (full, medium and light plugins, with each Creation's name).
- **Save version 153 only (saves written by Starfield 1.16.244):** the change records and the quest state. sf-save-reader 0.1 reads change records for save version 153 only. For older saves `dump` shows the header and plugin list, and `walk`, `records` and `quests` refuse the save (exit code 3). Load an old save in the current game and save again to get a version-153 save.
- A save version newer than 153 is refused, because its layout may have changed. Look for a newer release then.
- A quest with no done stage has no current stage: the text output shows `stage -` and the JSON shows `"stage": null`.

The format is written up in [FORMAT.md](FORMAT.md), with where each fact came from.

## Commands

| Command | What it does |
|---|---|
| `dump <save> [--json]` | Container, header, plugin list and section sizes. `--json` adds every change record (version 153). |
| `walk <save>` | Reads every change record and prints a summary (counts by kind, and whether the walk ended exactly where the save says). |
| `records <save> [--type N] [--plugin NAME] [--limit N] [--data [DIR]] [--json]` | Change records, numbered from 0. `--plugin` keeps one plugin's records; `--type 8` keeps quests. |
| `quests <save> [--data [DIR]] [--filter TEXT] [--details] [--json]` | Quest state. `--details` adds the stage list (`*` = done) and objective states. |
| `compare <a.sfs> <b.sfs>` | Header fields, plugins and sections that differ between two saves. |
| `extract <save> <out.bin>` | Writes the decompressed save data to a new file (it never overwrites a file). |

Options for every command: `--verbose`, `--log <file>`, `--redact` (below), `--help` and `--version`. `--lang <code>` picks the language of names (default `en`).

### Names from the game's plugins: `--data`

With `--data <folder>`, `records` and `quests` add each form's name and editor ID, read from the plugins in that folder (localized names from `Strings\` or the plugins' BA2 archives). `--data` alone (or `--data auto`) looks for the folder in this order:

1. the `STARFIELD_DATA` environment variable;
2. every Steam library: `...\steamapps\common\Starfield\Data`;
3. Game Pass / Xbox app: `X:\XboxGames\Starfield\Content\Data` on each fixed drive.

`quests --data` also warns about quest records whose plugin isn't in the folder (the mod was removed since the save) and about a quest record whose form is now something else in its plugin (the mod changed since the save).

### Where saves and plugins are

- **Steam:** saves are in `Documents\My Games\Starfield\Saves`; the Data folder is `C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data` (or the Steam library you installed to).
- **Game Pass / Xbox app:** the game is in `X:\XboxGames\Starfield\Content`. The saves are kept inside the Xbox app's own container files (in the game's folder under `%LOCALAPPDATA%\Packages`), not as `.sfs` files. Export them to `.sfs` first, for example with HarukaMa's [starfield-xgp-import](https://github.com/HarukaMa/starfield-xgp-import), then run sf-save-reader on the exported file.
- **Vortex:** Vortex deploys mods into the game's real Data folder, so `--data` with the game's Data folder sees your mods.
- **Mod Organizer 2:** MO2 keeps mods in its own `mods` folder and shows them in Data only to programs it starts. Add `sf-save-reader.exe` as an executable in MO2 (with arguments such as `quests "<save>" --data "<game>\Data"`) and run it from MO2, so the mods' plugins are seen. Run outside MO2, only the plugins really in Data (the base game and Creations) get names.

## Privacy: `--redact`

A save's file name holds the character ID and the player name (in hex), and the header holds the player name. `--redact` replaces both with `(redacted)` in the output and the log, so you can share the output. Plugin names, locations and quest states stay. The format has no platform or account ID field that sf-save-reader reads.

## Logging and troubleshooting

- `--verbose` writes what the reader read and decided to the error stream (stderr): the file and container sizes, the header and plugin counts, how many change records were read and whether they ended where the save says, which Data folder was used, which plugins were scanned and how many forms were found, where names came from (a loose strings file or which BA2), and how long each part took.
- `--log <file>` writes the same lines, with times, to a file (the file is created or replaced; to protect your saves it refuses a `.sfs` name, a file you also gave as input, and any file that starts like a save). There is no log file unless you ask for one.
- Errors name the file and say how to fix the problem, for example:

```
error: Save12.sfs: save version 143 (game 1.14.74.0) isn't supported; this reader reads save version 153 only
fix: sf-save-reader 0.1 reads change records for save version 153 (Starfield 1.16.244) only; ...
```

For a bug report, run the command again with `--log sf-save-reader.log --redact` and attach the log.

Exit codes: 0 = done; 1 = done with warnings (or some quest records didn't decode); 2 = usage error; 3 = error (the file can't be read, isn't a save, or its version isn't supported).

## JSON output

`dump --json`, `records --json` and `quests --json` write one JSON object with `schemaVersion` (1 in sf-save-reader 0.1), `tool` (name and version), `file` and the data (`save` and `body`, `records`, or `quests`). A new field may be added without a new schema version; a field that is renamed or removed raises it.

## Using it as a library

Reference `SfsReader.csproj` from your project (namespace `SfsReader`):

```csharp
var (container, save) = SaveFile.Open(path);          // header and plugin list
var body = SaveBody.Parse(container.Inner, save);     // change records (version 153)
foreach (var r in body.Records.Where(r => r.FormType == 8))
{
    var q = QuestState.Decode(container.Inner, r);
    Console.WriteLine($"{r.Form} {q.Status} stage {q.CurrentStage?.ToString() ?? "-"}");
}
var game = new GameData(@"C:\...\Starfield\Data") { Log = Console.Error.WriteLine };
```

## Limits

- Read-only: it opens saves for reading only, shared, and writes only the files you name (`--log`, `extract`).
- It doesn't read global data, inventories, ships or the other record data inside a save (see FORMAT.md, "Not in this file").
- Change records and quests: save version 153 only (above).
- It doesn't run the game's scripts, so "running" may still be "starting" for a quest that just began (FORMAT.md, "Quest state").

## Credits

- [StarfieldSaveTool](https://github.com/Nexus-Mods/StarfieldSaveTool) by insomnious: the container layout and the header field names.
- UESP: the VSVAL and form-reference scheme of the older games, and the plugin and BA2 layouts (with xEdit and Mutagen).
- HarukaMa's starfield-xgp-import, for Game Pass saves.
- CommonLibSF, for engine form-type names.

## License

MIT, see [LICENSE](LICENSE).
