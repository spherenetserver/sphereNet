using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Panel;
using SphereNet.Panel.Updates;

namespace SphereNet.Tests;

/// <summary>
/// Panelin "check for update" akisinin ag'a dokunmayan kisimlari: surum
/// karsilastirma kurali, version.json okuma ve uygulama kapilari.
/// </summary>
public sealed class PanelUpdateServiceTests : IDisposable
{
    private readonly string _installDir;

    public PanelUpdateServiceTests()
    {
        _installDir = Path.Combine(Path.GetTempPath(), "snet-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_installDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_installDir, recursive: true); }
        catch (IOException) { /* temp artigi — testi kirmasin */ }
    }

    private static readonly UpdateSettings Settings = new(
        Repo: "spherenetserver/sphereNet",
        Channel: "nightly",
        Runtime: "win-x64",
        CheckMinutes: 15);

    private static BuildVersion Version(long buildNumber, string sha = "abcdef1234567890") =>
        new(
            Sha: sha,
            ShortSha: sha[..7],
            Branch: "main",
            BuildNumber: buildNumber,
            BuiltAt: new DateTime(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc),
            Runtime: "win-x64",
            CommitSubject: "test commit");

    /// <summary>
    /// CI'nin (release.yml) urettigi version.json'in birebir sekli. Bu test
    /// kirilirsa workflow ile panel arasindaki sozlesme bozulmus demektir.
    /// </summary>
    private const string CiVersionJson = """
        {
          "sha": "9f3c2a1b8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a",
          "shortSha": "9f3c2a1",
          "branch": "main",
          "buildNumber": 42,
          "builtAt": "2026-07-16T10:00:00.0000000Z",
          "runtime": "win-x64",
          "commitSubject": "Loot template expansion"
        }
        """;

    private UpdateService CreateService(PanelContext ctx) =>
        new(ctx, Settings, NullLogger.Instance, _installDir);

    /// <summary>Host modu: guncelleme uygulanabilir.</summary>
    private static PanelContext HostContext() => new()
    {
        OnHostExit = () => true,
        OnSave = () => true,
    };

    private void WriteVersionJson(string json) =>
        File.WriteAllText(Path.Combine(_installDir, "version.json"), json);

    // ── Surum karsilastirma ──────────────────────────────────────────────────

    [Fact]
    public void IsNewer_TrueOnlyWhenRemoteBuildNumberIsHigher()
    {
        Assert.True(UpdateService.IsNewer(Version(43), Version(42)));
        Assert.False(UpdateService.IsNewer(Version(42), Version(42)));
    }

    [Fact]
    public void IsNewer_FalseWhenRemoteIsOlder()
    {
        // Bir release geri alinirsa (rollback) panel "guncelleme var" dememeli;
        // yoksa surekli eskiye donen bir dongu olusur.
        Assert.False(UpdateService.IsNewer(Version(41), Version(42)));
    }

    [Fact]
    public void IsNewer_IgnoresShaWhenBuildNumbersMatch()
    {
        // SHA'lar siralanamaz; karsilastirma yalnizca build numarasina dayanir.
        Assert.False(UpdateService.IsNewer(
            Version(42, "1111111aaaaaaaaa"),
            Version(42, "2222222bbbbbbbbb")));
    }

    [Fact]
    public void IsNewer_FalseWhenEitherSideUnknown()
    {
        Assert.False(UpdateService.IsNewer(null, Version(42)));
        Assert.False(UpdateService.IsNewer(Version(42), null));
        Assert.False(UpdateService.IsNewer(null, null));
    }

    // ── version.json okuma ───────────────────────────────────────────────────

    [Fact]
    public void ReadsCiProducedVersionJson()
    {
        WriteVersionJson(CiVersionJson);

        using var svc = CreateService(HostContext());
        var status = svc.GetStatus();

        Assert.False(status.IsDevBuild);
        Assert.NotNull(status.Current);
        Assert.Equal("9f3c2a1", status.Current!.ShortSha);
        Assert.Equal(42, status.Current.BuildNumber);
        Assert.Equal("main", status.Current.Branch);
        Assert.Equal("win-x64", status.Current.Runtime);
        Assert.Equal("Loot template expansion", status.Current.CommitSubject);
    }

    [Fact]
    public void MissingVersionJson_IsTreatedAsDevBuild()
    {
        using var svc = CreateService(HostContext());
        var status = svc.GetStatus();

        Assert.True(status.IsDevBuild);
        Assert.Null(status.Current);
        Assert.False(status.CanApply);
    }

    [Fact]
    public void CorruptVersionJson_DegradesToDevBuildInsteadOfThrowing()
    {
        // Yarim yazilmis bir version.json paneli tamamen dusurmemeli.
        WriteVersionJson("{ not json at all");

        using var svc = CreateService(HostContext());
        var status = svc.GetStatus();

        Assert.True(status.IsDevBuild);
        Assert.Null(status.Current);
    }

    // ── Uygulama kapilari ────────────────────────────────────────────────────

    [Fact]
    public void HostModeWithStampedBuild_CanApply()
    {
        WriteVersionJson(CiVersionJson);

        using var svc = CreateService(HostContext());

        Assert.True(svc.GetStatus().CanApply);
    }

    [Fact]
    public void StandaloneMode_CannotApply()
    {
        WriteVersionJson(CiVersionJson);

        // OnHostExit yok = Host'suz (standalone) mod: dosyalari takas edip
        // sureci yeniden baslatacak bir ust katman yok.
        using var svc = CreateService(new PanelContext());

        Assert.False(svc.GetStatus().CanApply);
        Assert.False(svc.TryBeginApply(out var error));
        Assert.Contains("SphereNet.Host", error);
    }

    [Fact]
    public void DevBuild_RefusesToApply()
    {
        // version.json yok. Guncellemeyi uygulamak, gelistiricinin kendi
        // derledigi binary'yi sessizce ezerdi.
        using var svc = CreateService(HostContext());

        Assert.False(svc.TryBeginApply(out var error));
        Assert.Contains("update.cmd", error);
    }

    // ── Updater scripti ──────────────────────────────────────────────────────

    /// <summary>
    /// Updater scripti ASCII disi karakter ICERMEMELI.
    ///
    /// Script diske .ps1 olarak yazilip powershell.exe (Windows PowerShell 5.1)
    /// ile calistiriliyor. 5.1, BOM'suz bir .ps1'i UTF-8 degil ANSI varsayar:
    /// tek bir em-dash bile "â€"" olur ve icindeki tirnak scripti parse
    /// edilemez hale getirir. Bu tam da Host kapanmisken olur, yani sunucu bir
    /// daha acilmaz. UpdateService BOM ile yazarak bunu ayrica onluyor; bu test
    /// ikinci katmani (saf ASCII) korur.
    /// </summary>
    [Fact]
    public void UpdaterScript_IsPureAscii()
    {
        var offenders = UpdaterScript.PowerShell
            .Split('\n')
            .Select((line, i) => (Line: line, Number: i + 1))
            .Where(x => x.Line.Any(c => c > 127))
            .Select(x => $"satir {x.Number}: {x.Line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Updater scriptinde ASCII disi karakter var; PowerShell 5.1 bunu bozar:\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void FreshService_ReportsNoUpdateBeforeAnyCheck()
    {
        WriteVersionJson(CiVersionJson);

        using var svc = CreateService(HostContext());
        var status = svc.GetStatus();

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.Latest);
        Assert.Null(status.LastCheckedUtc);
        Assert.False(status.Busy);
        Assert.Equal("Idle", status.State);
        Assert.Equal("spherenetserver/sphereNet", status.Repo);
        Assert.Equal("nightly", status.Channel);
        Assert.Equal("win-x64", status.Runtime);
    }
}
