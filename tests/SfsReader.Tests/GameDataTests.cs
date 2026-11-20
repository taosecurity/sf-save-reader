using System.IO.Compression;
using System.Text;

namespace SfsReader.Tests;

/// <summary>GameData against made-up plugins written to a temporary Data folder. Every plugin, name and form ID is
/// invented. Includes both unusual own-record numberings (NOTES.md "Known bug", S3): a full plugin numbering its own
/// records one past its master count, and a medium plugin using the full style.</summary>
public sealed class GameDataTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("sfsreader-gamedata-").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    // ---- made-up plugin writer ----

    static byte[] Sub(string type, byte[] data) =>
        new Writer().Ascii(type).U16((ushort)data.Length).Bytes(data).ToArray();
    static byte[] Z(string s) => [.. Encoding.UTF8.GetBytes(s), 0];
    static byte[] U32(uint v) => BitConverter.GetBytes(v);

    static byte[] Record(string type, uint formId, byte[] body, bool compress = false)
    {
        uint flags = 0;
        if (compress)
        {
            var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(body);
            body = [.. U32((uint)body.Length), .. ms.ToArray()];
            flags = 0x40000;
        }
        return new Writer().Ascii(type).U32((uint)body.Length).U32(flags).U32(formId).U32(0).U16(0).U16(0).Bytes(body).ToArray();
    }

    static byte[] Group(string label, params byte[][] records)
    {
        var inner = records.SelectMany(r => r).ToArray();
        return new Writer().Ascii("GRUP").U32((uint)(24 + inner.Length)).Ascii(label).U32(0).U32(0).U32(0).Bytes(inner).ToArray();
    }

    void Plugin(string name, uint flags, string[] masters, params byte[][] groups)
    {
        var hdr = new Writer().Bytes(Sub("HEDR", new byte[12]));
        foreach (var m in masters) hdr.Bytes(Sub("MAST", Z(m))).Bytes(Sub("DATA", new byte[8]));
        var h = hdr.ToArray();
        var w = new Writer().Ascii("TES4").U32((uint)h.Length).U32(flags).U32(0).U32(0).U16(0).U16(0).Bytes(h);
        foreach (var g in groups) w.Bytes(g);
        File.WriteAllBytes(Path.Combine(dir, name), w.ToArray());
    }

    static FormRef Ref(string plugin, uint local) => new() { RefId = 1, FormId = 0x01000000 | local, Plugin = plugin, LocalId = local };

    void Masters()
    {
        Plugin("MadeUpMaster.esm", 0x1, [], Group("QUST", Record("QUST", 0x00000800, [.. Sub("EDID", Z("MasterQuest")), .. Sub("FULL", Z("Master Quest"))])));
        Plugin("MadeUpLight.esm", 0x101, ["MadeUpMaster.esm"], Group("MISC", Record("MISC", 0x01000801, Sub("EDID", Z("LightThing")))));
        Plugin("MadeUpMedium.esm", 0x401, ["MadeUpMaster.esm"], Group("MISC", Record("MISC", 0x01000802, Sub("EDID", Z("MediumThing")))));
    }

    // ---- own-record rule ----

    [Theory]
    [InlineData(0x01000900u, 1, 0, 0, 0x900u)]     // full style, index = full master count
    [InlineData(0x02000900u, 1, 0, 0, 0x900u)]     // full style, one past the count (Genesis SuitPowerCores)
    [InlineData(0x00000900u, 1, 0, 0, null)]       // the master's record
    [InlineData(0xFD010900u, 1, 1, 0, 0x900u)]     // medium style, index = medium master count
    [InlineData(0xFD000900u, 1, 1, 0, null)]       // the medium master's record
    [InlineData(0xFE001900u, 1, 0, 1, 0x900u)]     // light style, index = light master count
    [InlineData(0xFE000900u, 1, 0, 1, null)]       // the light master's record
    [InlineData(0xFF000900u, 1, 0, 0, null)]       // never a plugin record
    public void Own_record_rule(uint fid, int full, int medium, int light, uint? own) =>
        Assert.Equal(own, GameData.OwnLocal(fid, full, medium, light));

    [Fact]
    public void Full_plugin_numbering_its_own_records_one_past_its_masters()
    {
        Masters();
        Plugin("OddFull.esm", 0x1, ["MadeUpMaster.esm"],
            Group("MISC", Record("MISC", 0x020071B1, Sub("EDID", Z("OddFullThing"))), Record("MISC", 0x00000800, Sub("EDID", Z("Override")))));
        var gd = new GameData(dir);
        gd.Load("OddFull.esm", [0x71B1, 0x800]);
        Assert.Equal("OddFullThing", gd.Get(Ref("OddFull.esm", 0x71B1))!.EditorId);
        Assert.Null(gd.Get(Ref("OddFull.esm", 0x800))); // an override of the master's record, not its own
        Assert.Contains(gd.Warnings, w => w.Contains("OddFull.esm: no record 800"));
    }

    [Fact]
    public void Medium_plugin_using_the_full_style()
    {
        Masters();
        // masters: one full, one light, one medium; own records in the full style 01xxxxxx and the medium style FD01xxxx
        Plugin("OddMedium.esm", 0x401, ["MadeUpMaster.esm", "MadeUpLight.esm", "MadeUpMedium.esm"],
            Group("QUST", Record("QUST", 0x0100084F, Sub("EDID", Z("OddMediumQuest")))),
            Group("MISC", Record("MISC", 0xFD010850, Sub("EDID", Z("MediumStyle"))), Record("MISC", 0xFD000802, Sub("EDID", Z("MediumOverride")))));
        var gd = new GameData(dir);
        gd.Load("OddMedium.esm", [0x84F, 0x850, 0x802], warnMissing: false);
        Assert.Equal("(no name)  [OddMediumQuest]", gd.Describe(Ref("OddMedium.esm", 0x84F)));
        Assert.Equal("(no name)  [MediumStyle]", gd.Describe(Ref("OddMedium.esm", 0x850)));
        Assert.Null(gd.Get(Ref("OddMedium.esm", 0x802)));
        Assert.Empty(gd.Warnings);
    }

    [Fact]
    public void Light_plugin_in_the_light_and_full_styles()
    {
        Masters();
        Plugin("MyLight.esm", 0x101, ["MadeUpMaster.esm", "MadeUpLight.esm"],
            Group("MISC", Record("MISC", 0xFE001810, Sub("EDID", Z("LightStyle"))), Record("MISC", 0x01000811, Sub("EDID", Z("FullStyle")))));
        var gd = new GameData(dir);
        Assert.Equal("(no name)  [LightStyle]", gd.Describe(Ref("MyLight.esm", 0x810)));
        Assert.Equal("(no name)  [FullStyle]", gd.Describe(Ref("MyLight.esm", 0x811)));
    }

    // ---- names ----

    [Fact]
    public void Compressed_record_and_plain_name()
    {
        Masters();
        Plugin("Plain.esm", 0x1, ["MadeUpMaster.esm"],
            Group("WEAP", Record("WEAP", 0x01000900, [.. Sub("EDID", Z("ZipGun")), .. Sub("FULL", Z("Zip Gun"))], compress: true)));
        var gd = new GameData(dir);
        var r = gd.Get(Ref("Plain.esm", 0x900))!;
        Assert.Equal("WEAP", r.Signature);
        Assert.Equal("Zip Gun  [ZipGun]", gd.Describe(Ref("Plain.esm", 0x900)));
        Assert.Equal("Master Quest  [MasterQuest]", gd.Describe(Ref("MadeUpMaster.esm", 0x800)));
    }

    static byte[] Strings(params (uint Id, string Text)[] items)
    {
        var data = new Writer();
        var dirW = new Writer();
        foreach (var (id, text) in items) { dirW.U32(id).U32((uint)data.Length); data.Bytes(Z(text)); }
        return new Writer().U32((uint)items.Length).U32((uint)data.Length).Bytes(dirW.ToArray()).Bytes(data.ToArray()).ToArray();
    }

    [Fact]
    public void Localized_name_from_a_loose_strings_file()
    {
        Masters();
        Plugin("Loc.esm", 0x81, ["MadeUpMaster.esm"], Group("MISC", Record("MISC", 0x01000900, [.. Sub("EDID", Z("LocThing")), .. Sub("FULL", U32(0x42))])));
        Directory.CreateDirectory(Path.Combine(dir, "Strings"));
        File.WriteAllBytes(Path.Combine(dir, "Strings", "Loc_en.strings"), Strings((0x41, "Wrong"), (0x42, "Made-Up Thing")));
        Assert.Equal("Made-Up Thing  [LocThing]", new GameData(dir).Describe(Ref("Loc.esm", 0x900)));
    }

    [Fact]
    public void The_log_says_what_was_scanned_and_where_names_came_from()
    {
        Masters();
        Plugin("LogLoc.esm", 0x81, ["MadeUpMaster.esm"], Group("MISC", Record("MISC", 0x01000900, [.. Sub("EDID", Z("LogThing")), .. Sub("FULL", U32(0x42))])));
        Directory.CreateDirectory(Path.Combine(dir, "Strings"));
        File.WriteAllBytes(Path.Combine(dir, "Strings", "LogLoc_en.strings"), Strings((0x42, "Logged Thing")));
        var log = new List<string>();
        var gd = new GameData(dir) { Log = log.Add };
        gd.Load("LogLoc.esm", [0x900, 0x901], warnMissing: false);
        Assert.Contains(log, l => l.StartsWith("LogLoc.esm: full plugin, localized, masters 1 full / 0 medium / 0 light"));
        Assert.Contains(log, l => l.StartsWith("LogLoc.esm: scanned for 2 form(s), found 1"));
        Assert.Contains(log, l => l.StartsWith("LogLoc.esm: names from loose ") && l.EndsWith("LogLoc_en.strings"));
    }

    [Fact]
    public void Localized_name_from_the_plugins_ba2()
    {
        Masters();
        Plugin("LocBa.esm", 0x81, ["MadeUpMaster.esm"], Group("MISC", Record("MISC", 0x01000900, [.. Sub("EDID", Z("BaThing")), .. Sub("FULL", U32(7))])));
        var file = Strings((7, "Archived Name"));
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(file);
        var packed = ms.ToArray();
        string name = "strings/LocBa_de.strings";
        long dataAt = 32 + 36, namesAt = dataAt + packed.Length;
        var w = new Writer().Ascii("BTDX").U32(2).Ascii("GNRL").U32(1).U64((ulong)namesAt).U32(1).U32(0)
            .U32(0).Ascii("stri").U32(0).U32(0).U64((ulong)dataAt).U32((uint)packed.Length).U32((uint)file.Length).U32(0xBAADF00D)
            .Bytes(packed).Str(name);
        File.WriteAllBytes(Path.Combine(dir, "LocBa - Localization.ba2"), w.ToArray());
        Assert.Equal("Archived Name  [BaThing]", new GameData(dir, "de").Describe(Ref("LocBa.esm", 0x900)));
    }

    [Fact]
    public void Placed_reference_is_described_by_its_base_object()
    {
        Masters();
        Plugin("Refs.esm", 0x1, ["MadeUpMaster.esm"],
            Group("CONT", Record("CONT", 0x01000900, [.. Sub("EDID", Z("BoxBase")), .. Sub("FULL", Z("Supply Box"))])),
            Group("CELL", Record("REFR", 0x01000901, Sub("NAME", U32(0x01000900))), Record("REFR", 0x01000902, Sub("NAME", U32(0x00000800))),
                Record("REFR", 0x01000903, [.. Sub("EDID", Z("BoxRef")), .. Sub("NAME", U32(0x01000900))])));
        var gd = new GameData(dir);
        Assert.Equal("Supply Box  [BoxBase]", gd.Describe(Ref("Refs.esm", 0x901)));
        Assert.Equal("Master Quest  [MasterQuest]", gd.Describe(Ref("Refs.esm", 0x902))); // base in the master
        Assert.Equal("Supply Box  [BoxRef -> BoxBase]", gd.Describe(Ref("Refs.esm", 0x903))); // own editor ID and the base's
    }

    [Fact]
    public void Missing_plugin_and_unknown_forms()
    {
        Masters();
        var gd = new GameData(dir);
        Assert.False(gd.HasPlugin("NotThere.esm"));
        Assert.False(gd.HasPlugin("?light 5"));
        Assert.True(gd.HasPlugin("madeupmaster.esm"));
        Assert.Equal("", gd.Describe(Ref("NotThere.esm", 0x800)));
        Assert.Equal("", gd.Describe(new FormRef { FormId = 0xFF000001, Plugin = "", LocalId = 1 }));
        Assert.Null(gd.Get(Ref("MadeUpMaster.esm", 0x999)));
        Assert.Empty(gd.Warnings); // Get never warns about missing forms
        gd.Load("MadeUpMaster.esm", [0x998]);
        Assert.Single(gd.Warnings);
    }
}
