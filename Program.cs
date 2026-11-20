using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SfsReader;

// sf-save-reader: read-only inspection of Starfield .sfs save files. Input saves are only ever opened for reading.
// Exit codes: 0 = done, 1 = done with warnings (or quest records that didn't decode), 2 = usage error, 3 = error.

const int SchemaVersion = 1;
string version = typeof(SaveFile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     .Split('+')[0] ?? "0.0.0";
string tool = $"sf-save-reader {version}";

const string Usage = """
    usage: sf-save-reader <command> <file.sfs> [options]

    commands:
      dump <file.sfs> [--json]          container, header, plugin list and section sizes (any save version);
                                        --json adds every change record for save version 153
      walk <file.sfs>                   read every change record and print a summary
      records <file.sfs> [--type N] [--plugin NAME] [--limit N] [--data [DIR]] [--lang en] [--json]
                                        change records (index from 0), with names from the game's plugins if --data is given
      quests <file.sfs> [--data [DIR]] [--lang en] [--filter TEXT] [--details] [--json]
                                        quest state: status, current stage, stages done, objectives
      compare <a.sfs> <b.sfs>           header fields, plugins and sections that differ between two saves
      extract <file.sfs> <out.bin>      write the decompressed save data to a NEW file (never overwrites)

    options for every command:
      --verbose                         write what the reader read and decided to stderr
      --log <file>                      write the same log to a file (created or replaced)
      --redact                          leave out the player name and the save's file name (it holds the
                                        character ID and the player name) in the output
      --data [DIR]                      the game's Data folder; with no folder (or "auto") it is looked for:
                                        STARFIELD_DATA, the Steam libraries, then X:\XboxGames\Starfield\Content\Data
      --help, --version

    Change records (walk, records, quests, dump --json) are read for save version 153 (Starfield 1.16.244) only.
    """;

// Help and version words count only in command position (so `--filter help` still filters).
if (args.Length >= 1 && args[0] is "--version" or "version"
    && args.Skip(1).All(a => a is "--verbose" or "--redact"))
{
    Console.WriteLine(tool);
    return 0;
}
if (args.Length == 0 || args[0] is "--help" or "-h" or "/?" or "help"
    || (args.Length == 2 && args[1] is "--help" or "-h" or "/?"))
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}

// ---- options for every command ----
var rest = new List<string>(args);
bool verbose = Take(rest, "--verbose");
bool redact = Take(rest, "--redact");
string? logPath = null;
int li = rest.IndexOf("--log");
if (li >= 0)
{
    if (li + 1 >= rest.Count || rest[li + 1].StartsWith("--"))
    {
        Console.Error.WriteLine("error: --log needs a file name, for example --log sf-save-reader.log");
        return 2;
    }
    logPath = rest[li + 1];
    rest.RemoveRange(li, 2);
    // Never let a mistyped --log replace a save: refuse a .sfs name, a file named elsewhere on the command
    // line, and any existing file that starts like a save (BCPS).
    string? why = null;
    try
    {
        string full = Path.GetFullPath(logPath);
        if (logPath.EndsWith(".sfs", StringComparison.OrdinalIgnoreCase)) why = "it ends in .sfs";
        else if (rest.Any(a => !a.StartsWith("--") && File.Exists(a) &&
                               string.Equals(Path.GetFullPath(a), full, StringComparison.OrdinalIgnoreCase)))
            why = "the same file is given as input";
        else if (File.Exists(full))
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var m = new byte[4];
            if (fs.Read(m, 0, 4) == 4 && m.AsSpan().SequenceEqual("BCPS"u8)) why = "the file is a Starfield save";
        }
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    if (why != null)
    {
        Console.Error.WriteLine($"error: --log {(redact ? "(file)" : logPath)}: won't write the log there, because {why}");
        Console.Error.WriteLine("fix: give --log a new file name, for example --log sf-save-reader.log");
        return 2;
    }
}
args = rest.ToArray();

