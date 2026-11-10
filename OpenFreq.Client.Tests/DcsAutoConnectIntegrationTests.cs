using System.IO;
using System.Reflection;
using System.Text.Json;
using OpenFreqClient.Models;
using OpenFreqClient.Services;

namespace OpenFreq.Client.Tests;

/// <summary>T3: single instance per profile.</summary>
public class SingleInstanceGuardTests
{
    private static string UniqueName() => SingleInstanceGuard.NameFor("-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SecondAcquire_FailsUntilFirstIsReleased()
    {
        var name = UniqueName();
        using (var first = SingleInstanceGuard.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstanceGuard.TryAcquire(name));
        }

        using var again = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void ProfilesHaveSeparateNames()
    {
        Assert.Equal(@"Local\OpenFreq.Client", SingleInstanceGuard.NameFor(""));
        Assert.NotEqual(SingleInstanceGuard.NameFor(""), SingleInstanceGuard.NameFor("-test2"));
    }

    [Fact]
    public void SignalShow_ReachesTheRunningInstance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = UniqueName();
        using var guard = SingleInstanceGuard.TryAcquire(name)!;
        using var shown = new ManualResetEventSlim();
        guard.ListenForShow(() => shown.Set());
        Assert.True(SingleInstanceGuard.SignalShow(name));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SignalShow_WithoutRunningInstance_IsFalse() => Assert.False(SingleInstanceGuard.SignalShow(UniqueName()));
}

/// <summary>client.json, hook install/removal and settings persistence.</summary>
public class DcsAutoConnectInstallTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openfreq-ac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ClientJson_HasPortTokenExeAndFlags()
    {
        var exe = @"C:\Users\Pilot\AppData\Local\OpenFreq DCS Client\OpenFreq.Client.exe";
        var json = DcsExportInstaller.BuildClientConfigJson(
            new AutoConnectClientConfig(34323, "0123456789abcdef0123456789abcdef", exe, true, false));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(34323, root.GetProperty("port").GetInt32());
        Assert.Equal("0123456789abcdef0123456789abcdef", root.GetProperty("token").GetString());
        Assert.Equal(exe, root.GetProperty("exe").GetString());
        Assert.True(root.GetProperty("autoConnect").GetBoolean());
        Assert.False(root.GetProperty("autoLaunch").GetBoolean());
    }

    [Fact]
    public void ClientJson_RefreshUpdatesExeOnly_AndNeverCreates()
    {
        var dir = TempDir();
        try
        {
            DcsExportInstaller.RefreshAutoConnectExePath(dir, @"D:\Portable\OpenFreq.Client.exe");
            Assert.False(File.Exists(DcsExportInstaller.ClientConfigPath(dir)));

            DcsExportInstaller.WriteClientConfigFile(dir,
                new AutoConnectClientConfig(34323, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", @"C:\Old\OpenFreq.Client.exe", true, true));
            DcsExportInstaller.RefreshAutoConnectExePath(dir, @"D:\Portable\OpenFreq.Client.exe");

            using var doc = JsonDocument.Parse(File.ReadAllText(DcsExportInstaller.ClientConfigPath(dir)));
            Assert.Equal(@"D:\Portable\OpenFreq.Client.exe", doc.RootElement.GetProperty("exe").GetString());
            Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", doc.RootElement.GetProperty("token").GetString());
            Assert.True(doc.RootElement.GetProperty("autoConnect").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("autoLaunch").GetBoolean());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RemoveHook_DeletesOnlyOurLoader()
    {
        var dir = TempDir();
        try
        {
            var hooks = Path.Combine(dir, "Scripts", "Hooks");
            Directory.CreateDirectory(hooks);
            var ours = Path.Combine(hooks, DcsExportInstaller.AutoConnectHookFileName);
            var srs = Path.Combine(hooks, "DCS-SRS-hook.lua");
            File.WriteAllText(srs, "-- someone else's hook");

            File.WriteAllText(ours, "-- same name, not ours");
            DcsExportInstaller.RemoveAutoConnectHook(dir);
            Assert.True(File.Exists(ours));

            File.WriteAllBytes(ours, ReadEmbedded("DCS/OpenFreqDCS/Hooks/OpenFreq-ClientGameGUI.lua"));
            DcsExportInstaller.RemoveAutoConnectHook(dir);
            Assert.False(File.Exists(ours));
            Assert.True(File.Exists(srs));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HookAndModule_AreEmbedded()
    {
        Assert.NotEmpty(ReadEmbedded("DCS/OpenFreqDCS/Hooks/OpenFreq-ClientGameGUI.lua"));
        Assert.NotEmpty(ReadEmbedded("DCS/OpenFreqDCS/Scripts/OpenFreqAutoConnectGameGUI.lua"));
    }

    [Fact]
    public void Settings_RoundTrip_AndDefaultsForOlderFiles()
    {
        var settings = new OpenFreqSettings
        {
            AutoConnectEnabled = true,
            AutoLaunchEnabled = true,
            AutoReconnectEnabled = false,
            DisconnectOnServerLeave = false,
            AllowAutomaticServerSwitching = true,
            CloseAppOnServerLeave = true,
            AutoConnectConsentAsked = true,
            AutoConnectServerPasswords = { ["voice.example.com:9937"] = "pw" },
        };
        var json = OpenFreqClient.Json.Json.Instance.Serialize(new AppConfiguration { Settings = settings });
        var back = OpenFreqClient.Json.Json.Instance.Deserialize<AppConfiguration>(json)!.Settings;
        Assert.True(back.AutoConnectEnabled);
        Assert.True(back.AutoLaunchEnabled);
        Assert.False(back.AutoReconnectEnabled);
        Assert.False(back.DisconnectOnServerLeave);
        Assert.True(back.AllowAutomaticServerSwitching);
        Assert.True(back.CloseAppOnServerLeave);
        Assert.True(back.AutoConnectConsentAsked);
        Assert.Equal("pw", back.AutoConnectServerPasswords["voice.example.com:9937"]);

        var old = OpenFreqClient.Json.Json.Instance.Deserialize<AppConfiguration>("""{"settings":{"openFreqServerAddress":"a:1"}}""")!.Settings;
        Assert.False(old.AutoConnectEnabled);
        Assert.False(old.AutoLaunchEnabled);
        Assert.True(old.AutoReconnectEnabled);
        Assert.True(old.DisconnectOnServerLeave);
        Assert.False(old.AllowAutomaticServerSwitching);
        Assert.False(old.CloseAppOnServerLeave);
        Assert.False(old.AutoConnectConsentAsked);
        Assert.Empty(old.AutoConnectServerPasswords);
    }

    private static byte[] ReadEmbedded(string logicalName)
    {
        // %(RecursiveDir) puts a backslash into the logical name on Windows; normalise like the installer.
        var assembly = typeof(DcsExportInstaller).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Replace('\\', '/').Equals(logicalName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(name);
        using var stream = assembly.GetManifestResourceStream(name!);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        stream!.CopyTo(memory);
        return memory.ToArray();
    }
}
