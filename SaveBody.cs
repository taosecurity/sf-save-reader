namespace SfsReader;

/// <summary>One change record (FORMAT.md, "The change records").</summary>
public sealed class ChangeRecord
{
    /// <summary>Position in the change-record list, counting from 0.</summary>
    public int Index { get; init; }
    /// <summary>The record's flags byte (which optional header fields are present).</summary>
    public byte HeaderFlags { get; init; }
    /// <summary>The record version: its own byte when flag bit 0 is set, otherwise the save version.</summary>
    public byte Version { get; init; }
    /// <summary>The form the record is about.</summary>
    public FormRef Form { get; init; }
    /// <summary>Kind of the record's own refid (2 = a form created during play).</summary>
    public int RefKind => Form.Kind;
    /// <summary>Change flags; 0x80008000 when the field is absent.</summary>
    public uint ChangeFlags { get; init; }
    /// <summary>Whether the change-flags field was stored (flag bit 1).</summary>
    public bool HasChangeFlags { get; init; }
    /// <summary>Save form type from the type directory (FORMAT.md, "Save form types").</summary>
    public byte FormType { get; init; }
    /// <summary>The record's reference list, in order.</summary>
    public IReadOnlyList<FormRef> References { get; init; } = [];
    /// <summary>Indexes into the save's string table.</summary>
    public IReadOnlyList<ushort> StringIndexes { get; init; } = [];
    /// <summary>Offset of the record's data in the decompressed save data.</summary>
    public long DataOffset { get; init; }
    /// <summary>Size of the record's data in bytes.</summary>
    public int DataSize { get; init; }
    /// <summary>Offset of the record's flags byte in the decompressed save data.</summary>
    public long Offset { get; init; }
}

/// <summary>A run of records of one save form type (FORMAT.md, "The type directory").</summary>
public sealed record TypeGroup(byte FormType, uint Count);

/// <summary>
/// The body of a save: the form-ID array, the type directory and the change records, read from the location table.
/// Written from FORMAT.md only; it covers save version 153.
/// </summary>
public sealed class SaveBody
{
    /// <summary>Save versions this reader's body layout is written for (FORMAT.md covers 153).</summary>
    public static readonly byte[] SupportedSaveVersions = [153];

    /// <summary>The form-ID array, in file order: kind-0 payload i names entry i - 1.</summary>
    public uint[] FormIds { get; private init; } = [];
    public List<TypeGroup> TypeGroups { get; } = [];
    public List<ChangeRecord> Records { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>Where the record walk stopped (should equal the location table's slot [5]).</summary>
    public long ChangeFormsEnd { get; private set; }

    /// <param name="d">The decompressed save data (<see cref="SfsContainer.Inner"/>).</param>
    /// <param name="g">The header and plugin list already read from the same data.</param>
    public static SaveBody Parse(byte[] d, SaveGame g)
    {
        if (!SupportedSaveVersions.Contains(g.SaveVersion))
            throw new InvalidDataException($"save version {g.SaveVersion} (game {g.CurrentGameVersion}) isn't supported; " +
                                           $"this reader reads save version {string.Join(", ", SupportedSaveVersions)} only");
        if (g.LocationTable.Length < 12)
            throw new InvalidDataException("the location table wasn't read");
        var t = g.LocationTable;
        foreach (int slot in new[] { 0, 1, 4, 5, 11 })
            if (t[slot] > d.LongLength)
                throw new InvalidDataException($"location table slot [{slot}] = 0x{t[slot]:X} is past the end of the save data (0x{d.LongLength:X})");
        if (t[4] > t[5])
            throw new InvalidDataException($"change forms start (0x{t[4]:X}) after they end (0x{t[5]:X})");

        var body = new SaveBody { FormIds = ReadFormIdArray(d, t[0], t[1]) };
        long arrayEnd = t[0] + 4L + 4L * body.FormIds.Length;
        if (arrayEnd != t[1])
            body.Warnings.Add($"form-ID array ends at 0x{arrayEnd:X}, slot [1] is 0x{t[1]:X}");

        var cur = new Cursor(d, t[11], d.LongLength);
        uint groups = cur.Vsval();
        long typed = 0;
        for (uint i = 0; i < groups; i++)
        {
            byte type = cur.U8();
            uint run = cur.Vsval();
            body.TypeGroups.Add(new TypeGroup(type, run));
            typed += run;
        }
        uint count = t[9];
        if (typed != count)
            body.Warnings.Add($"type directory covers {typed} records, slot [9] says {count}");

        var plugins = new PluginTable(g);
        var types = body.TypeGroups.SelectMany(x => Enumerable.Repeat(x.FormType, (int)Math.Min(x.Count, int.MaxValue)));
        using var typeIt = types.GetEnumerator();
        cur = new Cursor(d, t[4], t[5]);
        for (int i = 0; i < count; i++)
        {
            long at = cur.Pos;
            ChangeRecord rec;
            try { rec = ReadRecord(ref cur, i, g.SaveVersion, typeIt.MoveNext() ? typeIt.Current : (byte)0xFF, body.FormIds, plugins); }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"change record #{i} at 0x{at:X}: {e.Message}", e);
            }
            body.Records.Add(rec);
        }
        body.ChangeFormsEnd = cur.Pos;
        if (cur.Pos != t[5])
            body.Warnings.Add($"change records end at 0x{cur.Pos:X}, slot [5] is 0x{t[5]:X}");
        body.Warnings.AddRange(plugins.Warnings);
        return body;
    }

