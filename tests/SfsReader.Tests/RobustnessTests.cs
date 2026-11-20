namespace SfsReader.Tests;

/// <summary>Bad input (cut-off saves, unknown versions, files that aren't saves) is an InvalidDataException with a
/// message, never an index error. Every input is a made-up fragment.</summary>
public class RobustnessTests
{
    [Fact]
    public void A_slot_past_the_end_gives_no_negative_or_overlong_section()
    {
        var d = new FakeSave().Build();
        long table = SaveGame.Parse(d).LocationTableOffset;
        BitConverter.GetBytes(0x7FFFFFFFu).CopyTo(d, table + 4 * 2); // global data 1 far past the end
        var g = SaveGame.Parse(d);
        Assert.Contains(g.Warnings, w => w.Contains("global data 1") && w.Contains("past the end"));
        Assert.All(g.Sections, s => Assert.True(s.Size >= 0 && s.Offset + s.Size <= d.LongLength, $"{s.Name} {s.Offset} {s.Size}"));
    }

    [Fact]
    public void A_plugin_list_over_64_KB_is_read_although_its_u16_size_wraps()
    {
        var fake = new FakeSave { Light = Enumerable.Range(0, 700).Select(i => $"MadeUpLight{i:D4}{new string('x', 90)}.esm").ToList() };
        var g = SaveGame.Parse(fake.Build());
        Assert.Equal(2 + 700 + 1, g.Plugins.Count);
        Assert.Contains(g.Warnings, w => w.Contains("wrapped"));
        Assert.DoesNotContain(g.Warnings, w => w.Contains("past the end") || w.Contains("plugin list ended"));
        Assert.Equal((ushort?)1, g.PostPluginUnknown);
    }

    [Fact]
    public void A_header_cut_off_anywhere_is_an_InvalidDataException()
    {
        var d = new FakeSave().Build();
        // Every cut inside the header and plugin list, plus a few inside the location table.
        long table = SaveGame.Parse(d).LocationTableOffset;
        for (int cut = 0; cut < table + 100; cut++)
        {
            var part = d[..cut];
            var e = Assert.ThrowsAny<Exception>(() => SaveGame.Parse(part));
            Assert.IsType<InvalidDataException>(e);
        }
    }

    [Fact]
    public void A_cut_off_header_names_the_offset()
    {
        var d = new FakeSave().Build()[..40];
        var e = Assert.Throws<InvalidDataException>(() => SaveGame.Parse(d));
        Assert.Contains("ends early", e.Message);
    }

    [Fact]
    public void A_save_version_newer_than_153_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => SaveGame.Parse(new FakeSave { SaveVersion = 154 }.Build()));
        Assert.Contains("newer than this reader knows", e.Message);
    }

    [Fact]
    public void Save_version_153_still_reads()
    {
        var g = SaveGame.Parse(new FakeSave().Build());
        Assert.Equal(SaveGame.NewestKnownSaveVersion, g.SaveVersion);
        Assert.Empty(g.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(0x47)]
    public void A_file_too_short_for_a_container_is_refused(int length)
    {
        var bytes = new byte[length];
        if (length >= 4) "BCPS"u8.CopyTo(bytes);
        WithFile(bytes, path => Assert.Throws<InvalidDataException>(() => SfsContainer.Load(path)));
    }

    [Fact]
    public void A_file_that_is_not_a_save_says_so()
    {
        var bytes = new byte[0x100];
        "PK\u0003\u0004"u8.CopyTo(bytes);
        WithFile(bytes, path =>
        {
            var e = Assert.Throws<InvalidDataException>(() => SfsContainer.Load(path));
            Assert.Contains("not a Starfield .sfs save", e.Message);
        });
    }

    [Fact]
    public void A_chunk_table_past_the_end_is_refused()
    {
        var file = new Writer();
        file.Ascii("BCPS").U32(1).U64(0x10000).U64(0).U64(0x100).U64(0x100000).U64(0).U32(0x40000).U32(0).U32(16).U32(0)
            .U32(0).Ascii("ZIP ");
        file.Bytes(new byte[0x100 - file.Length]);
        WithFile(file.ToArray(), path =>
        {
            var e = Assert.Throws<InvalidDataException>(() => SfsContainer.Load(path));
            Assert.Contains("chunk-size table", e.Message);
        });
    }

    [Fact]
    public void A_cut_off_last_chunk_is_refused()
    {
        var file = new Writer();
        file.Ascii("BCPS").U32(1).U64(0x48).U64(0).U64(0x100).U64(0x1000).U64(0).U32(0x40000).U32(0).U32(16).U32(0)
            .U32(0).Ascii("ZIP ");
        file.U32(0x800);
        file.Bytes(new byte[0x100 - file.Length]);
        file.Bytes(new byte[0x20]); // far less than the 0x800 bytes the table promises
        WithFile(file.ToArray(), path =>
        {
            var e = Assert.Throws<InvalidDataException>(() => SfsContainer.Load(path));
            Assert.Contains("runs past the end", e.Message);
        });
    }

    [Fact]
    public void Data_auto_uses_STARFIELD_DATA_first()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sfsreader-data-{Guid.NewGuid():N}");
        string? before = Environment.GetEnvironmentVariable("STARFIELD_DATA");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "Starfield.esm"), []);
            Environment.SetEnvironmentVariable("STARFIELD_DATA", dir);
            var log = new List<string>();
            Assert.Equal(dir, DataFolder.Find(log.Add));
            Assert.Single(log);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STARFIELD_DATA", before);
            Directory.Delete(dir, recursive: true);
        }
    }

    static void WithFile(byte[] bytes, Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sfsreader-test-{Guid.NewGuid():N}.sfs");
        try
        {
            File.WriteAllBytes(path, bytes);
            test(path);
        }
        finally { File.Delete(path); }
    }
}
