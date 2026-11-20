# Changelog

## 0.1.3 (2026-11-20)

### What's new

This is a small fix release. When a save is from an older version of Starfield, the message that tells you what to do now names the tool's real version. Before, it always said "sf-save-reader 0.1". Nothing about how saves are read has changed.

### Example

A test save marked as save version 152 (an older game version) is refused with exit code 3, and the fix line names the version you are running (the file name is shortened; the test save gives its game version as 9.9.9.0):

    > sf-save-reader walk OldSave.sfs
    error: OldSave.sfs: save version 152 (game 9.9.9.0) isn't supported; this reader reads save version 153 only
    fix: sf-save-reader 0.1.3 reads change records for save version 153 (Starfield 1.16.244) only; `dump` still shows the header and plugin list of older saves. Load the save in the current game and save again to update it

### Fixes

- The fix line for a save version that isn't supported now uses the same name and version as `--version`, read from the program itself. A new test runs the program on a made-up save version 152 and checks it. The supported save version in that line now comes from the reader's own list too.
- A BA2 archive that uses a compression method other than zlib no longer gets that same fix line (which told you to load and save your game again). It now gets the general fix line for files that can't be read.

### Download

Download `sf-save-reader-0.1.3.zip`, unzip it to a folder and run `sf-save-reader.exe --version` in the `sf-save-reader-0.1.3` folder it makes. It needs the .NET 9 runtime (or later). The SHA-256 checksum is in `sf-save-reader-0.1.3.zip.sha256`.

## 0.1.2 (2026-11-20)

### What's new

This update fixes name lookups on Linux and Proton. Linux file systems are case-sensitive, so before this fix a save that spelled a plugin differently from the file in the Data folder showed no names for that plugin. Windows users see no difference.

### Example

`quests --data` names each quest from the plugins in the Data folder (lines marked `...` were left out):

    > sf-save-reader quests "Exitsave0_....sfs" --data "D:\Games\Starfield\Data"
    00003448 Starfield.esm:3448                      running, stage 1510  One Small Step  [MQ101]
    ...
    001C0F26 Starfield.esm:1C0F26                    stopped, completed, stage 200  Board and Lodging  [FFLodge01]
    ...
    597 quest records: 597 decoded exactly, 0 not decoded

### Changes

- Plugins, the Strings folder, `.strings` files and a plugin's BA2 archives are now found whatever their capitalization.
- `--data` finds `Starfield.esm` in any spelling.
- `--lang` must be a language code such as `en` or `de`.
- Tests now run on Windows and Linux for every change (GitHub Actions).

### Download

Download `sf-save-reader-0.1.2.zip`, unzip it to a folder and run `sf-save-reader.exe --version` in the `sf-save-reader-0.1.2` folder it makes. It needs the .NET 9 runtime (or later). The SHA-256 checksum is in `sf-save-reader-0.1.2.zip.sha256`.

## 0.1.1 (2026-11-20)

A small update: `dump` no longer prints empty brackets `[]` after plugins that have no Creation name, the README gained real sample output, and release builds no longer contain the folder they were built in.

## 0.1.0 (2026-11-20)

The first release: a read-only reader for Starfield `.sfs` saves that shows the header and plugin list, lists change records by plugin, decodes every quest's state, compares two saves and unpacks a save's container.
