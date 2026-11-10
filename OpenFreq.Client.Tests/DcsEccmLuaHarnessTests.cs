using System.ComponentModel;
using System.Diagnostics;
using Xunit.Abstractions;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Runs DCS/tests/eccm_harness.lua against the real OpenFreqDCS.lua with a stand-alone Lua interpreter: ARC-210
/// ECCM page parsing (HQ net, readiness, SINCGARS), the CT-TD fix and the ARC-164 virtual HQ knob. Needs a `lua`
/// on PATH (or OPENFREQ_LUA pointing at one); without it the test passes without running (CI has no Lua).
/// </summary>
public class DcsEccmLuaHarnessTests
{
    private readonly ITestOutputHelper _out;

    public DcsEccmLuaHarnessTests(ITestOutputHelper output) => _out = output;

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DCS", "tests", "eccm_harness.lua")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public void EccmExport_ParsesTheArc210EccmPage_AndTheArc164Knob()
    {
        var root = RepoRoot();
        if (root == null)
        {
            _out.WriteLine("skipped: repository sources not found");
            return;
        }

        var lua = Environment.GetEnvironmentVariable("OPENFREQ_LUA");
        if (string.IsNullOrEmpty(lua)) lua = "lua";

        var start = new ProcessStartInfo(lua)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root
        };
        start.ArgumentList.Add(Path.Combine(root, "DCS", "tests", "eccm_harness.lua"));
        start.ArgumentList.Add(Path.Combine(root, "DCS", "OpenFreqDCS", "Scripts", "OpenFreqDCS.lua"));

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            _out.WriteLine($"skipped: no Lua interpreter ({lua})");
            return;
        }
        Assert.NotNull(process);

        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "lua harness timed out");
        _out.WriteLine(stdout);
        _out.WriteLine(stderr);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("ECCM HARNESS OK", stdout);
    }
}