    static uint[] ReadFormIdArray(byte[] d, long start, long limit)
    {
        var cur = new Cursor(d, start, d.LongLength);
        uint n = cur.U32();
        if (start + 4 + 4L * n > d.LongLength)
            throw new InvalidDataException($"form-ID array at 0x{start:X} claims {n} entries, past the end of the save data");
        var ids = new uint[n];
        for (int i = 0; i < n; i++) ids[i] = cur.U32();
        return ids;
    }

    static ChangeRecord ReadRecord(ref Cursor cur, int index, byte saveVersion, byte formType, uint[] formIds, PluginTable plugins)
    {
        long at = cur.Pos;
        byte flags = cur.U8();
        if ((flags & 0xC0) != 0)
            throw new InvalidDataException($"flags byte 0x{flags:X2} has bit 6 or 7 set (unused), so the reader is lost");
        byte version = (flags & 0x01) != 0 ? cur.U8() : saveVersion;
        var form = plugins.Resolve(cur.RefId(), formIds);
        bool hasChange = (flags & 0x02) != 0;
        uint change = hasChange ? cur.U32() : 0x80008000;
        uint refCount = (flags & 0x08) != 0 ? cur.Vsval() : 0;
        uint strCount = (flags & 0x10) != 0 ? cur.Vsval() : 0;
        uint dataSize = (flags & 0x20) != 0 ? cur.Vsval() : 0;
        cur.Need(3L * refCount, "reference list");
        var refs = new FormRef[refCount];
        for (int i = 0; i < refCount; i++) refs[i] = plugins.Resolve(cur.RefId(), formIds);
        cur.Need(2L * strCount, "string index list");
        var strs = new ushort[strCount];
        for (int i = 0; i < strCount; i++) strs[i] = cur.U16();
        long dataAt = cur.Pos;
        cur.Skip(dataSize, "data");
        return new ChangeRecord
        {
            Index = index, HeaderFlags = flags, Version = version, Form = form, ChangeFlags = change, HasChangeFlags = hasChange,
            FormType = formType, References = refs, StringIndexes = strs, DataOffset = dataAt, DataSize = (int)dataSize, Offset = at,
        };
    }

    /// <summary>Reads a VSVAL (FORMAT.md, "VSVAL"): the low 2 bits of the first byte give the size
    /// (0 = 1 byte, 1 = 2, 2 = 4); the value is the little-endian number shifted right by 2. Tag 3 is an error.</summary>
    public static uint ReadVsval(byte[] d, ref long pos, long end)
    {
        var c = new Cursor(d, pos, end);
        uint v = c.Vsval();
        pos = c.Pos;
        return v;
    }

    /// <summary>Decodes a 3-byte refid (FORMAT.md, "Refids"). Kind 3 is an error.</summary>
    public static FormRef DecodeRefId(uint refId, uint[] formIds, SaveGame g) => new PluginTable(g).Resolve(refId, formIds);