StreamWriter? logFile = null;
if (logPath != null)
{
    try { logFile = new StreamWriter(logPath, append: false) { AutoFlush = true }; }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
        Console.Error.WriteLine($"error: can't write the log file {logPath}: {e.Message}");
        Console.Error.WriteLine("fix: give --log a path in a folder you can write to, for example your Documents folder");
        return 2;
    }
}
var clock = Stopwatch.StartNew();
void Log(string line)
{
    if (verbose) Console.Error.WriteLine($"[verbose] {line}");
    logFile?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}");
}
Log($"{tool} on .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
// --redact: a save's file name holds the character ID and the player name, so every file argument
// (a .sfs name, or any argument that is an existing file) is hidden wherever it could appear.
bool IsFileArg(string a, int i) => i > 0 && !a.StartsWith("--")
    && (a.EndsWith(".sfs", StringComparison.OrdinalIgnoreCase) || File.Exists(a));
string Scrub(string text)
{
    if (!redact) return text;
    for (int i = 1; i < args.Length; i++)
    {
        if (!IsFileArg(args[i], i)) continue;
        string full;
        try { full = Path.GetFullPath(args[i]); } catch (Exception) { full = args[i]; }
        foreach (var s in new[] { full, args[i], Path.GetFileName(args[i]) }.Where(s => s.Length > 0)
                                                                          .OrderByDescending(s => s.Length))
            text = text.Replace(s, "(save)", StringComparison.OrdinalIgnoreCase);
    }
    return text;
}
Log($"command: {string.Join(" ", args.Select((a, i) => redact && IsFileArg(a, i) ? "(save)" : a))}");

if (args.Length < 2)
{
    Console.Error.WriteLine($"error: {(args.Length == 0 ? "no command" : $"'{args[0]}' needs a save file")}\n\n{Usage}");
    return 2;
}

string current = args[1]; // the file named in an error
try
{
    int code = Run();
    Log($"exit {code} after {clock.ElapsedMilliseconds} ms");
    return code;
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException
                              or OverflowException or ArgumentException or OutOfMemoryException)
{
    string where = redact ? "(save)" : current;
    Console.Error.WriteLine($"error: {where}: {Scrub(e.Message)}");
    Console.Error.WriteLine($"fix: {Fix(e)}");
    Log($"error: {e.GetType().Name}: {Scrub(e.Message)}");
    Log(Scrub(e.StackTrace ?? ""));
    Log($"exit 3 after {clock.ElapsedMilliseconds} ms");
    return 3;
}
finally
{
    logFile?.Dispose();
}

int Run()
{
    switch (args[0].ToLowerInvariant())
    {
        case "dump":
        {
            var (c, g) = Load(args[1]);
            if (args.Contains("--json"))
            {
                // Record output needs a body this reader knows (save version 153); older saves get the header only.
                SaveBody? body = SaveBody.SupportedSaveVersions.Contains(g.SaveVersion) ? ParseBody(c, g) : null;
                if (body == null) Log($"save version {g.SaveVersion}: header and plugin list only (no change records)");
                var node = JsonSerializer.SerializeToNode(new
                {
                    schemaVersion = SchemaVersion, tool,
                    file = FileName(args[1]), container = Describe(c), save = g,
                    body = body == null ? null : new
                    {
                        formIds = body.FormIds.Length, typeGroups = body.TypeGroups, changeFormsEnd = body.ChangeFormsEnd,
                        warnings = body.Warnings, records = body.Records.Select(RecordJson),
                    },
                })!;
                if (redact) node["save"]!["PlayerName"] = "(redacted)";
                Console.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return g.Warnings.Count + (body?.Warnings.Count ?? 0) == 0 ? 0 : 1;
            }
            PrintDump(FileName(args[1]), c, g);
            return g.Warnings.Count == 0 ? 0 : 1;
        }
        case "records":
        {
            var (c, g) = Load(args[1]);
            var body = ParseBody(c, g);
            int? type = Opt(args, "--type") is string ts ? Number(ts, "--type") : null;
            string? plugin = Opt(args, "--plugin");
            int limit = Opt(args, "--limit") is string ls ? Number(ls, "--limit") : int.MaxValue;
            var list = body.Records.Where(r => (type == null || r.FormType == type)
                                               && (plugin == null || r.Form.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase)))
                .Take(limit).ToList();
            Log($"records: {list.Count:N0} of {body.Records.Count:N0} pass the filters");
            var game = Game(args);
            if (game != null)
                foreach (var grp in list.Where(r => game.HasPlugin(r.Form.Plugin)).GroupBy(r => r.Form.Plugin, StringComparer.OrdinalIgnoreCase))
                    game.Load(grp.Key, grp.Select(r => r.Form.LocalId).Distinct(), warnMissing: false);
            if (args.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        schemaVersion = SchemaVersion, tool, file = FileName(args[1]), records = list.Select(r => game == null ? RecordJson(r) : NamedRecordJson(r, game)),
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            else
                foreach (var r in list)
                    Console.WriteLine($"#{r.Index,-6} 0x{r.Offset:X8} type {r.FormType,-3} {r.Form,-48} change 0x{r.ChangeFlags:X8}" +
                                      $"{(r.HasChangeFlags ? "" : " (absent)")}  {r.References.Count} refs, {r.DataSize} bytes" +
                                      $"{(game?.Describe(r.Form) is { Length: > 0 } d ? "  " + d : "")}");
            foreach (var w in g.Warnings.Concat(body.Warnings).Concat(game?.Warnings ?? [])) Console.Error.WriteLine($"warning: {w}");
            return g.Warnings.Count + body.Warnings.Count == 0 ? 0 : 1;
        }
        case "quests":
        {
            var (c, g) = Load(args[1]);
            var body = ParseBody(c, g);
            var game = Game(args);
            var questRecords = body.Records.Where(r => r.FormType == 8).ToList();
            var notes = new List<string>();
            if (game != null) // one pass over each plugin for all its quests
            {
                foreach (var grp in questRecords.Where(r => game.HasPlugin(r.Form.Plugin)).GroupBy(r => r.Form.Plugin, StringComparer.OrdinalIgnoreCase))
                    game.Load(grp.Key, grp.Select(r => r.Form.LocalId).Distinct(), warnMissing: false);
                var missing = questRecords.Where(r => !r.Form.IsNull && !r.Form.IsCreated && !game.HasPlugin(r.Form.Plugin))
                    .GroupBy(r => r.Form.Plugin, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ToList();
                if (missing.Count > 0)
                    notes.Add($"{missing.Sum(x => x.Count())} quest record(s) belong to {missing.Count} plugin(s) that aren't in {game.DataDir}, " +
                              $"so they have no names (the mods were removed since the save?): " +
                              string.Join(", ", missing.Take(5).Select(x => $"{x.Key} ({x.Count()})")) + (missing.Count > 5 ? ", ..." : ""));
                foreach (var r in questRecords)
                    if (game.Get(r.Form) is { Signature: not "QUST" } other)
                        notes.Add($"{r.Form}: the form is a {other.Signature} in {other.Plugin}, not a quest (the plugin changed since the save?)");
            }
            string? filter = Opt(args, "--filter");
            bool details = args.Contains("--details"), json = args.Contains("--json");
            var rows = new List<object>();
            int decoded = 0, failed = 0;
            foreach (var r in questRecords)
            {
                QuestState q;
                try { q = QuestState.Decode(c.Inner, r); decoded++; }
                catch (InvalidDataException e) { failed++; Console.Error.WriteLine($"not decoded: {e.Message}"); continue; }
                string label = game?.Describe(r.Form) ?? "";
                if (filter != null && !$"{r.Form} {label}".Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (json)
                    rows.Add(new
                    {
                        index = r.Index, form = r.Form.ToString(), formId = $"{r.Form.FormId:X8}", plugin = r.Form.Plugin,
                        localId = $"{r.Form.LocalId:X}", name = label, status = q.Status, stage = q.CurrentStage,
                        completed = q.Completed, enabled = q.Enabled, pending = q.Pending, active = q.Active,
                        changeFlags = $"0x{r.ChangeFlags:X8}", questFlags = q.QuestFlags is uint f ? $"0x{f:X8}" : null,
                        stages = q.Stages?.Select(s => new { index = s.Index, done = s.Done }),
                        objectives = q.Objectives?.Select(o => new { index = o.Index, state = o.State, value = o.Value }),
                    });
                else
                {
                    Console.WriteLine($"{r.Form,-48} {q.Status}{(q.Completed ? ", completed" : "")}, stage {q.CurrentStage?.ToString() ?? "-"}" +
                                      $"{(label.Length > 0 ? "  " + label : "")}");
                    if (details)
                    {
                        if (q.Stages != null)
                            Console.WriteLine($"    stages: {string.Join(" ", q.Stages.Select(s => $"{s.Index}{(s.Done ? "*" : "")}"))}");
                        if (q.Objectives != null)
                            Console.WriteLine($"    objectives: {string.Join(" ", q.Objectives.Select(o => $"{o.Index}={o.State}"))}");
                    }
                }
            }
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = SchemaVersion, tool, file = FileName(args[1]), quests = rows },
                                                           new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"{decoded + failed} quest records: {decoded} decoded exactly, {failed} not decoded");
            Log($"quests: {decoded} decoded, {failed} not decoded, {rows.Count} shown");
            foreach (var w in g.Warnings.Concat(body.Warnings).Concat(game?.Warnings ?? []).Concat(notes))
                Console.Error.WriteLine($"warning: {w}");
            return failed > 0 || g.Warnings.Count + body.Warnings.Count > 0 ? 1 : 0;
        }
        case "walk":
        {
            var (c, g) = Load(args[1]);
            var body = ParseBody(c, g);
            PrintWalk(FileName(args[1]), g, body);
            return g.Warnings.Count + body.Warnings.Count == 0 ? 0 : 1;
        }
        case "compare" when args.Length >= 3:
        {
            var (ca, a) = Load(args[1]);
            current = args[2];
            var (cb, b) = Load(args[2]);
            Compare.Print(args[1], ca, a, args[2], cb, b, redact);
            foreach (var w in a.Warnings) Console.Error.WriteLine($"warning: A: {w}");
            foreach (var w in b.Warnings) Console.Error.WriteLine($"warning: B: {w}");
            return a.Warnings.Count + b.Warnings.Count > 0 ? 1 : 0;
        }
        case "extract" when args.Length >= 3:
        {
            if (File.Exists(args[2]))
                throw new IOException($"refusing to overwrite existing file {args[2]}; give a new file name");
            if (Path.GetFullPath(args[2]).Equals(Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase))
                throw new IOException("output path is the input save");
            var c = SfsContainer.Load(args[1]);
            using (var fs = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write))
                fs.Write(c.Inner);
            Console.WriteLine($"wrote {c.Inner.LongLength:N0} bytes to {args[2]}");
            return 0;
        }
        default:
            Console.Error.WriteLine($"error: unknown command or missing argument: {args[0]}\n\n{Usage}");
            return 2;
    }
}

