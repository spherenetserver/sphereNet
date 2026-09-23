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
