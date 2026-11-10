using System.Diagnostics;
using System.IO;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Runs tools/LuaHookTests/run.lua (the auto-connect DCS hook tests with stubbed DCS globals)
/// when a Lua interpreter is on PATH. Without one (e.g. a CI runner with no Lua installed) the
/// test returns early, the same skip pattern DcsExportInstallerTests uses for Windows-only code.
/// </summary>
public class LuaHookHarnessTests
{
    private static readonly string[] Interpreters = ["lua", "lua5.4", "lua5.3", "lua5.1", "luajit"];

    [Fact]
    public void AutoConnectHooks_LuaHarnessPasses()
    {
        var lua = FindInterpreter();
        if (lua == null)
            return;

        var root = FindRepoRoot();
        Assert.NotNull(root);

        var psi = new ProcessStartInfo(lua)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Path.Combine(root!, "tools", "LuaHookTests", "run.lua"));
        psi.ArgumentList.Add(root!);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "Lua harness timed out");
        Assert.True(process.ExitCode == 0, $"Lua harness failed:\n{stdout.Result}\n{stderr.Result}");
        Assert.Contains(" 0 failed", stdout.Result);
    }

    private static string? FindInterpreter()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe" } : new[] { "" };
        foreach (var name in Interpreters)
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var ext in extensions)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OpenFreq.sln")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