(SfsContainer, SaveGame) Load(string path)
{
    current = path;
    if (!File.Exists(path))
        throw new FileNotFoundException($"no such file{(Directory.Exists(path) ? " (it is a folder; give one .sfs file)" : "")}", path);
    var t = clock.ElapsedMilliseconds;
    var (c, g) = SaveFile.Open(path);
    Log($"{(redact ? "(save)" : Path.GetFileName(path))}: {c.FileSize:N0} bytes, BCPS v{c.Version}, {c.ChunkSizes.Length} chunks " +
        $"({c.StoredChunks} stored) -> {c.Inner.LongLength:N0} bytes, {clock.ElapsedMilliseconds - t} ms");
    Log($"header: engine {g.EngineVersion}, save version {g.SaveVersion}, game {g.CurrentGameVersion}, " +
        $"{g.Plugins.Count} plugins ({g.Plugins.Count(p => p.Kind == "full")} full, {g.Plugins.Count(p => p.Kind == "medium")} medium, " +
        $"{g.Plugins.Count(p => p.Kind == "light")} light), {g.Warnings.Count} warning(s)");
    return (c, g);
}

SaveBody ParseBody(SfsContainer c, SaveGame g)
{
    var t = clock.ElapsedMilliseconds;
    var b = SaveBody.Parse(c.Inner, g);
    Log($"body: {b.FormIds.Length:N0} form IDs, {b.TypeGroups.Count} type groups, {b.Records.Count:N0} change records " +
        $"(slot [9] = {g.ChangeFormCount:N0}), end 0x{b.ChangeFormsEnd:X} (slot [5] = 0x{g.LocationTable[5]:X}), " +
        $"{b.Warnings.Count} warning(s), {clock.ElapsedMilliseconds - t} ms");
    return b;
}

