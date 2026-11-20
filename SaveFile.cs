namespace SfsReader;

/// <summary>Opens a .sfs file read-only: the container, then the header and plugin list.</summary>
public static class SaveFile
{
    /// <summary>Reads the container and the header. The file is opened for reading only, shared, and never changed.
    /// Container problems that still leave a readable save go into <see cref="SaveGame.Warnings"/>.</summary>
    public static (SfsContainer Container, SaveGame Save) Open(string path)
    {
        var c = SfsContainer.Load(path);
        var g = SaveGame.Parse(c.Inner);
        if ((ulong)c.Inner.LongLength != c.UncompressedSize)
            g.Warnings.Add($"decompressed {c.Inner.LongLength} bytes, header says {c.UncompressedSize}");
        if (c.DataEnd != c.FileSize)
            g.Warnings.Add($"last chunk ends at {c.DataEnd}, file is {c.FileSize} bytes");
        return (c, g);
    }
}
