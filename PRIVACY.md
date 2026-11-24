# Privacy

sf-save-reader runs on your own computer. It collects no data and sends nothing anywhere.

## What it does not do

- It makes no network connections.
- It has no telemetry, analytics, crash reporting or update checks.
- It never changes a save or any game file.
- It starts no other program.

## What it reads

- the save you name (`.sfs`), which it opens for reading only;
- with `--data` (or `--data auto`), the game's Data folder, to turn names and form IDs into readable names: the
  plugins' headers and records and their string tables (loose `.strings` files, or the plugin's own `.ba2`
  archives). The automatic search looks at the `STARFIELD_DATA` environment variable, Steam's `libraryfolders.vdf` and
  the Game Pass folders, only to find a folder that holds `Starfield.esm`.

## What it writes

It prints its results to the console. Nothing else is written unless you ask:

- `--log <file>` writes a troubleshooting log to the file you name (created or replaced). There is no log file unless
  you ask for one. `--verbose` prints the same lines on the console instead. The log refuses a `.sfs` name, a file you
  also gave as input and any file that starts like a save, so it can't overwrite a save.
- `extract <save> <out.bin>` writes the save's decompressed data to a new file. It never overwrites a file. That file
  holds everything in the save, including the player's name, so treat it like the save itself.

The output and the log name the save file, the player's name, the plugins in the save and your quest and location
data. The log also lists the paths it used, so it can include your Windows user name. Without `--redact`, read them
before you share them.

## `--redact`

`--redact` replaces the save's file name and the player's name with `(redacted)` in the output and the log. Plugin
names, locations and quest states stay. The format has no platform or account ID field that sf-save-reader reads.

## Personal data

sf-save-reader handles none beyond what is in your own save and file paths, as described above. Nothing leaves your
computer unless you copy the output somewhere yourself.

## Downloading it

Downloading sf-save-reader from GitHub is covered by GitHub's own
[privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
sf-save-reader's author receives only the public download counts GitHub shows.

## Questions

Open an issue at https://github.com/taosecurity/sf-save-reader/issues.