    /// <summary>Turns a form ID in the save's numbering into its plugin and form number (FORMAT.md, "Form ID → plugin").</summary>
    public static FormRef DecodeFormId(uint formId, SaveGame g) => new PluginTable(g).FromFormId(0, formId);

    /// <summary>A bounds-checked little-endian reader over part of the save data.</summary>
    internal struct Cursor(byte[] d, long pos, long end)
    {
        public long Pos = pos;
        readonly long end = end;

        public void Need(long n, string what = "field")
        {
            if (n < 0 || Pos + n > end)
                throw new InvalidDataException($"{what} of {n} bytes at 0x{Pos:X} runs past 0x{end:X}");
        }
        public void Skip(long n, string what) { Need(n, what); Pos += n; }
        public byte U8() { Need(1); return d[Pos++]; }
        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(d, (int)Pos); Pos += 2; return v; }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(d, (int)Pos); Pos += 4; return v; }
        public uint RefId() { Need(3); uint v = (uint)(d[Pos] << 16 | d[Pos + 1] << 8 | d[Pos + 2]); Pos += 3; return v; }
        public uint Vsval()
        {
            Need(1, "VSVAL");
            int tag = d[Pos] & 3;
            switch (tag)
            {
                case 0: return (uint)d[Pos++] >> 2;
                case 1: { Need(2, "VSVAL"); uint v = BitConverter.ToUInt16(d, (int)Pos); Pos += 2; return v >> 2; }
                case 2: { Need(4, "VSVAL"); uint v = BitConverter.ToUInt32(d, (int)Pos); Pos += 4; return v >> 2; }
                default: throw new InvalidDataException($"VSVAL at 0x{Pos:X} has size tag 3, which the game never writes");
            }
        }
    }

    /// <summary>The save's plugin list split by kind, for form ID → plugin.</summary>
    sealed class PluginTable(SaveGame g)
    {
        readonly string[] full = g.Plugins.Where(p => p.Kind == "full").Select(p => p.Name).ToArray();
        readonly string[] light = g.Plugins.Where(p => p.Kind == "light").Select(p => p.Name).ToArray();
        readonly string[] medium = g.Plugins.Where(p => p.Kind == "medium").Select(p => p.Name).ToArray();
        readonly HashSet<string> warned = [];
        public List<string> Warnings { get; } = [];

        public FormRef Resolve(uint refId, uint[] formIds)
        {
            uint payload = refId & 0x3FFFFF;
            switch (refId >> 22)
            {
                case 0:
                    if (payload == 0) return new FormRef { RefId = refId, FormId = 0, Plugin = "", LocalId = 0 };
                    if (payload > formIds.Length)
                    {
                        Warn($"refid {refId:X6} names form-ID array entry {payload}, but the array has {formIds.Length}");
                        return new FormRef { RefId = refId, FormId = 0, Plugin = $"?array {payload}", LocalId = payload };
                    }
                    return FromFormId(refId, formIds[payload - 1]);
                case 1: return FromFormId(refId, payload);
                case 2: return FromFormId(refId, 0xFF000000 | payload);
                default: throw new InvalidDataException($"refid {refId:X6} has kind 3, which the game never writes");
            }
        }

        public FormRef FromFormId(uint refId, uint id)
        {
            uint top = id >> 24;
            if (id == 0) return new FormRef { RefId = refId, FormId = 0, Plugin = "", LocalId = 0 };
            if (top == 0xFF) return new FormRef { RefId = refId, FormId = id, Plugin = "", LocalId = id & 0xFFFFFF };
            (string[] list, uint idx, uint local, string kind) = top switch
            {
                0xFE => (light, (id >> 12) & 0xFFF, id & 0xFFF, "light"),
                0xFD => (medium, (id >> 16) & 0xFF, id & 0xFFFF, "medium"),
                _ => (full, top, id & 0xFFFFFF, "full"),
            };
            if (idx < list.Length) return new FormRef { RefId = refId, FormId = id, Plugin = list[idx], LocalId = local };
            string name = $"?{kind} {idx}";
            Warn($"form {id:X8} names {kind} plugin {idx}, but the save lists {list.Length} {kind} plugins");
            return new FormRef { RefId = refId, FormId = id, Plugin = name, LocalId = local };
        }

        void Warn(string w)
        {
            if (warned.Count < 20 && warned.Add(w)) Warnings.Add(w);
        }
    }
}