GameData? Game(string[] a)
{
    int i = Array.IndexOf(a, "--data");
    if (i < 0) return null;
    string dir = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[i + 1] : "auto";
    if (dir.Equals("auto", StringComparison.OrdinalIgnoreCase))
    {
        current = "--data auto";
        dir = DataFolder.Find(Log) ?? throw new DirectoryNotFoundException(
            "no Starfield Data folder found (looked at STARFIELD_DATA, the Steam libraries and X:\\XboxGames\\Starfield\\Content\\Data)");
    }
    if (!Directory.Exists(dir))
    {
        current = dir;
        throw new DirectoryNotFoundException("the --data folder doesn't exist");
    }
    if (!GameData.HasFile(dir, "Starfield.esm"))
        Console.Error.WriteLine($"warning: {dir} has no Starfield.esm; is it the game's Data folder?");
    Log($"--data: {dir}");
    return new GameData(dir, Opt(a, "--lang") ?? "en") { Log = Log };
}

string FileName(string path) => redact ? "(redacted)" : Path.GetFileName(path);

string Fix(Exception e)
{
    string m = e.Message;
    return e switch
    {
        FileNotFoundException or DirectoryNotFoundException when current.StartsWith("--data") || m.Contains("--data") || m.Contains("Data folder") =>
            "give the game's Data folder with --data <folder> (Steam: ...\\steamapps\\common\\Starfield\\Data; " +
            "Game Pass: X:\\XboxGames\\Starfield\\Content\\Data), or set STARFIELD_DATA",
        FileNotFoundException or DirectoryNotFoundException =>
            "check the path. Steam saves are in Documents\\My Games\\Starfield\\Saves; put the path in quotes if it has spaces",
        UnauthorizedAccessException =>
            "this account can't read the file; copy the save to a folder you own and run the reader on the copy",
        IOException when m.StartsWith("refusing") || m.StartsWith("output path") => "give extract a new output file name",
        IOException => "close the program that has the file open (the game, an editor), or copy the save first",
        InvalidDataException when m.Contains("newer than this reader") =>
            "look for a newer sf-save-reader release; this one knows saves up to Starfield 1.16.244",
        InvalidDataException when m.StartsWith("save version") && m.Contains("isn't supported") =>
            $"{tool} reads change records for save version {string.Join(", ", SaveBody.SupportedSaveVersions)} (Starfield 1.16.244) only; `dump` still shows " +
            "the header and plugin list of older saves. Load the save in the current game and save again to update it",
        InvalidDataException or ArgumentException or OverflowException or OutOfMemoryException =>
            "is this a Starfield .sfs save? A cut-off copy, a Game Pass save still in its container (see the README, " +
            "\"Game Pass\") or another game's save gives this. Try another save; run with --verbose and include the log in a bug report",
        FormatException => "--type and --limit take whole numbers, for example --type 8 or --limit 100",
        _ => "run again with --verbose (or --log <file>) to see what was read",
    };
}

