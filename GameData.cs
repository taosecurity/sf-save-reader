using System.IO.Compression;
using System.Text;

namespace SfsReader;

/// <summary>A plugin's own record, found by <see cref="GameData"/>.</summary>
/// <param name="Plugin">The plugin file name, as asked for.</param>
/// <param name="LocalId">The form number inside the plugin (as in <see cref="FormRef.LocalId"/>).</param>
/// <param name="FileFormId">The form ID as written in the plugin's record header.</param>
/// <param name="Signature">Record type, for example QUST.</param>
/// <param name="BasePlugin">For a placed reference (a NAME subrecord: REFR, ACHR and the other placed types), the
/// plugin of its base object; empty otherwise.</param>
/// <param name="BaseLocalId">The base object's form number in <paramref name="BasePlugin"/>.</param>
public sealed record PluginRecord(string Plugin, uint LocalId, uint FileFormId, string Signature, string EditorId, string Name,
                                  string BasePlugin = "", uint BaseLocalId = 0);

/// <summary>
/// Looks forms up in the plugins of a Starfield Data folder, read-only. Only a plugin's own records are kept
/// (not its overrides of master records). Plugin layout: public plugin rules (UESP, xEdit, Mutagen): a record header
/// is type, data size, flags, form ID, version control, version (24 bytes); groups are "GRUP" with a 24-byte header;
/// flag 0x40000 means the data is a u32 size and a zlib stream; the TES4 header lists the masters (MAST) and its
/// flags say light (0x100), medium (0x400) and localized (0x80).
/// <para>Form IDs inside a plugin number its masters per kind: full masters by the top byte, medium masters as
/// FD + index (bits 16-23), light masters as FE + index (bits 12-23). An index at or past the number of masters of
/// that kind is the plugin's own record, in any of the three styles (Genesis 8.8.41 has full plugins that number
/// their own records one past their master count, and medium plugins that use the full style).</para>
/// </summary>
public sealed class GameData(string dataDir, string language = "en")
{
    public string DataDir { get; } = dataDir;
    public string Language { get; } = string.IsNullOrWhiteSpace(language) ? "en"
        : language.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !language.Contains("..") && !language.Contains('/') && !language.Contains('\\')
            ? language
            : throw new ArgumentException($"--lang \"{language}\" isn't a language code; give one such as en, de or fr");

