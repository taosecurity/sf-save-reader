using System.Diagnostics;
using System.Reflection;

namespace SfsReader.Tests;

/// <summary>Text shown to users names the tool's real version (from the assembly), never a hard-coded number.
/// Runs the built sf-save-reader on a made-up save.</summary>
public class VersionTextTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // The dotnet host that runs these tests (dotnet test sets DOTNET_HOST_PATH), else the one on PATH.
    static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    static string AssemblyVersion() =>
        typeof(SaveFile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    (int Code, string Out, string Err) RunTool(params string[] args)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "sf-save-reader.dll");
        Assert.True(File.Exists(dll), $"{dll} wasn't built next to the tests");
        var psi = new ProcessStartInfo(DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            p.Kill(entireProcessTree: true);   // don't leave it running with the test save open
            Assert.Fail("sf-save-reader didn't finish in 60 seconds");
        }
        output.WriteLine($"sf-save-reader {string.Join(" ", args)} -> exit {p.ExitCode}\n{stderr.Result}");
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void The_unsupported_save_fix_names_the_assembly_version()
    {
        // A made-up save version 152, stored in one uncompressed BCPS chunk.
        var inner = new FakeSave { SaveVersion = 152 }.Build();
        var file = new Writer();
        file.Ascii("BCPS").U32(1).U64(0x48).U64(0).U64(0x100).U64((ulong)inner.Length).U64(0).U32(0x40000).U32(0).U32(16).U32(0)
            .U32(0).Ascii("ZIP ");
        file.U32((uint)inner.Length);
        file.Bytes(new byte[0x100 - file.Length]);
        file.Bytes(inner);

        string path = Path.Combine(Path.GetTempPath(), $"sfsreader-test-{Guid.NewGuid():N}.sfs");
        try
        {
            File.WriteAllBytes(path, file.ToArray());
            var (code, _, err) = RunTool("walk", path);
            string version = AssemblyVersion();
            Assert.Equal(3, code);
            Assert.Contains("isn't supported", err);
            Assert.Contains($"fix: sf-save-reader {version} reads change records", err);
            // The same version --version prints.
            var (vcode, vout, _) = RunTool("--version");
            Assert.Equal(0, vcode);
            Assert.Contains($"sf-save-reader {version}", vout);
        }
        finally { File.Delete(path); }
    }
}
