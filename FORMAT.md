# Starfield save format (.sfs)

This file describes how a Starfield save is laid out, for anyone writing a reader. It covers Starfield 1.16.244 (save version 153, engine version 27): the container, the header, the plugin list, the change forms and the quest state inside quest records. It holds format descriptions only. No game code, pseudocode or disassembly is in it.

Each fact carries a tag that says where it came from:

- `[code 0x…]`: read in the game's own save or load code (Starfield.exe 1.16.244, in a read-only Ghidra project). The address is the function or table where the fact can be seen.
- `[file]`: checked in real save files.
- `[public doc: …]`: from a public format document or tool. StarfieldSaveTool (Nexus-Mods/StarfieldSaveTool, by insomnious) is credited for the container and the header field names.

Status: written on 2026-11-10 (public-parser plan, wave 2 and the step after it).

All numbers are little-endian unless a line says otherwise. A **string** is a u16 byte count followed by that many UTF-8 bytes, with no terminator `[file]`.

## The container (the .sfs file itself)

The file is a `BCPS` container holding the save data in zlib chunks `[file]`, `[public doc: StarfieldSaveTool]`.

| Offset | Type | Meaning |
|---|---|---|
| 0x00 | 4 bytes | magic `BCPS` |
| 0x04 | u32 | container version, 1 |
| 0x08 | u64 | offset of the chunk-size table (0x48 in every sample) |
| 0x18 | u64 | offset of the first chunk: the end of the chunk-size table, rounded up (0x100 for most saves, 0x2D0 for a 159-chunk save) |
| 0x20 | u64 | total size of the decompressed save data |
| 0x30 | u32 | chunk size before compression: 0x40000 (256 KB) |
| 0x38 | u32 | alignment: 16 |
| 0x44 | 4 bytes | compression tag `ZIP ` (zlib) |

- The chunk-size table holds one u32 per chunk: the chunk's compressed size. The number of chunks is the total size divided by the chunk size, rounded up.
- Each chunk is a zlib stream that decompresses to 256 KB (the last one to whatever is left). The next chunk starts at the next 16-byte boundary after the previous one ends.
- **Stored chunks.** A chunk whose size in the table equals its full uncompressed size (256 KB, or what is left for the last one) and which isn't a zlib stream is stored as is: copy it straight into the save data `[file]`. The game does this when zlib can't make the chunk smaller, which happens for data that is already compressed inside the save. Seen in 4 of 1,010 archive saves (all large version-153 saves from one playthrough, 27 or 28 stored chunks each, all inside the last large section). Checks: no zlib chunk in the archive is full size; the stored chunks hold random-looking data (8.00 bits per byte); with them copied, each save rebuilds to exactly the size at 0x20 and the header, plugin list and section table read without warnings. A full-size chunk is tried as zlib only when its first two bytes are a valid zlib header (RFC 1950), and falls back to stored if that fails.
- Joined in order, the chunks give the **save data**. Every other offset in this file is measured from the start of the save data.
- Game Pass saves sit inside another container first; see HarukaMa/starfield-xgp-import.

## The header

`[file]`, `[public doc: StarfieldSaveTool]` for the field names, `[code 0x14182FD70]` (the header writer) for the save number.

| Type | Field |
|---|---|
| 12 bytes | magic `SFS_SAVEGAME` |
| u32 | header size, counted from the byte after this field |
| u32 | engine version: 27 |
| u8 | save version: 153 (0x99) |
| u32 | save number: the manual-save counter, written for every save type `[code 0x14182FD70]` |
| string | player name |
| u32 | player level |
| string | location name |
| string | play time, as the game shows it (for example `0d.5h.6m.0 days.5 hours.6 minutes`) |
| string | race |
| u16 | gender |
| f32 | experience |
| f32 | experience needed for the next level |
| u64 | save time, a Windows FILETIME (UTC) |

The header-size field covers these and possibly more bytes. Skip to (offset of the byte after the header-size field) + header size. The location name changes length, so the fields after it have no fixed offsets.

After the header come `[file]`, `[code 0x141814220]` (the writer starts with the same three fields):

| Type | Field |
|---|---|
| u8 | save version again (153) |
| string | game version that wrote the save (for example `1.16.244.0`) |
| string | game version the character was created in |
| u16 | plugin-info size: the location table starts this many bytes after the 2 padding bytes that follow |
| u16 | padding |

## The plugin list

`[file]` on 13 of 13 PC saves; `[public doc: StarfieldSaveTool]` for the pre-1.16 layout.

