using System.Text;

namespace SfsReader;

public sealed record Plugin(string Name, string Kind, bool HasExtra, string? CreationName, string? CreationId,
    string? Flags, byte? AchievementFriendly);

public sealed record Section(string Name, long Offset, long Size);

/// <summary>
/// The decompressed save ("SFS_SAVEGAME"). Field names follow StarfieldSaveTool where it names them;
/// the location table and sections come from this project's own analysis
/// (section labels use Fallout 4's names and are an inference).
/// </summary>
public sealed class SaveGame
{
    public string Magic { get; private set; } = "";
    public uint HeaderSize { get; private set; }
    public uint EngineVersion { get; private set; }
    public byte SaveVersion { get; private set; }
    /// <summary>Game code writes the manager's manual-save counter here for every save type (0x14182FD70).</summary>
    public uint SaveNumber { get; private set; }
    public string PlayerName { get; private set; } = "";
    public uint PlayerLevel { get; private set; }
    public string Location { get; private set; } = "";
    /// <summary>The string exactly as the game stores it: two forms joined, e.g. "0d.5h.6m.0 days.5 hours.6 minutes".</summary>
    public string Playtime { get; private set; } = "";
    /// <summary>Playtime for display: "0 days 5 hours 6 minutes" from the short form; the raw string if it doesn't match.</summary>
    public string PlaytimeText
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(Playtime, @"^(\d+)d\.(\d+)h\.(\d+)m(?:\.|$)");
            return m.Success ? $"{m.Groups[1].Value} days {m.Groups[2].Value} hours {m.Groups[3].Value} minutes" : Playtime;
        }
    }
    public string Race { get; private set; } = "";
    public ushort Gender { get; private set; }
    public float Experience { get; private set; }
    public float ExperienceRequired { get; private set; }
    public DateTime SaveTimeUtc { get; private set; }
    public byte SaveVersion2 { get; private set; }
    public string CurrentGameVersion { get; private set; } = "";
    public string CreatedGameVersion { get; private set; } = "";
    public uint PluginInfoSize { get; private set; }
    public List<Plugin> Plugins { get; } = [];
    /// <summary>Unnamed u16 between the plugin list and the location table (seen as 1, and 4 in one version-122 save).</summary>
    public ushort? PostPluginUnknown { get; private set; }
    public long LocationTableOffset { get; private set; }
    public uint[] LocationTable { get; private set; } = [];
    public uint GlobalData1Count { get; private set; }
    public uint GlobalData2Count { get; private set; }
    public uint GlobalData3Count { get; private set; }
    public uint ChangeFormCount { get; private set; }
    public List<Section> Sections { get; } = [];
    public List<string> Warnings { get; } = [];
    public long InnerSize { get; private set; }

    /// <summary>The newest save version this reader knows (FORMAT.md: Starfield 1.16.244). A newer one is refused,
    /// because its layout may have changed.</summary>
    public const byte NewestKnownSaveVersion = 153;

    public static SaveGame Parse(byte[] d)
    {
        var g = new SaveGame { InnerSize = d.LongLength };
        var r = new Reader(d);
        g.Magic = r.Ascii(12);
        if (g.Magic != "SFS_SAVEGAME")
            throw new InvalidDataException($"inner magic '{g.Magic}' is not SFS_SAVEGAME");
        g.HeaderSize = r.U32();
        long headerStart = r.Pos;
        g.EngineVersion = r.U32();
        g.SaveVersion = r.U8();
        if (g.SaveVersion > NewestKnownSaveVersion)
            throw new InvalidDataException($"save version {g.SaveVersion} is newer than this reader knows " +
                                           $"(save version {NewestKnownSaveVersion}, Starfield 1.16.244); a newer sf-save-reader is needed");
        g.SaveNumber = r.U32();
        g.PlayerName = r.Str();
        g.PlayerLevel = r.U32();
        g.Location = r.Str();
        g.Playtime = r.Str();
        g.Race = r.Str();
        g.Gender = r.U16();
        g.Experience = r.F32();
        g.ExperienceRequired = r.F32();
        long fileTime = (long)r.U64();
        if (fileTime < 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
            g.Warnings.Add($"save time 0x{fileTime:X} isn't a valid date");
        else
            g.SaveTimeUtc = DateTime.FromFileTimeUtc(fileTime);
        r.Pos = headerStart + g.HeaderSize; // skip the remaining header bytes the size field covers

        g.SaveVersion2 = r.U8();
        g.CurrentGameVersion = r.Str();
        g.CreatedGameVersion = r.Str();
        g.PluginInfoSize = r.U16();
        r.U16(); // padding (StarfieldSaveTool: 2 bytes)
        long pluginStart = r.Pos;
        int full = r.U8();
        for (int i = 0; i < full; i++) g.Plugins.Add(ReadPlugin(r, "full", g.SaveVersion2));
        int light = r.U16();
        for (int i = 0; i < light; i++) g.Plugins.Add(ReadPlugin(r, "light", g.SaveVersion2));
        // The medium list (and the u16 after it) starts at save version 109: 107 has neither, 109-119 have both,
        // which StarfieldSaveTool (>= 122) misses. No version-108 save has been seen.
        if (g.SaveVersion2 >= 109)
        {
            uint medium = r.U32();
            for (int i = 0; i < medium; i++) g.Plugins.Add(ReadPlugin(r, "medium", g.SaveVersion2));
        }

        // The location table follows the plugin block; the size field is measured from after the padding.
        g.LocationTableOffset = pluginStart + g.PluginInfoSize;
        // PluginInfoSize is a u16. If the plugin entries end a whole multiple of 0x10000 bytes past the stated
        // end (a very large plugin list), the field has wrapped: trust where the entries really end.
        foreach (long end in new[] { r.Pos, r.Pos + 2 })
        {
            long over = end - g.LocationTableOffset;
            if (over > 0 && over % 0x10000 == 0)
            {
                g.Warnings.Add($"PluginInfoSize 0x{g.PluginInfoSize:X} looks wrapped past 65,535 bytes; using the end of the plugin list (0x{end:X})");
                g.LocationTableOffset = end;
                break;
            }
        }
        // Save versions 109 and above have 2 more bytes here than StarfieldSaveTool's layout; meaning unknown.
        if (g.LocationTableOffset - r.Pos == 2)
            g.PostPluginUnknown = r.U16();
        if (r.Pos != g.LocationTableOffset)
            g.Warnings.Add($"plugin list ended at 0x{r.Pos:X}, expected 0x{g.LocationTableOffset:X} from PluginInfoSize");
        r.Pos = g.LocationTableOffset;
        g.LocationTable = new uint[25];
        for (int i = 0; i < 25; i++) g.LocationTable[i] = r.U32();
        g.GlobalData1Count = g.LocationTable[6];
        g.GlobalData2Count = g.LocationTable[7];
        g.GlobalData3Count = g.LocationTable[8];
        g.ChangeFormCount = g.LocationTable[9];

        var starts = new List<(string Name, long Off)>
        {
            ("header+plugins", 0),
            ("location table", g.LocationTableOffset),
            ("global data 1", g.LocationTable[2]),
            ("global data 2", g.LocationTable[3]),
            ("change forms", g.LocationTable[4]),
            ("global data 3", g.LocationTable[5]),
            ("form ID array", g.LocationTable[0]),
            ("table 3", g.LocationTable[1]),
        };
        // OrderBy is stable, so two slots with the same offset keep the order above (and the same labels) in every save.
        starts = starts.OrderBy(s => s.Off).ToList();
        for (int i = 0; i < starts.Count; i++)
        {
            if (starts[i].Off > d.LongLength) g.Warnings.Add($"{starts[i].Name} offset 0x{starts[i].Off:X} is past the end");
            // Clamp to the data, so a bad slot gives size 0 (never a negative size or a section past the end).
            long off = Math.Min(starts[i].Off, d.LongLength);
            long end = Math.Min(i + 1 < starts.Count ? starts[i + 1].Off : d.LongLength, d.LongLength);
            g.Sections.Add(new Section(starts[i].Name, off, Math.Max(0, end - off)));
        }
        return g;
    }

    /// <summary>Official plugins of the 1.12 era. In save versions 122 to 139 every other plugin carries the
    /// Creation block with no flag byte in front (StarfieldSaveTool's rule; checked on a version-122 save with mods).</summary>
    static readonly HashSet<string> NativePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Starfield.esm", "Constellation.esm", "OldMars.esm", "BlueprintShips-Starfield.esm",
        "SFBGS003.esm", "SFBGS006.esm", "SFBGS007.esm", "SFBGS008.esm",
    };

    static Plugin ReadPlugin(Reader r, string kind, byte saveVersion)
    {
        string name = r.Str();
        if (saveVersion < 122) return new Plugin(name, kind, false, null, null, null, null);
        bool extra = saveVersion >= 140 ? r.U8() != 0 : !NativePlugins.Contains(name);
        if (!extra) return new Plugin(name, kind, false, null, null, null, null);
        string creationName = r.Str();
        string creationId = r.Str();
        string flags = r.Str();
        byte ach = r.U8();
        return new Plugin(name, kind, true, creationName, creationId, flags, ach);
    }

    /// <summary>Bounds-checked reader: a save that ends early (cut off, or not a save) is an
    /// <see cref="InvalidDataException"/> that says where, never an index error.</summary>
    sealed class Reader(byte[] d)
    {
        public long Pos;
        long Need(long n)
        {
            if (Pos < 0 || n < 0 || Pos + n > d.LongLength)
                throw new InvalidDataException($"the save data ends early: {n} bytes needed at 0x{Pos:X}, but it is only " +
                                               $"0x{d.LongLength:X} bytes long (cut off or damaged save?)");
            long at = Pos;
            Pos += n;
            return at;
        }
        public byte U8() => d[Need(1)];
        public ushort U16() => BitConverter.ToUInt16(d, (int)Need(2));
        public uint U32() => BitConverter.ToUInt32(d, (int)Need(4));
        public ulong U64() => BitConverter.ToUInt64(d, (int)Need(8));
        public float F32() => BitConverter.ToSingle(d, (int)Need(4));
        public string Ascii(int n) => Encoding.ASCII.GetString(d, (int)Need(n), n);
        public string Str() { int n = U16(); return Encoding.UTF8.GetString(d, (int)Need(n), n); }
    }
}
