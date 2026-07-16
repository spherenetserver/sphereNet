using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Security;

namespace SphereNet.Panel.Updates;

/// <summary>
/// "Check for update" arka plani: GitHub release asset'lerinden surum okur,
/// paketi indirip dogrular ve calisan kurulumun uzerine uygulanmasi icin bir
/// updater sureci baslatir.
///
/// NEDEN GITHUB API DEGIL: anonim REST API'si IP basina saatte 60 istekle
/// sinirli ve artifact indirmek zaten token ister. Release asset'lerinin sabit
/// indirme URL'leri (releases/download/&lt;tag&gt;/&lt;ad&gt;) ise CDN uzerinden,
/// tokensiz ve limitsiz servis edilir. Bu yuzden tum akis o URL'ler uzerinden
/// yurur; token yalnizca depo private yapilirsa gerekir.
///
/// NEDEN AYRI BIR SUREC: paket SphereNet.Host.exe'yi de icerir, ama calisan
/// Host kendi EXE'sini kilitler ve uzerine yazamaz. Bu yuzden dosya takasini
/// Host'un disindaki kisa omurlu bir PowerShell sureci yapar: Host'un
/// cikmasini bekler, takasi yapar, Host'u yeniden baslatir.
/// </summary>
public sealed class UpdateService : IDisposable
{
    // CI camelCase yaziyor; Web defaults hem camelCase eslemesini hem de
    // buyuk/kucuk harf duyarsizligini getirir.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Paket + updater icin calisma alani. Kopyalama sirasinda haric tutulur.</summary>
    private const string StagingDirName = ".update";

    private const long MaxPackageBytes = 1024L * 1024 * 1024; // 1 GB — bozuk/dev asset'e karsi tavan

    private readonly UpdateSettings _settings;
    private readonly PanelContext _ctx;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly string _installDir;

    // Ayni anda tek bir indirme/uygulama akisi.
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    private readonly object _sync = new();
    private UpdateState _state = UpdateState.Idle;
    private int _progress;
    private string? _message;
    private DateTime? _lastChecked;
    private BuildVersion? _latest;
    private readonly BuildVersion? _current;
    private readonly bool _isDevBuild;