1. u8: number of full plugins, then that many plugin entries.
2. u16: number of light plugins, then that many plugin entries.
3. For save versions 109 and above: u32 number of medium plugins, then that many plugin entries `[file]`. StarfieldSaveTool reads this list only from version 122, but every archive save of versions 109, 110, 111 and 119 has it (193 saves; in version 119 it holds `SFBGS006.esm`). Version 107 and earlier don't have it; no version-108 save has been seen.
4. For save versions 109 and above (seen in 109-111, 119, 122, 140, 143 and 153; absent in 107 and earlier): a u16, meaning unknown. It is 1 in every sample except one version-122 save (game 1.12.36), where it is 4 `[file]`. StarfieldSaveTool's layout doesn't have it. A reader can tell it's there because the location table starts 2 bytes later than the plugin entries end.

A plugin entry is:

- string: file name (for example `Starfield.esm`);
- for save versions 140 and above: a u8 "has extra" flag. If it's nonzero, the **Creation block** follows: string Creation name, string Creation ID, string flags, and a u8 (achievement-friendly).
- for save versions 122 to 139: no flag byte. The Creation block follows every plugin **except** the official ones of that time: `Starfield.esm`, `Constellation.esm`, `OldMars.esm`, `BlueprintShips-Starfield.esm`, `SFBGS003.esm`, `SFBGS006.esm`, `SFBGS007.esm` and `SFBGS008.esm` `[public doc: StarfieldSaveTool]`, `[file]`. Checked on that version-122 save (3 mods, 4 mod plugins): with this rule the list ends exactly where the plugin-info size says, and the location table after it reads cleanly. Without it, the first mod plugin's block is read as the next plugin's name, which gave the old misread of 4,101 plugins. The mod plugins' blocks there hold an empty Creation name and ID and a 6-byte flags string.
- below save version 122: the file name only.

The location table starts at (offset after the padding u16) + plugin-info size `[file]`.

The order of the plugins gives the plugin indexes used by the form IDs inside the save: full plugins by their position in the full list, light plugins by their position in the light list, and medium plugins by their position in the medium list (see "Form ID → plugin").

## Form ID → plugin

A form ID names its plugin through its top bits. These are Starfield's public plugin rules (xEdit, Mutagen), and the game's own form-ID array loader decodes them in the same way `[code 0x14180ADE0]`:

| Top byte | Plugin kind | Plugin index | Form number |
|---|---|---|---|
| `FE` | light | bits 12–23 | bits 0–11 |
| `FD` | medium | bits 16–23 | bits 0–15 |
| `FF` | none: created during play | — | — |
| anything else | full | the top byte | bits 0–23 |

Inside the save, the indexes refer to the save's own plugin list, not to the current load order.

## Where the parts are: the location table

After the plugin list comes a table of 25 u32 values. The game writes 100 zero bytes there first, writes the sections, then goes back and fills the table in `[code 0x141814220]`. The loader reads the same 100 bytes `[code 0x141815620]`. Offsets are measured from the start of the decompressed save stream.

| Slot | Meaning | Source |
|---|---|---|
| `[0]` | Start of the form-ID array | `[code 0x141814220]`, `[code 0x141815620]` |
| `[1]` | Start of a small section written after the form-ID array (8 bytes in every sample save) | `[code 0x141814220]`, `[file]` |
| `[2]` | Start of global data 1 | `[code 0x141814220]` |
| `[3]` | Start of global data 2 | `[code 0x141814220]` |
| `[4]` | Start of the change forms | `[code 0x141814220]`, `[code 0x141815620]` |
| `[5]` | Start of global data 3, which is also where the change forms end | `[code 0x141814220]`, `[file]` |
| `[6]`, `[7]`, `[8]` | Number of entries in global data 1, 2 and 3 | `[code 0x141814220]` |
| `[9]` | Number of change records | `[code 0x141814220]` (counted once per record written), `[file]` |
| `[10]` | Not written by the save code; 0 in every sample | `[code 0x141814220]`, `[file]` |
| `[11]` | Start of the type directory | `[code 0x141814220]`, `[code 0x141815620]` |
| `[12]`–`[24]` | Not written; 0 in every sample | `[code 0x141814220]`, `[file]` |

The sections are written in this order: global data 1, global data 2, change forms, global data 3, the form-ID array, the slot `[1]` section, the type directory, and then more data to the end of the stream that this file doesn't describe `[code 0x141814220]`. That's why slot `[11]` is always slot `[1]` + 8 in the samples.