static bool Take(List<string> a, string name)
{
    bool found = a.Remove(name);
    while (a.Remove(name)) { }
    return found;
}

static string? Opt(string[] a, string name)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static int Number(string s, string option) =>
    int.TryParse(s, out int n) && n >= 0 ? n : throw new FormatException($"{option} needs a whole number, not '{s}'");

static object RecordJson(ChangeRecord r) => new
{
    index = r.Index, offset = r.Offset, form = r.Form.ToString(), formId = $"{r.Form.FormId:X8}", refKind = r.RefKind,
    type = r.FormType, headerFlags = r.HeaderFlags, version = r.Version, changeFlags = $"0x{r.ChangeFlags:X8}",
    hasChangeFlags = r.HasChangeFlags, dataSize = r.DataSize, strings = r.StringIndexes.Count,
    references = r.References.Select(x => x.ToString()),
};

/// <summary>The same fields plus the form's name from the game's plugins (records --json with --data).</summary>
static object NamedRecordJson(ChangeRecord r, GameData game) => new
{
    index = r.Index, offset = r.Offset, form = r.Form.ToString(), formId = $"{r.Form.FormId:X8}", refKind = r.RefKind,
    type = r.FormType, headerFlags = r.HeaderFlags, version = r.Version, changeFlags = $"0x{r.ChangeFlags:X8}",
    hasChangeFlags = r.HasChangeFlags, dataSize = r.DataSize, strings = r.StringIndexes.Count,
    references = r.References.Select(x => x.ToString()),
    name = game.Describe(r.Form),
};

