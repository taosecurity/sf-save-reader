using System.IO.Compression;
using System.Text;

namespace SfsReader;

/// <summary>
/// The outer .sfs container ("BCPS"): a fixed header, a table of compressed chunk sizes,
/// then zlib chunks of <see cref="ChunkSize"/> uncompressed bytes each, every chunk starting on an
/// <see cref="Alignment"/> boundary. Layout seen in files; matches Nexus-Mods/StarfieldSaveTool's notes.
/// </summary>
public sealed class SfsContainer
{
    public const string Magic = "BCPS";

    public uint Version { get; private init; }
    public ulong ChunkTableOffset { get; private init; }
    public ulong DataOffset { get; private init; }
    public ulong UncompressedSize { get; private init; }
    public uint ChunkSize { get; private init; }
    public uint Alignment { get; private init; }
    public uint Unknown28 { get; private init; }
    public uint Unknown40 { get; private init; }
    public string Compression { get; private init; } = "";
    public uint[] ChunkSizes { get; private init; } = [];
    public long FileSize { get; private init; }
    public long DataEnd { get; private init; }
    /// <summary>Chunks kept as is (no zlib) because compression wouldn't shrink them.</summary>
    public int StoredChunks { get; private init; }

    /// <summary>Decompressed inner stream ("SFS_SAVEGAME ...").</summary>
    public byte[] Inner { get; private init; } = [];

    public static SfsContainer Load(string path)
    {
        // Read-only, shared: never lock or modify the input save.
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }

        if (data.Length < 0x48)
            throw new InvalidDataException($"the file is only {data.Length} bytes, too short for a Starfield save (cut off or not a save?)");
        var magic = Encoding.ASCII.GetString(data, 0, 4);
        if (magic != Magic)
            throw new InvalidDataException($"not a Starfield .sfs save: it doesn't start with BCPS (it starts with '{magic}')");

        uint version = BitConverter.ToUInt32(data, 0x04);
        ulong tableOffset = BitConverter.ToUInt64(data, 0x08);
        ulong dataOffset = BitConverter.ToUInt64(data, 0x18);
        ulong usize = BitConverter.ToUInt64(data, 0x20);
        uint unk28 = BitConverter.ToUInt32(data, 0x28);
        uint chunk = BitConverter.ToUInt32(data, 0x30);
        uint align = BitConverter.ToUInt32(data, 0x38);
        uint unk40 = BitConverter.ToUInt32(data, 0x40);
        string comp = Encoding.ASCII.GetString(data, 0x44, 4);
        if (comp != "ZIP ")
            throw new InvalidDataException($"unsupported compression tag '{comp}'");
        if (chunk == 0 || align == 0)
            throw new InvalidDataException("chunk size or alignment is zero");

        if (usize == 0 || usize > (ulong)Array.MaxLength)
            throw new InvalidDataException($"the container says the save data is {usize:N0} bytes, which this reader can't hold");
        int count = (int)((usize + chunk - 1) / chunk);
        if (tableOffset > (ulong)data.LongLength || (ulong)count * 4 > (ulong)data.LongLength - tableOffset)
            throw new InvalidDataException("the chunk-size table runs past the end of the file (cut off save?)");
        if (dataOffset > (ulong)data.LongLength)
            throw new InvalidDataException("the first chunk starts past the end of the file (cut off save?)");
        var sizes = new uint[count];
        for (int i = 0; i < count; i++)
            sizes[i] = BitConverter.ToUInt32(data, (int)tableOffset + i * 4);

        var inner = new byte[usize];
        long pos = (long)dataOffset;
        long outPos = 0;
        int stored = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            uint s = sizes[i];
            int expected = (int)Math.Min(chunk, (ulong)inner.LongLength - (ulong)outPos);
            if (pos + s > data.LongLength || s < 2)
                throw new InvalidDataException($"chunk {i} of {sizes.Length} runs past the end of the file (cut off save?)");
            // Stored chunk: data that zlib can't shrink (already-compressed bytes inside the save) is
            // written as is, at its full size. Seen in a few large version-153 saves. A full-size
            // chunk is tried as zlib only when its first two bytes are a valid zlib header.
            bool full = s == expected;
            if (full && !LooksLikeZlib(data, pos))
                StoreChunk(data, pos, inner, outPos, expected, ref stored);
            else
            {
                try
                {
                    using var zs = new ZLibStream(new MemoryStream(data, (int)pos, (int)s), CompressionMode.Decompress);
                    zs.ReadExactly(inner, (int)outPos, expected);
                }
                catch (Exception e) when (full && e is InvalidDataException or EndOfStreamException)
                {
                    StoreChunk(data, pos, inner, outPos, expected, ref stored);
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
                {
                    throw new InvalidDataException($"chunk {i} at 0x{pos:X}: {e.Message}", e);
                }
            }
            outPos += expected;
            pos += s;
            pos = (pos + align - 1) / align * align;
        }

        return new SfsContainer
        {
            Version = version, ChunkTableOffset = tableOffset, DataOffset = dataOffset,
            UncompressedSize = usize, ChunkSize = chunk, Alignment = align, Unknown28 = unk28,
            Unknown40 = unk40, Compression = comp, ChunkSizes = sizes, FileSize = data.LongLength,
            DataEnd = pos, StoredChunks = stored, Inner = inner,
        };
    }

    /// <summary>Copies a stored (not compressed) chunk into the output as is and counts it.</summary>
    static void StoreChunk(byte[] src, long pos, byte[] dst, long outPos, int count, ref int stored)
    {
        Buffer.BlockCopy(src, (int)pos, dst, (int)outPos, count);
        stored++;
    }

    /// <summary>A zlib stream starts with CMF/FLG: method 8 (deflate), window up to 32 KB, and the
    /// pair divisible by 31 (RFC 1950). Random stored data passes this by chance about 1 time in 1,000.</summary>
    static bool LooksLikeZlib(byte[] d, long pos)
    {
        byte cmf = d[pos], flg = d[pos + 1];
        return (cmf & 0x0F) == 8 && (cmf >> 4) <= 7 && ((cmf << 8) | flg) % 31 == 0;
    }
}
