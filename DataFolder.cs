using System.Text.RegularExpressions;

namespace SfsReader;

/// <summary>Finds the game's Data folder for <c>--data auto</c>: the STARFIELD_DATA environment variable, then every
/// Steam library (from Steam's libraryfolders.vdf), then the Game Pass / Xbox app folder on each fixed drive.
/// Read-only; it only checks that a folder holds Starfield.esm.</summary>
public static class DataFolder
{
    /// <summary>The folders to try, in order (they may not exist).</summary>
    public static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("STARFIELD_DATA") is { Length: > 0 } env) yield return env;
        foreach (var steam in SteamRoots())
        {
            yield return Path.Combine(steam, "steamapps", "common", "Starfield", "Data");
            foreach (var lib in SteamLibraries(steam))
                yield return Path.Combine(lib, "steamapps", "common", "Starfield", "Data");
        }
        foreach (var drive in FixedDrives())
            yield return Path.Combine(drive, "XboxGames", "Starfield", "Content", "Data");
    }

    /// <summary>The first candidate that holds Starfield.esm, or null. Each folder tried goes to <paramref name="log"/>.</summary>
    public static string? Find(Action<string>? log = null)
    {
        foreach (var dir in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool ok = File.Exists(Path.Combine(dir, "Starfield.esm"));
            log?.Invoke($"--data auto: {dir} {(ok ? "holds Starfield.esm, using it" : "(no Starfield.esm)")}");
            if (ok) return dir;
        }
        return null;
    }

    static IEnumerable<string> SteamRoots()
    {
        foreach (var v in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } pf)
                yield return Path.Combine(pf, "Steam");
        if (Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
            yield return Path.Combine(home, ".steam", "steam"); // Steam on Linux (Proton)
    }

    static IEnumerable<string> SteamLibraries(string steamRoot)
    {
        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        string text;
        try { text = File.Exists(vdf) ? File.ReadAllText(vdf) : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text = ""; }
        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    static IEnumerable<string> FixedDrives()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var d in DriveInfo.GetDrives())
        {
            bool fixedDrive;
            try { fixedDrive = d.DriveType == DriveType.Fixed && d.IsReady; }
            catch (IOException) { fixedDrive = false; }
            if (fixedDrive) yield return d.RootDirectory.FullName;
        }
    }
}
