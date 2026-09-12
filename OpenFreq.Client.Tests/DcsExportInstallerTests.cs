using System.IO;
using System.Reflection;
using OpenFreqClient.Services;
using Xunit;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Covers DcsExportInstaller's Export.lua merge logic -- the hook is inserted via a scoped
/// "-- OpenFreqDCS BEGIN/END" marker block rather than ever overwriting the file wholesale, so
/// other tools that also hook Export.lua (most notably SRS -- DCS-SimpleRadioStandalone, which a
/// lot of users run alongside OpenFreq) keep their own hook intact regardless of install order.
/// EnsureExportHook is private, so it's exercised directly via reflection rather than needing a
/// full DcsExportInstaller/EnsureInstalled pass. The whole method is compiled only under
/// #if WINDOWS (DCS itself is Windows-only), so it doesn't exist in a Linux build's assembly at
/// all -- these tests early-return on non-Windows hosts (e.g. the Linux CI runner) instead of
/// failing a reflection lookup for a method that was never compiled in.
/// </summary>
public class DcsExportInstallerTests
{
    private static void InvokeEnsureExportHook(string exportLuaPath)
    {
        var method = typeof(DcsExportInstaller).GetMethod("EnsureExportHook",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [exportLuaPath]);
    }

    private static void InvokeRemoveExportHook(string exportLuaPath)
    {
        var method = typeof(DcsExportInstaller).GetMethod("RemoveExportHook",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [exportLuaPath]);
    }

    private static bool SkipOnNonWindows() => !OperatingSystem.IsWindows();

    // Not SRS's real, exact hook text (that's a separate MIT-licensed project we don't vendor) --
    // just plausible foreign content in the same "a dofile call plus maybe a comment" shape any
    // other Export.lua-hooking tool would leave, enough to prove we never touch content we didn't
    // write ourselves.
    private const string ForeignHook =
        "local lfs=require('lfs');dofile(lfs.writedir()..[[Scripts\\SomeOtherTool\\Hook.lua]])";

    [Fact]
    public void NoExistingFile_CreatesFileWithJustOurHook()
    {
        if (SkipOnNonWindows()) return;

        var path = Path.GetTempFileName();
        File.Delete(path);
        try
        {
            InvokeEnsureExportHook(path);

            var content = File.ReadAllText(path);
            Assert.Contains("-- OpenFreqDCS BEGIN", content);
            Assert.Contains("-- OpenFreqDCS END", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExistingForeignHook_IsPreservedVerbatim()
    {
        if (SkipOnNonWindows()) return;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, ForeignHook);

            InvokeEnsureExportHook(path);

            var content = File.ReadAllText(path);
            Assert.Contains(ForeignHook, content);
            Assert.Contains("-- OpenFreqDCS BEGIN", content);
            Assert.Contains("-- OpenFreqDCS END", content);
            // Foreign content must come first -- we only ever append, never prepend or interleave.
            Assert.True(content.IndexOf(ForeignHook, StringComparison.Ordinal) <
                        content.IndexOf("-- OpenFreqDCS BEGIN", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RunningTwice_DoesNotDuplicateOurHook_AndStillPreservesForeignContent()
    {
        if (SkipOnNonWindows()) return;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, ForeignHook);

            InvokeEnsureExportHook(path);
            InvokeEnsureExportHook(path);

            var content = File.ReadAllText(path);
            var beginCount = content.Split("-- OpenFreqDCS BEGIN").Length - 1;
            Assert.Equal(1, beginCount);
            Assert.Contains(ForeignHook, content);
            // Foreign content appears exactly once too -- we never duplicated it ourselves.
            Assert.Equal(1, content.Split(ForeignHook).Length - 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReinstallingOverAnOlderOwnHook_ReplacesOnlyOurOwnBlock()
    {
        if (SkipOnNonWindows()) return;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                ForeignHook + "\r\n\r\n-- OpenFreqDCS BEGIN\r\nold stale content\r\n-- OpenFreqDCS END\r\n");

            InvokeEnsureExportHook(path);

            var content = File.ReadAllText(path);
            Assert.Contains(ForeignHook, content);
            Assert.DoesNotContain("old stale content", content);
            Assert.Equal(1, content.Split("-- OpenFreqDCS BEGIN").Length - 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemoveExportHook_StripsOurBlock_PreservesForeignContent()
    {
        if (SkipOnNonWindows()) return;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                ForeignHook + "\r\n\r\n-- OpenFreqDCS BEGIN\r\nour hook content\r\n-- OpenFreqDCS END\r\n");

            InvokeRemoveExportHook(path);

            var content = File.ReadAllText(path);
            Assert.Contains(ForeignHook, content);
            Assert.DoesNotContain("-- OpenFreqDCS BEGIN", content);
            Assert.DoesNotContain("our hook content", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemoveExportHook_NoExistingFile_DoesNotThrowOrCreateOne()
    {
        if (SkipOnNonWindows()) return;

        var path = Path.GetTempFileName();
        File.Delete(path);
        try
        {
            InvokeRemoveExportHook(path);

            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemoveExportHook_OnlyOurHookPresent_LeavesFileEffectivelyEmpty()
    {
        if (SkipOnNonWindows()) return;

        var path = Path.GetTempFileName();
        try
        {
            InvokeEnsureExportHook(path);

            InvokeRemoveExportHook(path);

            var content = File.ReadAllText(path);
            Assert.DoesNotContain("-- OpenFreqDCS BEGIN", content);
            Assert.DoesNotContain(ExportTokenForTests, content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private const string ExportTokenForTests = "Mods\\Services\\OpenFreqDCS\\Scripts\\OpenFreqDCS.lua";
}
