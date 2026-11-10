using System.Diagnostics;
using System.Text;
using OpenFreq.Common.Jamming;
using OpenFreqServer.Jamming;

namespace OpenFreqServer.Tests.Jamming;

/// <summary>
/// The DCS server hook (Lua) and the OpenFreq server intake (C#) must agree on the wire format: a datagram built by
/// the real hook (tools/LuaHookTests/make_datagram.lua) must authenticate and parse here. Skipped when no Lua
/// interpreter is on PATH (as LuaHookHarnessTests does).
/// </summary>
public class MissionJammerLuaInteropTests
{
    private const string Secret = "0123456789abcdef-interop";
    private static readonly string[] Interpreters = ["lua", "lua5.4", "lua5.3", "lua5.1", "luajit"];

    [Fact]
    public void DatagramFromTheLuaHookIsAcceptedAndBuildsTheJammers()
    {
        var lua = FindInterpreter();
        if (lua == null) return;
        var root = FindRepoRoot();
        Assert.NotNull(root);

        var psi = new ProcessStartInfo(lua)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.Latin1
        };
        psi.ArgumentList.Add(Path.Combine(root!, "tools", "LuaHookTests", "make_datagram.lua"));
        psi.ArgumentList.Add(root!);
        psi.ArgumentList.Add(Secret);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), "lua timed out");
        Assert.True(process.ExitCode == 0, stderr.Result);
        var datagram = Encoding.Latin1.GetBytes(stdout.Result); // raw bytes as written

        var protocol = new MissionJammerProtocol(Secret);
        var report = protocol.TryAccept(datagram, DateTimeOffset.UtcNow);
        Assert.True(report != null, protocol.LastRejection);
        Assert.Equal("0badc0de", report.Boot);
        Assert.Equal(42, report.Seq);
        Assert.Equal(3, report.Units.Count);
        Assert.Equal("Ural | dead", report.Units[1].Name);

        var warnings = new List<string>();
        var defs = MissionJammerProtocol.BuildDefinitions(report, "JAM:", 200, _ => null, warnings);
        Assert.Empty(warnings);
        Assert.Equal(["dcs:JAM:r934b;spot=251 #001", "dcs:Ural | dead", "dcs:JAM:il22pp"], defs.Select(d => d.Id));

        Assert.Equal([251_000], defs[0].Targeting.SpotFrequenciesKhz);
        Assert.Equal(512.5 + 12, defs[0].AltitudeMeters);
        Assert.Equal(647000.25, defs[0].DcsPosition!.Z);
        Assert.Equal("leer2", defs[1].PresetId); // group tag
        Assert.False(defs[1].Alive);
        Assert.Equal(7000, defs[2].AltitudeMeters); // air: no mast

        Assert.Null(new MissionJammerProtocol("a-different-secret-xx").TryAccept(datagram, DateTimeOffset.UtcNow));
    }

    private static string? FindInterpreter()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var name in Interpreters)
        foreach (var candidate in new[] { Path.Combine(dir, name), Path.Combine(dir, name + ".exe") })
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DCS", "Server", "OpenFreq-ServerGameGUI.lua")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