## VSVAL: variable-size count

Counts in the change forms and the type directory are stored as a VSVAL.

- Writing `[code 0x14140C7A0]`: a value below 0x40 takes 1 byte, below 0x4000 takes 2 bytes, and up to 0x3FFFFFFF takes 4 bytes. The stored number is `value << 2`, with the size tag in the low 2 bits: 0 = 1 byte, 1 = 2 bytes, 2 = 4 bytes. The writer never produces tag 3; a larger value is not written at all.
- Reading `[code 0x141809400]` (the main save stream) and `[code 0x141865370]` (a record's own buffer): read the first byte; the low 2 bits give the size; read that many bytes as a little-endian number; the value is that number `>> 2`.
- **Tag 3:** the two readers disagree about it (one treats it as a tag byte followed by a plain u32, the other doesn't read further), and the writer never writes it. A reader should treat tag 3 as an error.
- `[file]`: tag 3 doesn't occur in any of the 13 sample saves.
- `[public doc: UESP, Skyrim and Fallout 4 save file format]` describes the same encoding for the older games.

## Refids: 3-byte form references

Change records name forms with a 3-byte refid.

- **Byte order:** big-endian. The value is `byte0 << 16 | byte1 << 8 | byte2` `[code 0x1418599A0]`. The writer stores the high byte first in the same way `[code 0x141814220]`.
- **Kind** = the top 2 bits of the 24-bit value (`value >> 22`). The other 22 bits are the payload.

| Kind | Meaning | How to get the form ID |
|---|---|---|
| 0 | Index into the form-ID array | Look up the payload in the form-ID array (next section). 0 means no form. |
| 1 | A form from the first plugin (form ID top byte `00`) | `payload` (top byte `00`) |
| 2 | A form created during play (top byte `FF`) | `0xFF000000 | payload` |
| 3 | Never written | Treat as an error |

Sources:

- The loader decodes kinds exactly as in the table `[code 0x1418599A0]`. Kind 3 isn't a separate case there: the loader would treat it like kind 1. The writer never produces it.
- The writer picks the kind from the form ID `[code 0x141814220]`: top byte `00` gives kind 1, top byte `FF` gives kind 2, and anything else (other full, light `FE` and medium `FD` plugins, and form ID 0) goes through the form-ID array as kind 0 `[code 0x14180AAB0]`.
- After decoding, the game passes kinds 1 and 2 through a remapping table that fixes up IDs when the load order or created IDs have changed `[code 0x14180B410]`. A save reader should report the stored value: it's what the save says, before any fix-up for the current game.
- `[file]`: in the 13 sample saves, about 46,000 references per save are kind 0, 392,000 kind 1 and 104,000 kind 2. Kind 3 never occurs.
- `[public doc: UESP, Skyrim and Fallout 4 save file format]` describes the same 2-bit scheme. Starfield's own meaning of kind 1 (first-plugin forms only) comes from the code above.

## The form-ID array (location slot `[0]`)

- **Layout:** u32 count, then that many u32 form IDs `[code 0x14180ADE0]` (loader), `[code 0x14180AC10]` (writer).
- **Index base:** a kind-0 refid whose payload is `i` names entry `i - 1` of the array (the first entry is index 1). Index 0 is kept for "no form". The loader puts form ID 0 at index 0 before it reads the array, then numbers the array's entries 1, 2, 3 and so on in file order `[code 0x141815620]`, `[code 0x14180ADE0]`. The writer gives out indexes starting at 1, and when saving it writes the count minus the reserved slot and then the entries for indexes 1 onward `[code 0x14180AAB0]`, `[code 0x14180AC10]`.
- **Form IDs in the array** use the save's own plugin numbering. The loader turns each one into the current load order: top byte `FE` = light plugin with its index in bits 12–23, top byte `FD` = medium plugin with its index in bits 16–23, anything else = a full plugin by its top byte `[code 0x14180ADE0]`. This matches the public plugin rules (xEdit, Mutagen). An entry whose plugin isn't loaded still takes up its index.
- `[file]`: in every sample save, every kind-0 refid with a nonzero payload is between 1 and the array's count. In Save44 the array holds 62,440 IDs (4 + 62,440 × 4 = 249,764 bytes, the section's size).

## The type directory (location slot `[11]`)

The type directory gives each change record its save form type. The change records themselves don't carry it.

- **Layout:** a VSVAL group count, then for each group: a u8 save form type and a VSVAL run length `[code 0x141815620]` (loader), `[code 0x141814220]` (writer).
- **Meaning:** the groups are in record order. The first run's records are the first `run` change records, and so on. The writer sorts the records first and starts a new group whenever the type changes `[code 0x141814220]`.
- The loader reads the directory only for save versions above 0x46 (70). For older saves it gets the types by reading every record header first `[code 0x141815620]`. Save version 153 has a directory.
- `[file]`: in all 13 sample saves the runs add up exactly to slot `[9]` (57,397 to 57,404 records, 29 groups in Save44).

## Save form types (fact 13)

The u8 in the type directory is a save form type, not the engine's form-type number. The loader turns it into an engine form type through a fixed table of 59 entries `[code table 0x144A53290]`. Values 0x3B and above map to "none". The writer uses the reverse table, which the game fills at startup. The names come from CommonLibSF's `FormType` enum, which uses the same engine numbers.

| Save type | Record | Save type | Record | Save type | Record |
|---|---|---|---|---|---|
| 0 | REFR | 20 | MSTT | 40 | PCON |
| 1 | ACHR | 21 | FURN | 41 | FLST |
| 2 | PMIS | 22 | WEAP | 42 | LVLN |
| 3 | PGRE | 23 | AMMO | 43 | LVLI |
| 4 | PBEA | 24 | KEYM | 44 | LVSP |
| 5 | PFLA | 25 | ALCH | 45 | PARW |
| 6 | CELL | 26 | IDLM | 46 | ENCH |
| 7 | INFO | 27 | NOTE | 47 | TERM |
| 8 | QUST | 28 | CLAS | 48 | INNR |
| 9 | NPC_ | 29 | FACT | 49 | AFFE |
| 10 | ACTI | 30 | PACK | 50 | LVLP |
| 11 | TACT | 31 | NAVM | 51 | LVSC |
| 12 | ARMO | 32 | WOOP | 52 | LVLB |
| 13 | BOOK | 33 | MGEF | 53 | PPLA |
| 14 | CONT | 34 | SMQN | 54 | PEMI |
| 15 | DOOR | 35 | SCEN | 55 | PCMT |
| 16 | INGR | 36 | LCTN | 56 | PCCN |
| 17 | LIGH | 37 | RELA | 57 | PCBN |
| 18 | MISC | 38 | PHZD | 58 | engine form type 216 (not named in CommonLibSF) |
| 19 | STAT | 39 | PBAR | | |

- So save form type 0 = placed objects (REFR) and 1 = actors (ACHR). Records of these types with refid kind 2 are objects and actors created during play.
- `[file]`: on 2026-10-26 the forms of type 8 records were looked up in the plugins and were all `QUST`, and types 7, 34 and 35 were `INFO`, `SMQN` and `SCEN`. All four match this table.

## The change records (location slots `[4]` to `[5]`)

There are slot `[9]` records, one after another, from slot `[4]` to exactly slot `[5]`. Each one is:

1. **Flags**, u8. Each bit says whether an optional field is present `[code 0x14185E180]` (loader), `[code 0x14185DFF0]` (writer).
2. If flag bit 0 (0x01): a **u8 record version**. If absent, the record's version is the save version. The writer adds this byte only when they differ.
3. The record's own **refid**, 3 bytes (see Refids).
4. If flag bit 1 (0x02): **u32 change flags**. **If absent, the change flags are 0x80008000**, not 0: the writer leaves the field out exactly when the value is 0x80008000, and the loader fills that value in.
5. If flag bit 3 (0x08): **VSVAL reference count**. If absent, 0.
6. If flag bit 4 (0x10): **VSVAL string count**. If absent, 0.
7. If flag bit 5 (0x20): **VSVAL data size**. If absent, 0.
8. The **references**: reference-count refids, 3 bytes each `[code 0x141863E00]`.
9. The **string indexes**: string-count u16 values `[code 0x141863E00]`. They point into the save's string table, which is held in global data type 0.
10. The **data**: data-size bytes. For save version 153 the data is stored as is. The loader has a decompression path, but the version-153 header has no field for an uncompressed size `[code 0x141864B20]`.

Other flag bits:

- Bit 2 (0x04) is neither written nor read by the game `[code 0x14185DFF0]`, `[code 0x14185E180]`. It adds no field.
- Bits 6 and 7 aren't used. A reader should refuse a record with them set: that most likely means the reader is lost.

The loader chooses the header reader by save version. 0x141863E00 (which uses 0x14185E180) is the one for save versions 0x4A (74) and above, so it covers 153. Older saves use other readers `[code 0x141815620]`.

When the game loads a record's data, the record's references come from the reference list in order (each "read a form" step in the record's loader takes the next one), not from the data bytes `[code 0x1418650D0]`. This is the rule the quest-state decoding relies on.

`[file]`: walking all 13 sample saves with exactly these rules reads slot `[9]` records and ends exactly at slot `[5]` in every save (checked on 2026-11-10 with `SaveParser\work\wave2\walk_from_code.py`, written from this file and not from any other parser). In those saves:

- flag bits 0, 2, 6 and 7 never occur;
- about 5,350 records per save have no change-flags field (so their flags are 0x80008000). They're all save types 0, 1 and 38 (REFR, ACHR, PHZD). Every quest record (type 8) has the field.

## Quest state (the data of save type 8 records)

Quest records (save form type 8, `QUST`) hold the quest's state. The layout comes from the game's quest save and load functions: `[code 0x140D0F2E0]` (save), `[code 0x140D0FF00]` (load) and the base form's save `[code 0x1405DF980]`. They were read on 2026-10-26, and the block order and the sizes of the simple blocks were checked again in the loader on 2026-11-10. The meanings of the quest flags and objective states come from the game's Papyrus functions (IsRunning, IsCompleted, IsObjectiveDisplayed and so on, registered at `[code 0x1420CCAF0]`), which test the same fields that the save writes.

**References aren't in the data bytes.** Every place below that names a form takes the next refid from the record's reference list, in order (see "The change records"). The first reference is always the quest itself.

The data has one block for each change-flag bit that is set, in this order:

| Change-flag bit | Block |
|---|---|
| 0 (0x1) | base form data: u32 form flags, then a u16 |
| 1 (0x2) | u32 quest flags: 0x1 = enabled, 0x2 = completed, 0x80 = start or stop pending, 0x800 = active (the tracked quest). Other bits aren't identified. |
| 2 (0x4) | a u32 (meaning not identified) |
| 31 (0x80000000) | **stages**: VSVAL count, then for each one a u16 stage index and a u8 "done" flag (nonzero = done). The game sets the quest's current stage to the highest stage marked done `[code 0x140D0FF00]`. |
| 29 (0x20000000) | **objectives**: VSVAL count, then for each one three u32: objective index, state, value. States: 1 = shown, 2 = completed, 3 = completed and shown, 4 = failed, 5 = failed and shown. |
| 28 (0x10000000) | **aliases**: u8 "has instance" flag; u16 count, then that many references; u32 count, then for each alias a u32 alias ID, a u8 "created" flag, one reference, and 3 more references if the flag is set; u32 count, then for each a u32 ID and a reference; then a u8, and if it's nonzero the story event that started the quest: u32 type, u32 field, u32 value count, then for each value a u32 kind followed by a reference (kinds 0, 1, 2, 4 and 5) or a u32 (kind 3). |
| 27 (0x8000000) | u32, then VSVAL count, then for each entry: a u32; a VSVAL count of (u32, reference, reference, u32); a VSVAL count of (reference, u32); a u16; a u8 |
| 26 (0x4000000) | a u8 |
| 25 (0x2000000) | a u8, then two u32 |
| 24 (0x1000000) | a VSVAL |

Status rules from the Papyrus functions: enabled + pending = starting; enabled = running; pending only = stopping; neither = stopped. "Running" and "starting" also depend on fields that aren't saved, so a quest shown as running may still be starting.

`[file]`: on all 13 PC saves, 798 of 798 quest records decode with the data bytes and the reference list both used up exactly. In those saves every quest record has the change-flags field. `[in game]`: confirmed on 2026-11-10 by a controlled pair of saves (one faction questline finished in game; all 14 of its quests read as done at stage 10000, matching the game's script log).

## Not in this file

- Global data entries (left out of public 0.1). One lead, not checked: the loader treats global data type 0 as the string table and type 2 as the form-ID remap table, and neither has the usual length field `[code 0x141824020]`. That would explain the odd "length" seen for type 0 on 2026-10-21.
- The data inside records of other types (ships, inventories, placed objects).
- The data after the type directory.

## Credits

GhidraSQL v0.0.8 (Elias Bachaalany) was used read-only to query the Ghidra project. Engine form-type names: CommonLibSF. Container and header field names: StarfieldSaveTool (insomnious). VSVAL and refid scheme for the older games: UESP.