    /// <param name="installDir">
    /// Guncellenecek kurulumun koku. Varsayilan: calisan EXE'nin klasoru.
    /// Yalnizca testler override eder — gercek kurulumda her zaman kendi
    /// klasorumuzdur.
    /// </param>
    public UpdateService(PanelContext ctx, UpdateSettings settings, ILogger logger,
        string? installDir = null)
    {
        _ctx = ctx;
        _settings = settings;
        _logger = logger;

        // Single-file publish'te de AppContext.BaseDirectory EXE'nin klasorunu
        // verir (extraction dizinini degil) — kurulum kokumuz bu.
        _installDir = installDir ?? AppContext.BaseDirectory;

        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // Release indirmeleri objects.githubusercontent.com'a yonlenir.
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SphereNet-Panel-Updater/1.0");
        if (!string.IsNullOrWhiteSpace(_settings.Token))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _settings.Token);
        }

        _current = ReadCurrentVersion();
        _isDevBuild = _current is null;

        if (_isDevBuild)
        {
            _logger.LogInformation(
                "Update: version.json yok ({Dir}) — bu kurulum kaynaktan derlenmis sayiliyor, " +
                "otomatik guncelleme devre disi.", _installDir);
        }
        else
        {
            _logger.LogInformation("Update: mevcut surum {Short} (build #{Build}, {Branch})",
                _current!.ShortSha, _current.BuildNumber, _current.Branch);
        }
    }

    /// <summary>Kurulum kokundeki version.json. Yoksa null = dev build.</summary>
    private BuildVersion? ReadCurrentVersion()
    {
        var path = Path.Combine(_installDir, "version.json");
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<BuildVersion>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Update: version.json okunamadi — dev build sayiliyor.");
            return null;
        }
    }

    private string AssetUrl(string fileName) =>
        $"https://github.com/{_settings.Repo}/releases/download/{_settings.Channel}/{fileName}";

    private string PackageName => $"spherenet-{_settings.Runtime}.zip";

    /// <summary>
    /// Uygulama yalnizca Host modunda mumkun: Server.exe'nin icine gomulu
    /// panelde (standalone) sureci durdurup yeniden baslatacak bir ust katman
    /// yok.
    /// </summary>
    private bool CanApply => _ctx.OnHostExit is not null && !_isDevBuild;

    public UpdateStatus GetStatus()
    {
        lock (_sync)
        {
            return new UpdateStatus(
                Current: _current,
                Latest: _latest,
                UpdateAvailable: IsNewer(_latest, _current),
                IsDevBuild: _isDevBuild,
                State: _state.ToString(),
                ProgressPercent: _progress,
                Message: _message,
                LastCheckedUtc: _lastChecked,
                CanApply: CanApply,
                Busy: _state is UpdateState.Checking or UpdateState.Downloading
                    or UpdateState.Verifying or UpdateState.Extracting
                    or UpdateState.Staged or UpdateState.Applying,
                Repo: _settings.Repo,
                Channel: _settings.Channel,
                Runtime: _settings.Runtime);
        }
    }

    /// <summary>
    /// Uzaktaki build numarasi yereldekinden buyukse guncelleme var. SHA'lar
    /// siralanamadigi icin karsilastirma buna dayanir; esitse ya da uzaktaki
    /// daha eskiyse (rollback edilmis bir release) guncelleme onerilmez.
    /// </summary>
    internal static bool IsNewer(BuildVersion? latest, BuildVersion? current)
    {
        if (latest is null || current is null)
            return false;
        return latest.BuildNumber > current.BuildNumber;
    }

    private void SetState(UpdateState state, string? message = null, int progress = 0)
    {
        lock (_sync)
        {
            _state = state;
            _message = message;
            _progress = progress;
        }
    }

    // ── Kontrol ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Release'teki version.json'i okur. Indirme/uygulama surerken durumu
    /// bozmamak icin no-op doner.
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            // Bir uygulama akisi surerken durum ONUNDUR. Arka plan kontrolu 15
            // dakikada bir calisir; tam indirmenin ortasina denk gelirse sonunda
            // state'i Idle'a cekip UI'da "mesgul degil" gosterirdi (cift tiklama
            // _applyGate tarafindan zaten engelleniyor, ama gorunum yaniltici).
            if (_applyGate.CurrentCount == 0)
                return GetStatus();

            if (_state is not (UpdateState.Idle or UpdateState.Failed))
                return GetStatus();
            _state = UpdateState.Checking;
            _message = null;
        }

        try
        {
            var latest = await FetchLatestVersionAsync(ct).ConfigureAwait(false);
            lock (_sync)
            {
                _latest = latest;
                _lastChecked = DateTime.UtcNow;
                _state = UpdateState.Idle;
                _message = IsNewer(latest, _current)
                    ? $"Yeni surum hazir: {latest.ShortSha} (build #{latest.BuildNumber})"
                    : "Kurulum guncel.";
            }
            return GetStatus();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Kontrol hatasi kurulumu etkilemez; kullaniciya sebebi gosterilir.
            _logger.LogWarning(ex, "Update: kontrol basarisiz.");
            SetState(UpdateState.Failed, $"Guncelleme kontrolu basarisiz: {ex.Message}");
            return GetStatus();
        }
    }

    private async Task<BuildVersion> FetchLatestVersionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, AssetUrl("version.json"));
        // Release asset'leri CDN'den geliyor; eski bir kopyayi "guncel" sanmayalim.
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HttpRequestException(
                $"'{_settings.Channel}' release'inde version.json yok. " +
                "Henuz hic build yayinlanmamis olabilir.");
        }
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BuildVersion>(json, JsonOpts)
            ?? throw new JsonException("version.json bos ya da gecersiz.");
    }

    // ── Uygulama ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Paketi indirir, dogrular, acar ve updater'i baslatir. Uzun surdugu icin
    /// cagiran hemen doner; ilerleme /api/update/status'tan izlenir.
    /// </summary>
    public bool TryBeginApply(out string error)
    {
        error = "";

        if (_isDevBuild)
        {
            error = "Bu kurulum kaynaktan derlenmis (version.json yok). " +
                    "Kaynak agacinda update.cmd kullan.";
            return false;
        }
        if (_ctx.OnHostExit is null)
        {
            error = "Guncelleme yalnizca SphereNet.Host uzerinden calisirken uygulanabilir.";
            return false;
        }
        if (!_applyGate.Wait(0))
        {
            error = "Bir guncelleme islemi zaten suruyor.";
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update: uygulama basarisiz.");
                SetState(UpdateState.Failed, $"Guncelleme basarisiz: {ex.Message}");
            }
            finally
            {
                _applyGate.Release();
            }
        });

        return true;
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        // Her zaman taze bir surum bilgisi ile calis — kullanici saatler once
        // bakmis olabilir.
        SetState(UpdateState.Checking, "Surum bilgisi aliniyor…");
        var latest = await FetchLatestVersionAsync(ct).ConfigureAwait(false);
        lock (_sync)
        {
            _latest = latest;
            _lastChecked = DateTime.UtcNow;
        }

        if (!IsNewer(latest, _current))
        {
            SetState(UpdateState.Idle, "Kurulum zaten guncel — yapacak bir sey yok.");
            return;
        }

        var stagingDir = Path.Combine(_installDir, StagingDirName);
        var stagedDir = Path.Combine(stagingDir, "staged");
        var zipPath = Path.Combine(stagingDir, PackageName);

        // Yarim kalmis onceki bir denemenin artiklari yeni paketle karismasin.
        CleanDirectory(stagedDir);
        Directory.CreateDirectory(stagingDir);

        SetState(UpdateState.Downloading, $"{PackageName} indiriliyor…");
        await DownloadPackageAsync(zipPath, ct).ConfigureAwait(false);

        SetState(UpdateState.Verifying, "SHA256 dogrulaniyor…");
        await VerifyChecksumAsync(zipPath, ct).ConfigureAwait(false);

        SetState(UpdateState.Extracting, "Paket aciliyor…");
        ExtractPackage(zipPath, stagedDir);
        ValidateStaged(stagedDir, latest);

        // Zip artik gereksiz; ~60 MB yer tutar.
        TryDelete(zipPath);

        SetState(UpdateState.Staged, "Paket hazir — sunucu kaydediliyor…");

        // Kapanista otomatik save YOK (bkz. update.ps1 notu), o yuzden dunyayi
        // acikca kaydet. Save basarisiz olsa bile guncellemeyi surdurmek
        // dogru degil: kaydedilmemis oyun durumu kaybolur.
        if (_ctx.OnSave is not null)
        {
            bool saved;
            try
            {
                saved = _ctx.OnSave();
            }
            catch (PanelBackendException ex)
            {
                throw new InvalidOperationException(
                    $"Guncelleme oncesi dunya kaydi basarisiz: {ex.Message}. Guncelleme iptal edildi.", ex);
            }
            if (!saved)
            {
                throw new InvalidOperationException(
                    "Guncelleme oncesi dunya kaydi reddedildi. Guncelleme iptal edildi.");
            }
            // Kaydin diske inmesi icin kisa bir pay.
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        var scriptPath = Path.Combine(stagingDir, "apply-update.ps1");
        // BOM ZORUNLU: Windows PowerShell 5.1 (powershell.exe) BOM'suz bir .ps1'i
        // UTF-8 degil ANSI varsayar. File.WriteAllText'in varsayilani BOM'suz
        // UTF-8'dir; ASCII disi tek bir karakter bile bozulur ve bozuk bayt
        // dizisi bir tirnak icerirse script parse edilemez — Host kapanmisken,
        // yani sunucu bir daha acilmaz. UpdaterScript ayrica saf ASCII tutulur.
        await File.WriteAllTextAsync(scriptPath, UpdaterScript.PowerShell,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct).ConfigureAwait(false);

        var hostExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Calisan Host EXE'sinin yolu belirlenemedi.");

        LaunchUpdater(scriptPath, stagedDir, hostExe);

        SetState(UpdateState.Applying,
            $"Guncelleme uygulaniyor ({latest.ShortSha}). Sunucu kapanip yeniden baslayacak — " +
            "panel birkac saniye icinde geri gelir.");

        // Updater'in Host'un cikmasini beklemeye baslamasi icin kisa bir pay,
        // sonra Host'u birak. Bu noktadan sonra surec sonlaniyor.
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        _ctx.OnHostExit!.Invoke();
    }

    private async Task DownloadPackageAsync(string zipPath, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, AssetUrl(PackageName));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

        using var resp = await _http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        if (total > MaxPackageBytes)
            throw new InvalidOperationException($"Paket beklenmedik sekilde buyuk ({total} bayt).");

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[128 * 1024];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;

            if (read > MaxPackageBytes)
                throw new InvalidOperationException("Paket boyut tavanini asti; indirme durduruldu.");

            if (total > 0)
            {
                var pct = (int)(read * 100 / total);
                SetState(UpdateState.Downloading,
                    $"Indiriliyor… {read / 1024 / 1024} / {total / 1024 / 1024} MB", pct);
            }
        }
    }

    private async Task VerifyChecksumAsync(string zipPath, CancellationToken ct)
    {
        string expected;
        using (var req = new HttpRequestMessage(HttpMethod.Get, AssetUrl(PackageName + ".sha256")))
        {
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // "<sha256>  <dosya>" — sha256sum formati; ilk alan yeter.
            expected = body.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.ToLowerInvariant() ?? "";
        }

        if (expected.Length != 64)
            throw new InvalidOperationException("Release'teki .sha256 asset'i okunamadi ya da bozuk.");

        string actual;
        await using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            actual = Convert.ToHexString(hash).ToLowerInvariant();
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(expected)))
        {
            TryDelete(zipPath);
            throw new InvalidOperationException(
                $"SHA256 uyusmadi (beklenen {expected[..12]}…, bulunan {actual[..12]}…). " +
                "Paket bozuk ya da indirme yarida kalmis; guncelleme iptal edildi.");
        }
    }

    /// <summary>
    /// Zip'i acar. Zip-slip'e karsi her giris kurulum kokune degil, staging
    /// klasorune gore SafePath ile cozulur.
    ///
    /// Giris adlarindaki ayirac onemsizdir: zip spec forward slash ister ve
    /// .NET Core'un ZipFile'i (CI'daki pwsh) oyle yazar, ama .NET Framework
    /// ters bolu yazar. Path.Combine ikisini de Windows'ta dogru cozer.
    /// </summary>
    internal static void ExtractPackage(string zipPath, string stagedDir)
    {
        Directory.CreateDirectory(stagedDir);
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (!SafePath.TryResolveUnderRoot(stagedDir, entry.FullName, out var target, out _))
                throw new InvalidOperationException(
                    $"Paket, hedef klasorun disina cikan bir giris iceriyor: '{entry.FullName}'.");

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Acilan paketin gercekten calisir bir kurulum oldugunu, dosyalari takas
    /// etmeden ONCE dogrular. Eksik bir paketi uygulamak sunucuyu baslamaz hale
    /// getirirdi.
    /// </summary>
    private void ValidateStaged(string stagedDir, BuildVersion expected)
    {
        var exeExt = _settings.Runtime.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? ".exe" : "";
        string[] required =
        [
            $"SphereNet.Host{exeExt}",
            $"SphereNet.Server{exeExt}",
            "version.json",
            Path.Combine("panel", "index.html"),
        ];

        foreach (var rel in required)
        {
            if (!File.Exists(Path.Combine(stagedDir, rel)))
                throw new InvalidOperationException(
                    $"Indirilen paket eksik: '{rel}' yok. Guncelleme iptal edildi.");
        }

        // Paketin icindeki version.json, release'in isaret ettigi surumle
        // ayni olmali. Uymuyorsa asset'ler tutarsiz yuklenmis demektir.
        BuildVersion? packaged = null;
        try
        {
            packaged = JsonSerializer.Deserialize<BuildVersion>(
                File.ReadAllText(Path.Combine(stagedDir, "version.json")), JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Paketin icindeki version.json okunamadi.", ex);
        }

        if (packaged is null || packaged.Sha != expected.Sha)
        {
            throw new InvalidOperationException(
                $"Paket surumu release ile uyusmuyor (paket {packaged?.ShortSha ?? "?"}, " +
                $"release {expected.ShortSha}). Yayin yarida kalmis olabilir; birazdan tekrar dene.");
        }
    }

    private void LaunchUpdater(string scriptPath, string stagedDir, string hostExe)
    {
        var logFile = Path.Combine(_installDir, "logs", "update.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Kurulum kokunu calisma klasoru YAPMA: updater'in cwd'si silinen/
            // degistirilen bir klasor olursa dosya kilidi olusur.
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-HostPid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("-TargetDir");
        psi.ArgumentList.Add(_installDir.TrimEnd(Path.DirectorySeparatorChar));
        psi.ArgumentList.Add("-StageDir");
        psi.ArgumentList.Add(stagedDir);
        psi.ArgumentList.Add("-HostExe");
        psi.ArgumentList.Add(hostExe);
        psi.ArgumentList.Add("-LogFile");
        psi.ArgumentList.Add(logFile);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Updater sureci baslatilamadi.");

        _logger.LogInformation(
            "Update: updater baslatildi (pid {Pid}); Host kapaniyor. Ayrinti: {Log}",
            proc.Id, logFile);
    }

    private static void CleanDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Onceki guncelleme artigi temizlenemedi: {dir}. Klasoru elle sil ve tekrar dene.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* bosluk artigi — zararsiz */ }
    }

    public void Dispose()
    {
        _http.Dispose();
        _applyGate.Dispose();
    }
}
