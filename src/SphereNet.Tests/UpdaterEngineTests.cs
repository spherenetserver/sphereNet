using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SphereNet.Updater;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// SphereNet.Updater against a fake GitHub release: the nightly package is laid over
/// the install, user data is never touched, missing config comes from the
/// defaults\config templates, and a failed copy is rolled back.
/// </summary>
public sealed class UpdaterEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_upd_" + Guid.NewGuid().ToString("N"));

    public UpdaterEngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeRelease(Dictionary<string, byte[]> assets) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string name = request.RequestUri!.Segments[^1];
            return Task.FromResult(assets.TryGetValue(name, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string VersionJson(long build) =>
        $$"""{"sha":"abcdef0123456789","shortSha":"abcdef0","branch":"main","buildNumber":{{build}},"builtAt":"2026-09-23T00:00:00Z","runtime":"win-x64","commitSubject":"build {{build}}"}""";

    /// <summary>A package shaped like build.ps1's output, plus a config\sphere.ini the
    /// updater must refuse to lay over the user's.</summary>
    private static byte[] Package(long build, string hostBody = "host")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(text);
            }
            Add("SphereNet.Host.exe", hostBody);
            Add("SphereNet.Server.exe", "server");
            Add("version.json", VersionJson(build));
            Add("panel/index.html", "<html/>");
            Add("defaults/config/sphere.ini", "[SPHERE]\nSERVNAME=Template\n");
            Add("defaults/config/sphereCrypt.ini", "[crypt]\n");
            Add("config/sphere.ini", "SHOULD NEVER LAND");
        }
        return ms.ToArray();
    }

    private UpdaterEngine Engine(long build, byte[]? package = null, string? shaOverride = null)
    {
        package ??= Package(build);
        string sha = shaOverride ?? Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var assets = new Dictionary<string, byte[]>
        {
            ["version.json"] = Encoding.UTF8.GetBytes(VersionJson(build)),
            ["spherenet-win-x64.zip"] = package,
            ["spherenet-win-x64.zip.sha256"] = Encoding.ASCII.GetBytes($"{sha}  spherenet-win-x64.zip"),
        };
        return new UpdaterEngine(_dir, new UpdaterSettings(), _ => { }, new FakeRelease(assets));
    }

    private async Task<ApplyResult> Install(UpdaterEngine engine)
    {
        string staged = await engine.DownloadAndStageAsync(null, CancellationToken.None);
        var result = engine.ApplyStaged(staged);
        engine.CleanupStaging();
        return result;
    }

    [Fact]
    public async Task AFreshFolderGetsTheWholeInstallAndItsConfig()
    {
        var engine = Engine(10);
        Assert.Null(engine.ReadLocalVersion());
        Assert.True(UpdaterEngine.IsNewer(await engine.FetchRemoteVersionAsync(CancellationToken.None), null));

        var result = await Install(engine);

        Assert.True(File.Exists(Path.Combine(_dir, "SphereNet.Host.exe")));
        Assert.True(File.Exists(Path.Combine(_dir, "panel", "index.html")));
        Assert.Equal(10, engine.ReadLocalVersion()!.BuildNumber);
        Assert.Contains(Path.Combine("config", "sphere.ini"), result.DefaultsAdded);
        Assert.Contains("Template", File.ReadAllText(Path.Combine(_dir, "config", "sphere.ini")));
        Assert.True(File.Exists(Path.Combine(_dir, "config", "sphereCrypt.ini")));
        Assert.False(Directory.Exists(Path.Combine(_dir, ".update", "staged")));
    }

    [Fact]
    public async Task AnExistingSphereIniIsNeverTouched()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        File.WriteAllText(Path.Combine(_dir, "config", "sphere.ini"), "USER SETTINGS");

        var result = await Install(Engine(11));

        Assert.Equal("USER SETTINGS", File.ReadAllText(Path.Combine(_dir, "config", "sphere.ini")));
        Assert.DoesNotContain(Path.Combine("config", "sphere.ini"), result.DefaultsAdded);
        Assert.Contains(Path.Combine("config", "sphereCrypt.ini"), result.DefaultsAdded); // the missing one only
    }

    [Fact]
    public async Task ARootSphereIniIsNotShadowedByANewConfigOne()
    {
        // Host reads config\sphere.ini before the root one; creating it would hide the user's.
        File.WriteAllText(Path.Combine(_dir, "sphere.ini"), "ROOT USER SETTINGS");

        await Install(Engine(12));

        Assert.False(File.Exists(Path.Combine(_dir, "config", "sphere.ini")));
        Assert.Equal("ROOT USER SETTINGS", File.ReadAllText(Path.Combine(_dir, "sphere.ini")));
    }

    [Fact]
    public async Task UserDataFoldersSurviveAndThePanelIsRefreshed()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "save"));
        File.WriteAllText(Path.Combine(_dir, "save", "world.scp"), "WORLD");
        Directory.CreateDirectory(Path.Combine(_dir, "panel", "assets"));
        File.WriteAllText(Path.Combine(_dir, "panel", "assets", "stale-old.js"), "old");

        await Install(Engine(13));

        Assert.Equal("WORLD", File.ReadAllText(Path.Combine(_dir, "save", "world.scp")));
        Assert.False(File.Exists(Path.Combine(_dir, "panel", "assets", "stale-old.js")));
        Assert.True(File.Exists(Path.Combine(_dir, "panel", "index.html")));
    }

    [Fact]
    public async Task AChecksumMismatchAppliesNothing()
    {
        var engine = Engine(14, shaOverride: new string('0', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.DownloadAndStageAsync(null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "SphereNet.Host.exe")));
    }

    [Fact]
    public async Task AFailedCopyRollsBackWhatAlreadyChanged()
    {
        await Install(Engine(20, Package(20, hostBody: "v20")));

        var engine = Engine(21, Package(21, hostBody: "v21"));
        string staged = await engine.DownloadAndStageAsync(null, CancellationToken.None);
        // version.json sorts after SphereNet.Host.exe: hold it so the copy fails midway.
        using (new FileStream(Path.Combine(_dir, "version.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => engine.ApplyStaged(staged));

        Assert.Equal("v20", File.ReadAllText(Path.Combine(_dir, "SphereNet.Host.exe")));
        Assert.Equal(20, engine.ReadLocalVersion()!.BuildNumber);
    }

    [Fact]
    public void SettingsComeFromSphereIni()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        File.WriteAllText(Path.Combine(_dir, "config", "sphere.ini"),
            "[SPHERE]\n// comment\nAPPUPDATEREPO=me/fork // mine\nAPPUPDATECHANNEL=beta\nAPPUPDATETOKEN=\n");

        var s = UpdaterEngine.ReadSettings(_dir);

        Assert.Equal("me/fork", s.Repo);
        Assert.Equal("beta", s.Channel);
        Assert.Equal("win-x64", s.Runtime);
        Assert.Null(s.Token);
        Assert.Equal("https://github.com/me/fork/releases/download/beta/version.json", s.AssetUrl("version.json"));
    }

    [Fact]
    public async Task ASourceBuildIsRecognised()
    {
        File.WriteAllText(Path.Combine(_dir, "SphereNet.Host.exe"), "dev");
        var engine = Engine(30);
        Assert.True(engine.LooksLikeDevBuild());
        await Install(engine);
        Assert.False(engine.LooksLikeDevBuild());
    }
}
