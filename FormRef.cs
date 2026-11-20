namespace SfsReader;

/// <summary>
/// A form named inside a save: the 3-byte refid as stored, and the form ID and plugin it decodes to through the
/// save's own plugin list (FORMAT.md, "Refids" and "Form ID → plugin"). The plugin is the save's, not the current
/// load order's.
/// </summary>
public readonly record struct FormRef
{
    /// <summary>The 24-bit refid value (big-endian in the file).</summary>
    public uint RefId { get; init; }
    /// <summary>Top 2 bits of the refid: 0 = form-ID array index, 1 = first plugin, 2 = created during play.</summary>
    public int Kind => (int)(RefId >> 22);
    /// <summary>The form ID in the save's own plugin numbering (top byte FE = light, FD = medium, FF = created).</summary>
    public uint FormId { get; init; }
    /// <summary>The plugin's file name from the save's plugin list; empty for created forms and no form;
    /// starts with '?' when the index isn't in the save's list.</summary>
    public string Plugin { get; init; }
    /// <summary>The form number inside its plugin (bits 0-11 light, 0-15 medium, 0-23 full); for created forms the
    /// low 24 bits.</summary>
    public uint LocalId { get; init; }

    /// <summary>No form: form ID 0 (a kind-0 refid with payload 0, or an array entry of 0).</summary>
    public bool IsNull => FormId == 0;
    /// <summary>A form created during play (top byte FF).</summary>
    public bool IsCreated => FormId >> 24 == 0xFF;

    public override string ToString() =>
        IsNull ? "00000000 (none)"
        : IsCreated ? $"{FormId:X8} (created)"
        : $"{FormId:X8} {Plugin}:{LocalId:X}";
}
