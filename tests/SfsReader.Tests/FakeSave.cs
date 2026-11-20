using System.Text;

namespace SfsReader.Tests;

/// <summary>Builds a made-up decompressed save (the layout of FORMAT.md, save version 153) for the fragment tests.
/// Nothing here comes from a real save: every name, number and record is invented.</summary>
sealed class FakeSave
{
    public byte SaveVersion = 153;
    public List<string> Full = ["Starfield.esm", "Made-Up-Full.esm"];
    public List<string> Light = ["MadeUpLight0.esm", "MadeUpLight1.esm"];
    public List<string> Medium = ["MadeUpMedium0.esm"];
    public List<uint> FormIds = [];
    public List<(byte Type, uint Count)> Groups = [];
    public List<byte[]> Records = [];
    /// <summary>Added to the true slot [5] value, to test the end check.</summary>
    public int Slot5Shift;

    public byte[] Build()
    {
        var w = new Writer();
        w.Ascii("SFS_SAVEGAME");
        var header = new Writer();
        header.U32(27); header.U8(SaveVersion); header.U32(7); header.Str("Test Pilot"); header.U32(3);
        header.Str("Nowhere"); header.Str("0d.1h.2m.0 days.1 hours.2 minutes"); header.Str("Human"); header.U16(1);
        header.F32(10); header.F32(100); header.U64(0x01DB000000000000);
        w.U32((uint)header.Length); w.Bytes(header.ToArray());
        w.U8(SaveVersion); w.Str("9.9.9.0"); w.Str("9.9.9.0");

        var plugins = new Writer();
        plugins.U8((byte)Full.Count); foreach (var p in Full) Entry(plugins, p);
        plugins.U16((ushort)Light.Count); foreach (var p in Light) Entry(plugins, p);
        plugins.U32((uint)Medium.Count); foreach (var p in Medium) Entry(plugins, p);
        plugins.U16(1);
        w.U16((ushort)plugins.Length); w.U16(0); w.Bytes(plugins.ToArray());

        long table = w.Length;
        for (int i = 0; i < 25; i++) w.U32(0);
        var slots = new uint[25];
        slots[2] = (uint)w.Length; // global data 1, empty
        slots[3] = (uint)w.Length; // global data 2, empty
        slots[4] = (uint)w.Length;
        foreach (var r in Records) w.Bytes(r);
        slots[5] = (uint)(w.Length + Slot5Shift);
        slots[0] = (uint)w.Length;
        w.U32((uint)FormIds.Count); foreach (var id in FormIds) w.U32(id);
        slots[1] = (uint)w.Length;
        w.U32(0); w.U32(0);
        slots[11] = (uint)w.Length;
        w.Vsval((uint)Groups.Count);
        foreach (var (type, count) in Groups) { w.U8(type); w.Vsval(count); }
        slots[9] = (uint)Records.Count;
        w.Bytes(new byte[16]); // trailing data the reader doesn't describe

        var d = w.ToArray();
        for (int i = 0; i < 25; i++) BitConverter.GetBytes(slots[i]).CopyTo(d, table + 4 * i);
        return d;
    }

    static void Entry(Writer w, string name) { w.Str(name); w.U8(0); }

    public (SaveGame, SaveBody) Parse()
    {
        var d = Build();
        var g = SaveGame.Parse(d);
        return (g, SaveBody.Parse(d, g));
    }
}

/// <summary>Little-endian byte writer for the made-up fragments.</summary>
sealed class Writer
{
    readonly MemoryStream ms = new();
    public long Length => ms.Length;
    public byte[] ToArray() => ms.ToArray();
    public Writer U8(byte v) { ms.WriteByte(v); return this; }
    public Writer U16(ushort v) { ms.Write(BitConverter.GetBytes(v)); return this; }
    public Writer U32(uint v) { ms.Write(BitConverter.GetBytes(v)); return this; }
    public Writer U64(ulong v) { ms.Write(BitConverter.GetBytes(v)); return this; }
    public Writer F32(float v) { ms.Write(BitConverter.GetBytes(v)); return this; }
    public Writer Bytes(byte[] b) { ms.Write(b); return this; }
    public Writer Ascii(string s) { ms.Write(Encoding.ASCII.GetBytes(s)); return this; }
    public Writer Str(string s) { var b = Encoding.UTF8.GetBytes(s); U16((ushort)b.Length); ms.Write(b); return this; }
    /// <summary>A refid, big-endian, 3 bytes.</summary>
    public Writer Ref(uint kind, uint payload)
    {
        uint v = kind << 22 | payload;
        ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v);
        return this;
    }
    /// <summary>A VSVAL in the smallest size the writer uses.</summary>
    public Writer Vsval(uint v)
    {
        if (v < 0x40) return U8((byte)(v << 2));
        if (v < 0x4000) return U16((ushort)(v << 2 | 1));
        return U32(v << 2 | 2);
    }
}
