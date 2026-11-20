namespace SfsReader;

/// <summary>Prints the differences between two saves: header fields, plugin list and per-section sizes and bytes.</summary>
public static class Compare
{
    // What each header field means, from this project's code and file findings.
    static readonly Dictionary<string, string> Notes = new()
    {
        ["SaveNumber"] = "manual-save counter; only manual saves increment it",
        ["SaveTimeUtc"] = "real-world save time",
        ["Playtime"] = "in-game time",
        ["Location"] = "game state",
        ["Experience"] = "game state",
        ["PlayerLevel"] = "game state",
    };

    /// <param name="redact">Leave out the file names and the player name (they identify the player).</param>
    public static void Print(string pathA, SfsContainer ca, SaveGame a, string pathB, SfsContainer cb, SaveGame b, bool redact = false)
    {
        Console.WriteLine($"A  {(redact ? "(redacted)" : Path.GetFileName(pathA))}");
        Console.WriteLine($"B  {(redact ? "(redacted)" : Path.GetFileName(pathB))}");
        Console.WriteLine();
        Console.WriteLine("Header fields that differ:");
        int n = 0;
        foreach (var (name, va, vb) in Fields(a, b))
        {
            if (va == vb) continue;
            if (redact && name == "PlayerName") { n++; Console.WriteLine($"  {name,-20} (differs; redacted)"); continue; }
            n++;
            Notes.TryGetValue(name, out var note);
            Console.WriteLine($"  {name,-20} A={va}  B={vb}{(note is null ? "" : $"   ({note})")}");
        }
        if (n == 0) Console.WriteLine("  (none)");

        var pa = a.Plugins.Select(p => $"{p.Kind}:{p.Name}").ToList();
        var pb = b.Plugins.Select(p => $"{p.Kind}:{p.Name}").ToList();
        Console.WriteLine();
        Console.WriteLine(pa.SequenceEqual(pb) ? $"Plugin list: identical ({pa.Count})" : "Plugin list differs:");
        foreach (var x in pa.Except(pb)) Console.WriteLine($"  only in A: {x}");
        foreach (var x in pb.Except(pa)) Console.WriteLine($"  only in B: {x}");
        if (!pa.SequenceEqual(pb) && !pa.Except(pb).Any() && !pb.Except(pa).Any())
        {
            // Same plugins, different order (or a repeated entry): show where the lists part.
            int i = 0;
            while (i < pa.Count && i < pb.Count && pa[i] == pb[i]) i++;
            Console.WriteLine($"  same plugins, different order or count (A has {pa.Count}, B has {pb.Count}); first difference at #{i}: " +
                              $"A {(i < pa.Count ? pa[i] : "(end)")}, B {(i < pb.Count ? pb[i] : "(end)")}");
        }

        Console.WriteLine();
        Console.WriteLine($"Structure: {(SameLayout(a, b) ? "same section order and table counts" : "DIFFERENT section layout or counts")}");
        Console.WriteLine($"  {"section",-16} {"size A",12} {"size B",12} {"delta",9}  bytes differing (same-size sections)");
        foreach (var sa in a.Sections)
        {
            var sb = b.Sections.FirstOrDefault(s => s.Name == sa.Name);
            if (sb is null) { Console.WriteLine($"  {sa.Name,-16} {sa.Size,12:N0} {"-",12}"); continue; }
            string diff = sa.Size == sb.Size ? CountDiff(ca.Inner, sa, cb.Inner, sb).ToString("N0") : "-";
            Console.WriteLine($"  {sa.Name,-16} {sa.Size,12:N0} {sb.Size,12:N0} {sb.Size - sa.Size,9:+#,0;-#,0;0}  {diff}");
        }
        Console.WriteLine($"  {"total",-16} {a.InnerSize,12:N0} {b.InnerSize,12:N0} {b.InnerSize - a.InnerSize,9:+#,0;-#,0;0}");
        Console.WriteLine();
        Console.WriteLine("Note: the save type (Autosave/Quicksave/Save/Exitsave) is in the file name only; nothing inside the file records it.");
    }

    static bool SameLayout(SaveGame a, SaveGame b) =>
        a.Sections.Select(s => s.Name).SequenceEqual(b.Sections.Select(s => s.Name)) &&
        a.GlobalData1Count == b.GlobalData1Count && a.GlobalData2Count == b.GlobalData2Count &&
        a.GlobalData3Count == b.GlobalData3Count;

    static long CountDiff(byte[] x, Section sx, byte[] y, Section sy)
    {
        long d = 0;
        long n = Math.Min(sx.Size, Math.Min(x.LongLength - sx.Offset, y.LongLength - sy.Offset));
        for (long i = 0; i < n; i++)
            if (x[sx.Offset + i] != y[sy.Offset + i]) d++;
        return d;
    }

    static IEnumerable<(string, string, string)> Fields(SaveGame a, SaveGame b)
    {
        yield return ("EngineVersion", $"{a.EngineVersion}", $"{b.EngineVersion}");
        yield return ("SaveVersion", $"{a.SaveVersion}", $"{b.SaveVersion}");
        yield return ("SaveNumber", $"{a.SaveNumber}", $"{b.SaveNumber}");
        yield return ("PlayerName", a.PlayerName, b.PlayerName);
        yield return ("PlayerLevel", $"{a.PlayerLevel}", $"{b.PlayerLevel}");
        yield return ("Location", a.Location, b.Location);
        yield return ("Playtime", a.PlaytimeText, b.PlaytimeText);
        yield return ("Race", a.Race, b.Race);
        yield return ("Gender", $"{a.Gender}", $"{b.Gender}");
        yield return ("Experience", $"{a.Experience}", $"{b.Experience}");
        yield return ("ExperienceRequired", $"{a.ExperienceRequired}", $"{b.ExperienceRequired}");
        yield return ("SaveTimeUtc", $"{a.SaveTimeUtc:yyyy-MM-dd HH:mm:ss.fff}", $"{b.SaveTimeUtc:yyyy-MM-dd HH:mm:ss.fff}");
        yield return ("CurrentGameVersion", a.CurrentGameVersion, b.CurrentGameVersion);
        yield return ("CreatedGameVersion", a.CreatedGameVersion, b.CreatedGameVersion);
        yield return ("PostPluginUnknown", $"{a.PostPluginUnknown}", $"{b.PostPluginUnknown}");
        yield return ("ChangeFormCount", $"{a.ChangeFormCount}", $"{b.ChangeFormCount}");
    }
}