static void PrintWalk(string file, SaveGame g, SaveBody b)
{
    var t = g.LocationTable;
    Console.WriteLine($"File            {file}");
    Console.WriteLine($"Form-ID array   {b.FormIds.Length:N0} IDs at 0x{t[0]:X}");
    Console.WriteLine($"Type directory  {b.TypeGroups.Count} groups, {b.TypeGroups.Sum(x => (long)x.Count):N0} records");
    Console.WriteLine($"Change records  {b.Records.Count:N0} read (slot [9] = {t[9]:N0}), from 0x{t[4]:X} to 0x{b.ChangeFormsEnd:X} " +
                      $"(slot [5] = 0x{t[5]:X}{(b.ChangeFormsEnd == t[5] ? ", exact" : ", MISMATCH")})");
    var refs = b.Records.SelectMany(r => r.References).ToList();
    Console.WriteLine($"References      {refs.Count:N0}: kind 0 = {refs.Count(x => x.Kind == 0):N0}, kind 1 = {refs.Count(x => x.Kind == 1):N0}, " +
                      $"kind 2 = {refs.Count(x => x.Kind == 2):N0}");
    Console.WriteLine($"Header flags    " + string.Join(", ", Enumerable.Range(0, 8)
        .Select(bit => $"bit {bit} = {b.Records.Count(r => (r.HeaderFlags & (1 << bit)) != 0):N0}")));
    Console.WriteLine($"No change flags {b.Records.Count(r => !r.HasChangeFlags):N0} records (types " +
                      $"{string.Join(", ", b.Records.Where(r => !r.HasChangeFlags).Select(r => r.FormType).Distinct().Order())})");
    Console.WriteLine($"Created records {b.Records.Count(r => r.RefKind == 2):N0}");
    Console.WriteLine($"Quest records   {b.Records.Count(r => r.FormType == 8):N0}");
    foreach (var w in g.Warnings.Concat(b.Warnings))
        Console.WriteLine($"WARNING         {w}");
}

static object Describe(SfsContainer c) => new
{
    c.Version, c.ChunkTableOffset, c.DataOffset, c.UncompressedSize, c.ChunkSize, c.Alignment,
    Unknown28 = $"0x{c.Unknown28:X8}", Unknown40 = $"0x{c.Unknown40:X8}", c.Compression,
    Chunks = c.ChunkSizes.Length, c.StoredChunks, c.FileSize, c.DataEnd,
};

void PrintDump(string file, SfsContainer c, SaveGame g)
{
    Console.WriteLine($"File            {file}");
    Console.WriteLine($"Container       BCPS v{c.Version}, {c.Compression.Trim()} x{c.ChunkSizes.Length} chunks of {c.ChunkSize:N0}" +
                      $"{(c.StoredChunks > 0 ? $" ({c.StoredChunks} stored)" : "")}, align {c.Alignment}, " +
                      $"{c.FileSize:N0} -> {c.UncompressedSize:N0} bytes");
    Console.WriteLine($"Engine / save   {g.EngineVersion} / {g.SaveVersion}  (header {g.HeaderSize} bytes)");
    Console.WriteLine($"Save number     {g.SaveNumber}");
    Console.WriteLine($"Player          {(redact ? "(redacted)" : g.PlayerName)}, level {g.PlayerLevel}, {g.Race}, gender {g.Gender}");
    Console.WriteLine($"Experience      {g.Experience} / {g.ExperienceRequired}");
    Console.WriteLine($"Location        {g.Location}");
    Console.WriteLine($"Playtime        {g.PlaytimeText}");
    Console.WriteLine($"Saved (UTC)     {g.SaveTimeUtc:yyyy-MM-dd HH:mm:ss.fff}");
    Console.WriteLine($"Game version    {g.CurrentGameVersion} (created {g.CreatedGameVersion})");
    Console.WriteLine($"Plugins         {g.Plugins.Count} ({g.Plugins.Count(p => p.Kind == "full")} full, " +
                      $"{g.Plugins.Count(p => p.Kind == "light")} light, {g.Plugins.Count(p => p.Kind == "medium")} medium)");
    foreach (var p in g.Plugins)
        Console.WriteLine($"  {p.Kind,-6} {p.Name}{(p.HasExtra && !string.IsNullOrEmpty(p.CreationName) ? $"  [{p.CreationName}]" : "")}");
    Console.WriteLine($"After plugins   unknown u16 = {g.PostPluginUnknown?.ToString() ?? "(absent)"}");
    Console.WriteLine($"Counts          global data 1/2/3 = {g.GlobalData1Count}/{g.GlobalData2Count}/{g.GlobalData3Count}, change forms = {g.ChangeFormCount}");
    Console.WriteLine("Sections (label = Fallout 4 naming, inference)");
    foreach (var s in g.Sections)
        Console.WriteLine($"  0x{s.Offset:X8}  {s.Size,12:N0}  {s.Name}");
    foreach (var w in g.Warnings)
        Console.WriteLine($"WARNING         {w}");
}
