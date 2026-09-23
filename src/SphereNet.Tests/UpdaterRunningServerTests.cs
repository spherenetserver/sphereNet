using SphereNet.Updater;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// VDS field report: the updater reported no running server, deleted the panel
/// folder of a live install and failed on "Access to the path ...\panel\assets is
/// denied". AppContext.BaseDirectory ends in a separator, and the running-process
/// check appended a second one, so no path ever matched.
/// </summary>
public sealed class UpdaterRunningServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_updrun_" + Guid.NewGuid().ToString("N"));

    public UpdaterRunningServerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(@"C:\sphereNet\SphereNet.Host.exe", @"C:\sphereNet\", true)]    // BaseDirectory keeps the slash
    [InlineData(@"C:\sphereNet\SphereNet.Host.exe", @"C:\sphereNet", true)]
    [InlineData(@"C:\sphereNet2\SphereNet.Host.exe", @"C:\sphereNet", false)]
    [InlineData(@"C:\SPHERENET\bin\x.exe", @"c:\spherenet\", true)]
    public void AProcessUnderTheInstallFolderIsFoundWhateverTheTrailingSlash(string exe, string dir, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, UpdaterEngine.IsUnderDir(exe, dir));
    }

    [Fact]
    public void TheInstallFolderIsStoredWithoutATrailingSeparator()
    {
        var engine = new UpdaterEngine(_dir + Path.DirectorySeparatorChar, new UpdaterSettings(), _ => { });
        Assert.False(engine.InstallDir.EndsWith(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AReadOnlyFileDoesNotStopAFolderReplace()
    {
        string folder = Path.Combine(_dir, "panel", "assets");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "index.js");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        UpdaterEngine.DeleteDirectory(Path.Combine(_dir, "panel"), attempts: 1);
        Assert.False(Directory.Exists(Path.Combine(_dir, "panel")));
    }
}

/// <summary>Second VDS report: the panel folder still could not be deleted
/// ("Access denied" on panel\assets) and the whole update rolled back. A folder that
/// cannot be removed is now overwritten in place, read-only folders are cleared, and
/// a failure names the process holding the files.</summary>
public sealed class UpdaterFolderReplaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_updfold_" + Guid.NewGuid().ToString("N"));

    public UpdaterFolderReplaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(e, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private UpdaterEngine Engine(List<string> log) =>
        new(_dir, new UpdaterSettings(), log.Add);

    [Fact]
    public void ALockedStaleFileDoesNotStopTheFolderUpdate()
    {
        if (!OperatingSystem.IsWindows()) return;
        string staged = Path.Combine(_dir, "staged", "panel");
        Directory.CreateDirectory(Path.Combine(staged, "assets"));
        File.WriteAllText(Path.Combine(staged, "index.html"), "new");
        File.WriteAllText(Path.Combine(staged, "assets", "app-new.js"), "new");

        string live = Path.Combine(_dir, "panel");
        Directory.CreateDirectory(Path.Combine(live, "assets"));
        File.WriteAllText(Path.Combine(live, "index.html"), "old");
        string stale = Path.Combine(live, "assets", "app-old.js");
        File.WriteAllText(stale, "old");

        var log = new List<string>();
        using (new FileStream(stale, FileMode.Open, FileAccess.Read, FileShare.Read))   // held open
            Engine(log).ReplaceDirectory(staged, live);

        Assert.Equal("new", File.ReadAllText(Path.Combine(live, "index.html")));
        Assert.True(File.Exists(Path.Combine(live, "assets", "app-new.js")));
        Assert.Contains(log, l => l.Contains("uzerine yaziliyor"));
    }

    [Fact]
    public void AReadOnlyFolderIsDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        string folder = Path.Combine(_dir, "panel", "assets");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.js"), "x");
        File.SetAttributes(folder, FileAttributes.Directory | FileAttributes.ReadOnly);

        UpdaterEngine.DeleteDirectory(Path.Combine(_dir, "panel"), attempts: 1);
        Assert.False(Directory.Exists(Path.Combine(_dir, "panel")));
    }

    [Fact]
    public void TheProcessHoldingAFileIsNamed()
    {
        if (!OperatingSystem.IsWindows()) return;
        string file = Path.Combine(_dir, "held.bin");
        File.WriteAllText(file, "x");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var holders = FileLockInfo.WhoIsLocking([file]);

        Assert.Contains(holders, h => h.Contains($"pid {Environment.ProcessId}"));
    }
}
