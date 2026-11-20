using System.IO.Compression;

namespace SfsReader.Tests;

/// <summary>One made-up fragment per layout in FORMAT.md (VSVAL, refids, form ID → plugin, the form-ID array, the
/// type directory, the change records, the container). Each test names the FORMAT.md section it checks.</summary>
public class BodyTests
{
    // ---- VSVAL ----

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0u, 1)]
    [InlineData(new byte[] { 0xFC }, 0x3Fu, 1)]
    [InlineData(new byte[] { 0x01, 0x01 }, 0x40u, 2)]
    [InlineData(new byte[] { 0xFD, 0xFF }, 0x3FFFu, 2)]
    [InlineData(new byte[] { 0x02, 0x00, 0x01, 0x00 }, 0x4000u, 4)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }, 0x3FFFFFFFu, 4)]
    public void Vsval_reads_each_size(byte[] bytes, uint value, int size)
    {
        long pos = 0;
        Assert.Equal(value, SaveBody.ReadVsval(bytes, ref pos, bytes.Length));
        Assert.Equal(size, pos);
    }

    [Fact]
    public void Vsval_tag_3_is_an_error()
    {
        long pos = 0;
        Assert.Throws<InvalidDataException>(() => SaveBody.ReadVsval([0x03, 0, 0, 0, 0], ref pos, 5));
    }

    [Fact]
    public void Vsval_past_the_end_is_an_error()
    {
        long pos = 0;
        Assert.Throws<InvalidDataException>(() => SaveBody.ReadVsval([0x02, 0x00], ref pos, 2));
    }

    // ---- Refids and form ID → plugin ----

    static SaveGame Game() => SaveGame.Parse(new FakeSave().Build());

    [Fact]
    public void Refid_kind_0_indexes_the_array_from_1()
    {
        var g = Game();
        uint[] ids = [0x01000ABC, 0xFE001234];
        var a = SaveBody.DecodeRefId(0x000001, ids, g);
        Assert.Equal(0x01000ABCu, a.FormId);
        Assert.Equal("Made-Up-Full.esm", a.Plugin);
        Assert.Equal(0xABCu, a.LocalId);
        var b = SaveBody.DecodeRefId(0x000002, ids, g);
        Assert.Equal("MadeUpLight1.esm", b.Plugin);
        Assert.Equal(0x234u, b.LocalId);
    }

    [Fact]
    public void Refid_kind_0_payload_0_is_no_form()
    {
        var r = SaveBody.DecodeRefId(0x000000, [0x01000ABC], Game());
        Assert.True(r.IsNull);
        Assert.False(r.IsCreated);
    }

    [Fact]
    public void Refid_kind_0_past_the_array_is_unresolved()
    {
        var r = SaveBody.DecodeRefId(0x000005, [0x01000ABC], Game());
        Assert.StartsWith("?", r.Plugin);
    }

    [Fact]
    public void Refid_kind_1_is_the_first_plugin()
    {
        var r = SaveBody.DecodeRefId(1u << 22 | 0x12345, [], Game());
        Assert.Equal(1, r.Kind);
        Assert.Equal(0x00012345u, r.FormId);
        Assert.Equal("Starfield.esm", r.Plugin);
        Assert.Equal("00012345 Starfield.esm:12345", r.ToString());
    }

    [Fact]
    public void Refid_kind_2_is_created_during_play()
    {
        var r = SaveBody.DecodeRefId(2u << 22 | 0x00ABCD, [], Game());
        Assert.Equal(0xFF00ABCDu, r.FormId);
        Assert.True(r.IsCreated);
        Assert.False(r.IsNull);
    }

    [Fact]
    public void Refid_kind_3_is_an_error() =>
        Assert.Throws<InvalidDataException>(() => SaveBody.DecodeRefId(3u << 22 | 1, [], Game()));

    [Theory]
    [InlineData(0xFE001ABCu, "MadeUpLight1.esm", 0xABCu)]   // light: index in bits 12-23
    [InlineData(0xFD00BEEFu, "MadeUpMedium0.esm", 0xBEEFu)] // medium: index in bits 16-23
    [InlineData(0x01ABCDEFu, "Made-Up-Full.esm", 0xABCDEFu)] // full: the top byte
    public void FormId_names_its_plugin(uint id, string plugin, uint local)
    {
        var r = SaveBody.DecodeFormId(id, Game());
        Assert.Equal(plugin, r.Plugin);
        Assert.Equal(local, r.LocalId);
    }

    [Theory]
    [InlineData(0xFE002001u)] // light plugin 2: the fake save has 2
    [InlineData(0xFD010001u)] // medium plugin 1: it has 1
    [InlineData(0x05000001u)] // full plugin 5: it has 2
    public void FormId_with_an_unknown_plugin_index_is_unresolved(uint id) =>
        Assert.StartsWith("?", SaveBody.DecodeFormId(id, Game()).Plugin);

    // ---- Change records ----

    [Fact]
    public void Record_with_every_optional_field()
    {
        var rec = new Writer().U8(0x01 | 0x02 | 0x08 | 0x10 | 0x20).U8(150).Ref(1, 0x100)
            .U32(0x80000003).Vsval(2).Vsval(1).Vsval(3)
            .Ref(2, 0x55).Ref(0, 1)
            .U16(9)
            .Bytes([0xAA, 0xBB, 0xCC]).ToArray();
        var fake = new FakeSave { Records = [rec], FormIds = [0xFE000801], Groups = [(8, 1)] };
        var (_, body) = fake.Parse();
        Assert.Empty(body.Warnings);
        var r = Assert.Single(body.Records);
        Assert.Equal(150, r.Version);
        Assert.Equal(0x00000100u, r.Form.FormId);
        Assert.True(r.HasChangeFlags);
        Assert.Equal(0x80000003u, r.ChangeFlags);
        Assert.Equal(8, r.FormType);
        Assert.Equal(2, r.References.Count);
        Assert.True(r.References[0].IsCreated);
        Assert.Equal("MadeUpLight0.esm", r.References[1].Plugin);
        Assert.Equal([(ushort)9], r.StringIndexes);
        Assert.Equal(3, r.DataSize);
        Assert.Equal(0, r.Index);
    }

    [Fact]
    public void Record_with_only_its_refid_has_change_flags_0x80008000()
    {
        var rec = new Writer().U8(0x00).Ref(2, 0x77).ToArray();
        var (_, body) = new FakeSave { Records = [rec], Groups = [(0, 1)] }.Parse();
        var r = Assert.Single(body.Records);
        Assert.False(r.HasChangeFlags);
        Assert.Equal(0x80008000u, r.ChangeFlags);
        Assert.Equal(153, r.Version); // no version byte: the save version
        Assert.Equal(2, r.RefKind);
        Assert.Empty(r.References);
        Assert.Equal(0, r.DataSize);
    }

    [Fact]
    public void Flag_bit_2_adds_no_field()
    {
        var rec = new Writer().U8(0x04 | 0x20).Ref(1, 0x10).Vsval(1).U8(0x42).ToArray();
        var (_, body) = new FakeSave { Records = [rec, rec], Groups = [(19, 2)] }.Parse();
        Assert.Empty(body.Warnings);
        Assert.Equal(2, body.Records.Count);
        Assert.All(body.Records, r => Assert.Equal(1, r.DataSize));
    }

    [Theory]
    [InlineData(0x40)]
    [InlineData(0x80)]
    public void Flag_bits_6_and_7_are_refused(byte flag)
    {
        var rec = new Writer().U8(flag).Ref(1, 0x10).ToArray();
        Assert.Throws<InvalidDataException>(() => new FakeSave { Records = [rec], Groups = [(0, 1)] }.Parse());
    }

    [Fact]
    public void References_come_after_the_header_counts_in_order()
    {
        var rec = new Writer().U8(0x08).Ref(1, 0x1).Vsval(3).Ref(1, 0xA).Ref(1, 0xB).Ref(1, 0xC).ToArray();
        var (_, body) = new FakeSave { Records = [rec], Groups = [(0, 1)] }.Parse();
        Assert.Equal([0xAu, 0xBu, 0xCu], body.Records[0].References.Select(x => x.FormId));
    }

    [Fact]
    public void Record_data_running_past_slot_5_is_an_error()
    {
        var rec = new Writer().U8(0x20).Ref(1, 0x10).Vsval(50).ToArray(); // claims 50 data bytes, has none
        Assert.Throws<InvalidDataException>(() => new FakeSave { Records = [rec], Groups = [(0, 1)] }.Parse());
    }

    [Fact]
    public void Walk_must_end_exactly_at_slot_5()
    {
        var rec = new Writer().U8(0x00).Ref(1, 0x10).ToArray();
        var (_, body) = new FakeSave { Records = [rec], Groups = [(0, 1)], Slot5Shift = 4 }.Parse();
        Assert.Contains(body.Warnings, w => w.Contains("slot [5]"));
    }

    // ---- Type directory ----

    [Fact]
    public void Type_directory_runs_give_each_record_its_type()
    {
        var rec = new Writer().U8(0x00).Ref(1, 0x10).ToArray();
        var fake = new FakeSave { Records = [rec, rec, rec, rec, rec], Groups = [(0, 2), (8, 1), (1, 2)] };
        var (_, body) = fake.Parse();
        Assert.Empty(body.Warnings);
        Assert.Equal([0, 0, 8, 1, 1], body.Records.Select(r => (int)r.FormType));
        Assert.Equal([0, 1, 2, 3, 4], body.Records.Select(r => r.Index));
    }

    [Fact]
    public void Type_directory_that_does_not_add_up_to_slot_9_warns()
    {
        var rec = new Writer().U8(0x00).Ref(1, 0x10).ToArray();
        var (_, body) = new FakeSave { Records = [rec, rec], Groups = [(0, 1)] }.Parse();
        Assert.Contains(body.Warnings, w => w.Contains("type directory"));
    }

    [Fact]
    public void Type_directory_with_a_two_byte_run()
    {
        var rec = new Writer().U8(0x00).Ref(1, 0x10).ToArray();
        var fake = new FakeSave { Records = Enumerable.Repeat(rec, 70).ToList(), Groups = [(0, 70)] };
        var (_, body) = fake.Parse();
        Assert.Empty(body.Warnings);
        Assert.Equal(70, body.Records.Count);
    }

    // ---- Form-ID array ----

    [Fact]
    public void Form_id_array_is_a_count_then_ids()
    {
        var (_, body) = new FakeSave { FormIds = [0x01000001, 0xFD000002, 0xFE001003] }.Parse();
        Assert.Equal([0x01000001u, 0xFD000002u, 0xFE001003u], body.FormIds);
        Assert.Empty(body.Warnings);
    }

    // ---- Versions ----

    [Fact]
    public void Other_save_versions_are_refused()
    {
        var fake = new FakeSave { SaveVersion = 143 };
        var d = fake.Build();
        var g = SaveGame.Parse(d);
        Assert.Throws<InvalidDataException>(() => SaveBody.Parse(d, g));
    }

    // ---- Header and plugin list ----

    [Fact]
    public void Header_and_plugin_list()
    {
        var (g, _) = new FakeSave().Parse();
        Assert.Equal(27u, g.EngineVersion);
        Assert.Equal(153, g.SaveVersion);
        Assert.Equal("Test Pilot", g.PlayerName);
        Assert.Equal(["full", "full", "light", "light", "medium"], g.Plugins.Select(p => p.Kind));
        Assert.Equal((ushort)1, g.PostPluginUnknown);
        Assert.Empty(g.Warnings);
    }

    // ---- Container ----

    [Fact]
    public void Container_with_a_zlib_chunk_and_a_stored_chunk()
    {
        const int chunk = 0x40000;
        var inner = new FakeSave().Build();
        // Pad the made-up save to just over one chunk: chunk 0 compresses, chunk 1 is stored as is.
        var data = new byte[chunk + 64];
        inner.CopyTo(data, 0);
        var rnd = new Random(1234);
        var tail = new byte[64];
        rnd.NextBytes(tail);
        tail[0] = 0x00; // not a zlib header
        tail.CopyTo(data, chunk);

        byte[] z;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(data, 0, chunk);
            z = ms.ToArray();
        }
        var file = new Writer();
        file.Ascii("BCPS").U32(1).U64(0x48).U64(0).U64(0x100).U64((ulong)data.Length).U64(0).U32(chunk).U32(0).U32(16).U32(0)
            .U32(0).Ascii("ZIP ");
        file.U32((uint)z.Length).U32(64);
        file.Bytes(new byte[0x100 - file.Length]);
        file.Bytes(z);
        file.Bytes(new byte[(16 - z.Length % 16) % 16]);
        file.Bytes(tail);
        var bytes = file.ToArray();

        string path = Path.Combine(Path.GetTempPath(), $"sfsreader-test-{Guid.NewGuid():N}.sfs");
        try
        {
            File.WriteAllBytes(path, bytes);
            var (c, g) = SaveFile.Open(path);
            Assert.Equal(data, c.Inner);
            Assert.Equal(1, c.StoredChunks);
            Assert.Equal("Test Pilot", g.PlayerName);
            Assert.Empty(g.Warnings);
        }
        finally { File.Delete(path); }
    }
}