    /// <summary>Whether the folder holds a file with this name, in any spelling (for checks before a GameData exists,
    /// such as "does this folder hold Starfield.esm").</summary>
    public static bool HasFile(string dir, string name)
    {
        if (File.Exists(Path.Combine(dir, name))) return true;
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir)
                .Any(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    public List<string> Warnings { get; } = [];
    /// <summary>Optional troubleshooting log (sf-save-reader --verbose / --log): which plugins were scanned, how many
    /// records matched, and where names came from. Null = no logging.</summary>
    public Action<string>? Log { get; set; }

    readonly Dictionary<string, bool> exists = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, PluginInfo?> infos = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<uint, PluginRecord?>> asked = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<uint, string>?> strings = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> warned = [];

    /// <summary>Whether the Data folder holds this plugin file.</summary>
    public bool HasPlugin(string plugin)
    {
        if (string.IsNullOrEmpty(plugin) || plugin.StartsWith('?') || plugin.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        if (!exists.TryGetValue(plugin, out bool e))
            exists[plugin] = e = FindFile(DataDir, plugin) != null;
        return e;
    }

    // File names matched without regard to case. Windows does this anyway; on Linux (and Proton) the file system is
    // case-sensitive, while a save or a plugin's master list may spell a name differently from the file on disk.
    // Each folder is listed once per GameData: the lookups are a snapshot of the Data folder, so a caller that
    // installs files while it keeps a GameData object should make a new one.
    readonly Dictionary<string, Dictionary<string, string>?> listings = new(StringComparer.Ordinal);

    Dictionary<string, string>? Listing(string dir)
    {
        if (listings.TryGetValue(dir, out var l)) return l;
        l = null;
        try
        {
            if (Directory.Exists(dir))
            {
                l = new(StringComparer.OrdinalIgnoreCase);
                // Sorted, so when two files differ only in case (possible on Linux) the same one wins on every run.
                foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
                    if (!l.TryAdd(Path.GetFileName(f), f))
                        Warn($"{dir}: {Path.GetFileName(l[Path.GetFileName(f)])} and {Path.GetFileName(f)} differ only in case; using the first");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            l = null;
            Warn($"{dir}: can't list the folder ({e.Message}); files are found only by their exact name");
        }
        listings[dir] = l;
        return l;
    }

    /// <summary>The file's path in the folder, exact spelling first, then any spelling; null if it isn't there.</summary>
    string? FindFile(string dir, string name)
    {
        string exact = Path.Combine(dir, name);
        if (File.Exists(exact)) return exact;
        return Listing(dir)?.GetValueOrDefault(name);
    }

    string PluginPath(string plugin) => FindFile(DataDir, plugin) ?? Path.Combine(DataDir, plugin);

    // The loose Strings folder, any spelling ("Strings", "strings").
    string? stringsDir;
    bool stringsDirLooked;
    string? StringsDir()
    {
        if (stringsDirLooked) return stringsDir;
        stringsDirLooked = true;
        string exact = Path.Combine(DataDir, "Strings");
        if (Directory.Exists(exact)) return stringsDir = exact;
        try
        {
            stringsDir = Directory.Exists(DataDir)
                ? Directory.EnumerateDirectories(DataDir)
                           .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Strings", StringComparison.OrdinalIgnoreCase))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { stringsDir = null; }
        return stringsDir;
    }

    /// <summary>Reads the plugin's own records with these form numbers (one pass over the file for the ones not
    /// asked for before). With <paramref name="warnMissing"/>, each number the plugin doesn't have is a warning.</summary>
    public void Load(string plugin, IEnumerable<uint> localIds, bool warnMissing = true)
    {
        if (!HasPlugin(plugin)) { Warn($"{plugin}: not in {DataDir}"); return; }
        if (!asked.TryGetValue(plugin, out var known)) asked[plugin] = known = [];
        var want = localIds.Where(id => !known.ContainsKey(id)).ToHashSet();
        if (want.Count == 0) return;
        var info = Info(plugin);
        if (info == null) { foreach (var id in want) known[id] = null; return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Scan(plugin, info, want, known);
            Log?.Invoke($"{plugin}: scanned for {want.Count} form(s), found {want.Count(id => known.GetValueOrDefault(id) != null)}, " +
                        $"{clock.ElapsedMilliseconds} ms");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Warn($"{plugin}: can't read its records ({e.Message})");
        }
        foreach (var id in want)
        {
            known.TryAdd(id, null);
            if (known[id] == null && warnMissing) Warn($"{plugin}: no record {id:X}");
        }
    }

    /// <summary>The plugin's own record for this form, or null (no plugin file, not in it, created, or no form).</summary>
    public PluginRecord? Get(FormRef f)
    {
        if (f.IsNull || f.IsCreated) return null;
        return Get(f.Plugin, f.LocalId);
    }

    /// <summary>"Name  [EditorID]" for a form found in its plugin; "Name" when it has no editor ID and
    /// "(no name)  [EditorID]" when it has no name; empty when the form isn't found or has neither.
    /// A placed reference takes a missing name or editor ID from its base object, and when both have an editor ID
    /// it shows "[RefEditorID -> BaseEditorID]". (Keep this text stable: report tools built on the reader compare it.)</summary>
    public string Describe(FormRef f)
    {
        var r = Get(f);
        if (r == null) return "";
        string name = r.Name, edid = r.EditorId;
        if (r.BasePlugin.Length > 0 && Get(r.BasePlugin, r.BaseLocalId) is { } b)
        {
            if (name.Length == 0) name = b.Name;
            if (edid.Length == 0) edid = b.EditorId;
            else if (b.EditorId.Length > 0) edid = $"{edid} -> {b.EditorId}";
        }
        return (name.Length > 0, edid.Length > 0) switch
        {
            (true, true) => $"{name}  [{edid}]",
            (true, false) => name,
            (false, true) => $"(no name)  [{edid}]",
            _ => "",
        };
    }

    /// <summary>The plugin's own record with this form number, or null.</summary>
    public PluginRecord? Get(string plugin, uint localId)
    {
        if (!HasPlugin(plugin)) return null;
        if (!asked.TryGetValue(plugin, out var known) || !known.ContainsKey(localId))
        {
            Load(plugin, [localId], warnMissing: false);
            known = asked.GetValueOrDefault(plugin);
        }
        return known?.GetValueOrDefault(localId);
    }

    void Warn(string w)
    {
        if (warned.Count < 50 && warned.Add(w)) Warnings.Add(w);
    }

    // ---- plugin header ----

    sealed record PluginInfo(uint Flags, string[] Full, string[] Medium, string[] Light, long FirstGroup)
    {
        public bool Localized => (Flags & 0x80) != 0;
        public int FullMasters => Full.Length;
        public int MediumMasters => Medium.Length;
        public int LightMasters => Light.Length;
    }

    /// <summary>The plugin a form ID written inside <paramref name="plugin"/> belongs to, and its form number.</summary>
    static (string Plugin, uint Local)? Owner(string plugin, PluginInfo info, uint fileFormId)
    {
        if (fileFormId == 0) return null;
        if (OwnLocal(fileFormId, info.FullMasters, info.MediumMasters, info.LightMasters) is uint own) return (plugin, own);
        uint top = fileFormId >> 24;
        return top switch
        {
            0xFD => (info.Medium[(fileFormId >> 16) & 0xFF], fileFormId & 0xFFFF),
            0xFE => (info.Light[(fileFormId >> 12) & 0xFFF], fileFormId & 0xFFF),
            0xFF => null,
            _ => (info.Full[top], fileFormId & 0xFFFFFF),
        };
    }

    PluginInfo? Info(string plugin)
    {
        if (infos.TryGetValue(plugin, out var i)) return i;
        infos[plugin] = null;
        try
        {
            var (flags, masters, end) = ReadHeader(PluginPath(plugin));
            List<string> full = [], medium = [], light = [];
            foreach (var m in masters)
            {
                switch (KindOf(m, plugin))
                {
                    case "medium": medium.Add(m); break;
                    case "light": light.Add(m); break;
                    default: full.Add(m); break;
                }
            }
            Log?.Invoke($"{plugin}: {PluginKind(flags)} plugin{((flags & 0x80) != 0 ? ", localized" : "")}, masters " +
                        $"{full.Count} full / {medium.Count} medium / {light.Count} light");
            return infos[plugin] = new PluginInfo(flags, full.ToArray(), medium.ToArray(), light.ToArray(), end);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Warn($"{plugin}: can't read its header ({e.Message})");
            return null;
        }
    }

    /// <summary>"full", "medium" or "light" from the TES4 flags. A master that isn't in the Data folder counts as full
    /// (with a warning): the game wouldn't load the plugin then anyway.</summary>
    string KindOf(string master, string forPlugin)
    {
        if (!HasPlugin(master))
        {
            Warn($"{forPlugin}: master {master} isn't in the Data folder; counted as a full plugin");
            return "full";
        }
        try
        {
            return PluginKind(ReadHeader(PluginPath(master)).Flags);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Warn($"{forPlugin}: can't read the header of its master {master} ({e.Message}); counted as a full plugin");
            return "full";
        }
    }

    /// <summary>Plugin kind from the TES4 header flags: medium (0x400), light (0x100) or full.</summary>
    public static string PluginKind(uint flags) => (flags & 0x400) != 0 ? "medium" : (flags & 0x100) != 0 ? "light" : "full";

    static (uint Flags, string[] Masters, long End) ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var h = new byte[24];
        fs.ReadExactly(h);
        if (Encoding.ASCII.GetString(h, 0, 4) != "TES4")
            throw new InvalidDataException("not a plugin (no TES4 header)");
        uint size = BitConverter.ToUInt32(h, 4), flags = BitConverter.ToUInt32(h, 8);
        if (size > fs.Length - 24) throw new InvalidDataException("TES4 header runs past the end of the file");
        var data = new byte[size];
        fs.ReadExactly(data);
        var masters = new List<string>();
        foreach (var (type, at, len) in Subrecords(data))
            if (type == "MAST") masters.Add(ZString(data, at, len));
        return (flags, masters.ToArray(), 24 + size);
    }

    /// <summary>Is this form ID (as written in the plugin) one of the plugin's own records? Returns its form number.</summary>
    public static uint? OwnLocal(uint fileFormId, int fullMasters, int mediumMasters, int lightMasters)
    {
        uint top = fileFormId >> 24;
        if (top == 0xFD) return ((fileFormId >> 16) & 0xFF) >= mediumMasters ? fileFormId & 0xFFFF : null;
        if (top == 0xFE) return ((fileFormId >> 12) & 0xFFF) >= lightMasters ? fileFormId & 0xFFF : null;
        if (top == 0xFF) return null;
        return top >= fullMasters ? fileFormId & 0xFFFFFF : null;
    }

    // ---- record scan ----

    void Scan(string plugin, PluginInfo info, HashSet<uint> want, Dictionary<uint, PluginRecord?> known)
    {
        string path = PluginPath(plugin);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        long len = fs.Length, pos = info.FirstGroup;
        var h = new byte[24];
        int left = want.Count;
        while (pos + 24 <= len && left > 0)
        {
            fs.Position = pos;
            fs.ReadExactly(h);
            uint size = BitConverter.ToUInt32(h, 4);
            if (h[0] == 'G' && h[1] == 'R' && h[2] == 'U' && h[3] == 'P')
            {
                if (size < 24) throw new InvalidDataException($"group at 0x{pos:X} has size {size}");
                pos += 24; // the group's records follow its header
                continue;
            }
            uint rflags = BitConverter.ToUInt32(h, 8), fid = BitConverter.ToUInt32(h, 12);
            long next = pos + 24 + size;
            if (next > len) throw new InvalidDataException($"record at 0x{pos:X} runs past the end of the file");
            if (OwnLocal(fid, info.FullMasters, info.MediumMasters, info.LightMasters) is uint local && want.Contains(local)
                && !known.ContainsKey(local))
            {
                var data = new byte[size];
                fs.ReadExactly(data);
                string sig = Encoding.ASCII.GetString(h, 0, 4);
                var (edid, full, bp, bl) = Names(plugin, info, sig, rflags, data);
                known[local] = new PluginRecord(plugin, local, fid, sig, edid, full, bp, bl);
                left--;
            }
            pos = next;
        }
    }

    (string EditorId, string Name, string BasePlugin, uint BaseLocal) Names(string plugin, PluginInfo info, string sig, uint rflags, byte[] data)
    {
        if ((rflags & 0x40000) != 0)
        {
            if (data.Length < 4) return ("", "", "", 0);
            int outSize = BitConverter.ToInt32(data, 0);
            if (outSize < 0 || outSize > 256 << 20) return ("", "", "", 0);
            var outBuf = new byte[outSize];
            try
            {
                using var z = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
                z.ReadExactly(outBuf);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
            {
                Warn($"{plugin}: a compressed {sig} record doesn't decompress ({e.Message})");
                return ("", "", "", 0);
            }
            data = outBuf;
        }
        string edid = "", name = "";
        uint baseId = 0;
        bool placed = PlacedTypes.Contains(sig);
        foreach (var (type, at, len) in Subrecords(data))
        {
            if (type == "EDID" && edid.Length == 0) edid = ZString(data, at, len);
            else if (type == "FULL" && name.Length == 0)
                name = info.Localized ? (len >= 4 ? Lookup(plugin, BitConverter.ToUInt32(data, at)) : "") : ZString(data, at, len);
            else if (type == "NAME" && placed && baseId == 0 && len >= 4) baseId = BitConverter.ToUInt32(data, at);
        }
        var owner = baseId == 0 ? null : Owner(plugin, info, baseId);
        return (edid, name, owner?.Plugin ?? "", owner?.Local ?? 0);
    }

    /// <summary>Placed-object record types, whose NAME subrecord is the base object's form ID.</summary>
    static readonly HashSet<string> PlacedTypes = ["REFR", "ACHR", "PGRE", "PMIS", "PBEA", "PFLA", "PHZD", "PARW", "PBAR", "PCON"];

    static IEnumerable<(string Type, int At, int Len)> Subrecords(byte[] d)
    {
        int pos = 0;
        uint big = 0;
        while (pos + 6 <= d.Length)
        {
            string type = Encoding.ASCII.GetString(d, pos, 4);
            int len = BitConverter.ToUInt16(d, pos + 4);
            pos += 6;
            if (type == "XXXX" && len == 4 && pos + 4 <= d.Length) { big = BitConverter.ToUInt32(d, pos); pos += 4; continue; }
            if (big != 0) { len = (int)Math.Min(big, int.MaxValue); big = 0; }
            if (pos + len > d.Length) yield break;
            yield return (type, pos, len);
            pos += len;
        }
    }

    static string ZString(byte[] d, int at, int len)
    {
        int end = Array.IndexOf(d, (byte)0, at, len);
        return Encoding.UTF8.GetString(d, at, (end < 0 ? at + len : end) - at);
    }

    // ---- localized strings ----

    string Lookup(string plugin, uint id)
    {
        if (id == 0) return "";
        if (!strings.TryGetValue(plugin, out var table))
            strings[plugin] = table = LoadStrings(plugin);
        return table != null && table.TryGetValue(id, out var s) ? s : "";
    }

    /// <summary>The plugin's .STRINGS table for the language: a loose Strings\&lt;plugin&gt;_&lt;lang&gt;.strings file first, then the
    /// plugin's own BA2 archives ("&lt;plugin&gt; - Localization.ba2" first).</summary>
    Dictionary<uint, string>? LoadStrings(string plugin)
    {
        string baseName = Path.GetFileNameWithoutExtension(plugin);
        string file = $"{baseName}_{Language}.strings";
        try
        {
            if (StringsDir() is string sdir && FindFile(sdir, file) is string loose)
            {
                Log?.Invoke($"{plugin}: names from loose {loose}");
                return ParseStrings(File.ReadAllBytes(loose));
            }
            var archives = (Listing(DataDir)?.Values ?? Enumerable.Empty<string>())
                .Where(p => Path.GetFileName(p) is string n && n.StartsWith($"{baseName} - ", StringComparison.OrdinalIgnoreCase)
                            && n.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetFileName(p).EndsWith(" - Localization.ba2", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var ba2 in archives)
            {
                byte[]? bytes;
                try { bytes = Ba2.ReadFile(ba2, "strings/" + file); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // One unreadable archive (an unknown BA2 version, say) doesn't stop the search in the others.
                    Log?.Invoke($"{plugin}: skipped {Path.GetFileName(ba2)} ({e.Message})");
                    continue;
                }
                if (bytes != null)
                {
                    Log?.Invoke($"{plugin}: names from {Path.GetFileName(ba2)} (strings/{file})");
                    return ParseStrings(bytes);
                }
            }
            Warn($"{plugin}: no {file} (loose or in its BA2s); names are left out");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Warn($"{plugin}: can't read {file} ({e.Message}); names are left out");
        }
        return null;
    }

    /// <summary>.STRINGS: u32 count, u32 data size, count × (u32 id, u32 offset), then null-terminated strings at
    /// the offsets (counted from the start of the data).</summary>
    public static Dictionary<uint, string> ParseStrings(byte[] b)
    {
        var t = new Dictionary<uint, string>();
        if (b.Length < 8) return t;
        uint n = BitConverter.ToUInt32(b, 0);
        long dataAt = 8 + 8L * n;
        if (dataAt > b.Length) throw new InvalidDataException("strings table directory runs past the end of the file");
        for (int i = 0; i < n; i++)
        {
            uint id = BitConverter.ToUInt32(b, 8 + 8 * i), off = BitConverter.ToUInt32(b, 12 + 8 * i);
            long at = dataAt + off;
            if (at >= b.Length) continue;
            int end = Array.IndexOf(b, (byte)0, (int)at);
            t[id] = Encoding.UTF8.GetString(b, (int)at, (end < 0 ? b.Length : end) - (int)at);
        }
        return t;
    }
}

/// <summary>Reads one file from a general (GNRL) BA2 archive, read-only. Layout from public docs (UESP, BSArch):
/// "BTDX", u32 version, "GNRL", u32 file count, u64 name-table offset; versions 2 and 3 add 8 bytes, version 3 a u32
/// compression method (0 = zlib) after them; then 36 bytes per file: u32 name hash, 4-byte extension, u32 folder
/// hash, u32 flags, u64 offset, u32 packed size (0 = stored), u32 size, u32 padding. The name table holds a u16 length
/// and the path for each file.</summary>
public static class Ba2
{
    public static byte[]? ReadFile(string archive, string name)
    {
        using var fs = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var h = new byte[36];
        if (fs.Length < 24) return null;
        fs.ReadExactly(h, 0, 24);
        if (Encoding.ASCII.GetString(h, 0, 4) != "BTDX" || Encoding.ASCII.GetString(h, 8, 4) != "GNRL") return null;
        uint version = BitConverter.ToUInt32(h, 4), count = BitConverter.ToUInt32(h, 12);
        long names = BitConverter.ToInt64(h, 16);
        int headerSize = version switch { 1 => 24, 2 => 32, 3 => 36, _ => throw new InvalidDataException($"{Path.GetFileName(archive)}: BA2 version {version} isn't known") };
        uint method = 0;
        if (version == 3) { fs.ReadExactly(h, 24, 12); method = BitConverter.ToUInt32(h, 32); }
        if (names <= 0 || names > fs.Length) throw new InvalidDataException($"{Path.GetFileName(archive)}: name table offset is past the end");

        fs.Position = names;
        int found = -1;
        var nb = new byte[2];
        for (int i = 0; i < count && found < 0; i++)
        {
            if (fs.Read(nb, 0, 2) < 2) break;
            int len = BitConverter.ToUInt16(nb, 0);
            var s = new byte[len];
            fs.ReadExactly(s);
            if (string.Equals(Encoding.UTF8.GetString(s).Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase)) found = i;
        }
        if (found < 0) return null;

        var e = new byte[36];
        fs.Position = headerSize + 36L * found;
        fs.ReadExactly(e);
        long offset = BitConverter.ToInt64(e, 16);
        uint packed = BitConverter.ToUInt32(e, 24), size = BitConverter.ToUInt32(e, 28);
        if (offset < 0 || offset + (packed == 0 ? size : packed) > fs.Length)
            throw new InvalidDataException($"{Path.GetFileName(archive)}: {name} runs past the end of the archive");
        fs.Position = offset;
        var outBuf = new byte[size];
        if (packed == 0) { fs.ReadExactly(outBuf); return outBuf; }
        if (method != 0) throw new InvalidDataException($"{Path.GetFileName(archive)}: compression method {method} isn't supported (zlib only)");
        var comp = new byte[packed];
        fs.ReadExactly(comp);
        using var z = new ZLibStream(new MemoryStream(comp), CompressionMode.Decompress);
        z.ReadExactly(outBuf);
        return outBuf;
    }
}
