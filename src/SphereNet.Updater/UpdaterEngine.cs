using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SphereNet.Updater;

/// <summary>Bir build'in kimligi - CI'in (release.yml) yazdigi version.json.
/// Panelin BuildVersion'i ile ayni sekil; karsilastirma BuildNumber'a dayanir.</summary>
internal sealed record BuildVersion(
    string Sha,
    string ShortSha,
    string Branch,
    long BuildNumber,
    DateTime BuiltAt,
    string Runtime,
    string CommitSubject);

/// <summary>Guncelleme kaynagi: sphere.ini APPUPDATE* anahtarlari, komut satiri ezer.</summary>
internal sealed record UpdaterSettings(
    string Repo = "spherenetserver/sphereNet",
    string Channel = "nightly",
    string Runtime = "win-x64",
    string? Token = null)
{
    public string PackageName => $"spherenet-{Runtime}.zip";

    public string AssetUrl(string name) =>
        $"https://github.com/{Repo}/releases/download/{Channel}/{name}";
}

/// <summary>Bir uygulamanin sonucu.</summary>
internal sealed record ApplyResult(int FilesCopied, IReadOnlyList<string> FoldersReplaced,
    IReadOnlyList<string> DefaultsAdded);

/// <summary>
/// Updater'in cekirdegi: CI'in her main commit'inde yayinladigi nightly release
/// paketini (build.ps1 ciktisinin aynisi) indirir, dogrular, kurulum klasorune
/// uygular ve eksik config dosyalarini sablonlardan tamamlar.
///
/// Kurallar:
///  - Paket yalnizca binari ve panel tasir; config\, save\, scripts\, logs\,
///    accounts\ hicbir zaman ezilmez (pakette olsalar bile atlanir).
///  - Config sablonlari paketin defaults\config\ klasorunde gelir ve SADECE
///    kurulumda karsiligi yoksa kopyalanir - var olan sphere.ini'ye dokunulmaz.
///    Host once config\sphere.ini'ye sonra koktaki sphere.ini'ye baktigi icin iki
///    konum da "var" sayilir; aksi halde yeni dosya kullanicininkini golgelerdi.
///  - Degistirilen her sey once .update\backup'a alinir; bir hata olursa geri yuklenir.
/// </summary>
internal sealed class UpdaterEngine
{
    public const string StagingDirName = ".update";
    public const string DefaultsDirName = "defaults";
    public const string UpdaterExeBaseName = "SphereNet.Updater";

    /// <summary>Kullanici verisi: paket ne tasirsa tasisin bunlara dokunulmaz.</summary>
    private static readonly HashSet<string> ProtectedEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "save", "scripts", "logs", "accounts", StagingDirName,
    };

    private const long MaxPackageBytes = 1024L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Action<string> _log;

    public string InstallDir { get; }
    public UpdaterSettings Settings { get; }

    public UpdaterEngine(string installDir, UpdaterSettings settings, Action<string> log,
        HttpMessageHandler? handler = null)
    {
        // Sondaki ayraci at: AppContext.BaseDirectory "C:\sphereNet\" dondurur ve
        // yol karsilastirmasi "C:\sphereNet\\" ile yapilinca calisan sunucu hic
        // bulunmuyordu - guncelleme acik sunucunun dosyalarini silmeye girisiyordu.
        InstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        Settings = settings;
        _log = log;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SphereNet-Updater", "1.0"));
        if (!string.IsNullOrWhiteSpace(settings.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
    }

    private string StageRoot => Path.Combine(InstallDir, StagingDirName);
    private string StagedDir => Path.Combine(StageRoot, "staged");
    private string BackupDir => Path.Combine(StageRoot, "backup");

    // ------------------------------------------------------------ settings

    /// <summary>sphere.ini (config\ ya da kok) icindeki APPUPDATE* anahtarlari.</summary>
    public static UpdaterSettings ReadSettings(string installDir)
    {
        var s = new UpdaterSettings();
        string? ini = FindIni(installDir, "sphere.ini");
        if (ini == null)
            return s;
        string? repo = null, channel = null, runtime = null, token = null;
        foreach (string raw in File.ReadLines(ini))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            int eq = line.IndexOf('=');
            if (eq < 1) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..];
            int comment = val.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) val = val[..comment];
            val = val.Trim();
            if (val.Length == 0) continue;
            if (key.Equals("APPUPDATEREPO", StringComparison.OrdinalIgnoreCase)) repo = val;
            else if (key.Equals("APPUPDATECHANNEL", StringComparison.OrdinalIgnoreCase)) channel = val;
            else if (key.Equals("APPUPDATERUNTIME", StringComparison.OrdinalIgnoreCase)) runtime = val;
            else if (key.Equals("APPUPDATETOKEN", StringComparison.OrdinalIgnoreCase)) token = val;
        }
        return new UpdaterSettings(repo ?? s.Repo, channel ?? s.Channel, runtime ?? s.Runtime, token);
    }

    /// <summary>Host'un aradigi sirayla: config\&lt;ad&gt;, sonra kokte &lt;ad&gt;.</summary>
    public static string? FindIni(string installDir, string name)
    {
        foreach (string p in new[] { Path.Combine(installDir, "config", name), Path.Combine(installDir, name) })
            if (File.Exists(p)) return p;
        return null;
    }

    // ------------------------------------------------------------- version

    /// <summary>Kurulumun version.json'i. Yoksa null.</summary>
    public BuildVersion? ReadLocalVersion()
    {
        string path = Path.Combine(InstallDir, "version.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<BuildVersion>(File.ReadAllText(path), JsonOpts); }
        catch (JsonException) { return null; }
    }

    /// <summary>Kurulumda SphereNet binari'leri var ama version.json yok: kaynaktan
    /// derlenmis bir klasor. Bunun ustune paket yazmak icin --force gerekir.</summary>
    public bool LooksLikeDevBuild() =>
        ReadLocalVersion() == null &&
        (File.Exists(Path.Combine(InstallDir, "SphereNet.Host.exe")) ||
         File.Exists(Path.Combine(InstallDir, "SphereNet.Server.exe")) ||
         File.Exists(Path.Combine(InstallDir, "SphereNet.Host")) ||
         File.Exists(Path.Combine(InstallDir, "SphereNet.Server")));

    public static bool IsNewer(BuildVersion? latest, BuildVersion? current) =>
        latest != null && (current == null || latest.BuildNumber > current.BuildNumber);

    public async Task<BuildVersion> FetchRemoteVersionAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(Settings.AssetUrl("version.json"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"'{Settings.Repo}' deposunun '{Settings.Channel}' release'inde version.json yok.");
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<BuildVersion>(json, JsonOpts)
            ?? throw new InvalidOperationException("Release'teki version.json bos ya da gecersiz.");
    }

    // ------------------------------------------------------------ download

    /// <summary>Paketi indirir, .sha256 ile dogrular ve .update\staged'a acar.</summary>
    public async Task<string> DownloadAndStageAsync(Action<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(StageRoot);
        string zipPath = Path.Combine(StageRoot, Settings.PackageName);

        string expected;
        using (var resp = await _http.GetAsync(Settings.AssetUrl(Settings.PackageName + ".sha256"), ct))
        {
            resp.EnsureSuccessStatusCode();
            string text = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            expected = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (expected.Length != 64)
                throw new InvalidOperationException("Release'teki .sha256 asset'i okunamadi ya da bozuk.");
        }

        using (var resp = await _http.GetAsync(Settings.AssetUrl(Settings.PackageName),
                   HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            if (total > MaxPackageBytes)
                throw new InvalidOperationException("Paket beklenmedik kadar buyuk; indirme durduruldu.");
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[1 << 16];
            long read = 0;
            int lastPct = -1;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (read > MaxPackageBytes)
                    throw new InvalidOperationException("Paket beklenmedik kadar buyuk; indirme durduruldu.");
                if (total > 0)
                {
                    int pct = (int)(read * 100 / total.Value);
                    if (pct != lastPct) { lastPct = pct; progress?.Invoke(pct); }
                }
            }
        }

        string actual;
        await using (var fs = File.OpenRead(zipPath))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SHA256 uyusmuyor (beklenen {expected}, inen {actual}).");

        if (Directory.Exists(StagedDir))
            Directory.Delete(StagedDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, StagedDir);
        File.Delete(zipPath);
        return StagedDir;
    }

    // --------------------------------------------------------------- apply

    /// <summary>Acilmis paketi kuruluma uygular. Bir hata olursa yedegi geri yukler
    /// ve hatayi yeniden firlatir.</summary>
    public ApplyResult ApplyStaged(string stagedDir)
    {
        if (!Directory.Exists(stagedDir))
            throw new DirectoryNotFoundException($"Paket klasoru yok: {stagedDir}");

        if (Directory.Exists(BackupDir))
            Directory.Delete(BackupDir, recursive: true);
        Directory.CreateDirectory(BackupDir);

        var entries = new DirectoryInfo(stagedDir).GetFileSystemInfos()
            .Where(e => !ProtectedEntries.Contains(e.Name))
            .ToList();

        var backedUp = new List<string>();
        int files = 0;
        var folders = new List<string>();
        try
        {
            foreach (var e in entries)
            {
                string dst = Path.Combine(InstallDir, e.Name);
                if (Backup(dst, Path.Combine(BackupDir, e.Name)))
                    backedUp.Add(e.Name);

                if (e is DirectoryInfo dir)
                {
                    // Klasorler (panel\, defaults\) tamamen tazelenir: eski hash'li
                    // bundle dosyalari kalirsa index.html olmayan asset'e isaret eder.
                    ReplaceDirectory(dir.FullName, dst);
                    folders.Add(e.Name);
                }
                else
                {
                    ReplaceFile(e.FullName, dst);
                    files++;
                }
            }
        }
        catch (Exception ex)
        {
            ReportAccessProblem(ex);
            Rollback(backedUp);
            throw;
        }

        var added = EnsureDefaults();
        return new ApplyResult(files, folders, added);
    }

    /// <summary>defaults\config\ altindaki her sablonu, kurulumda karsiligi yoksa
    /// config\'e kopyalar. Var olan hicbir dosyaya dokunmaz.</summary>
    public IReadOnlyList<string> EnsureDefaults()
    {
        var added = new List<string>();
        string templates = Path.Combine(InstallDir, DefaultsDirName, "config");
        if (!Directory.Exists(templates))
            return added;
        string configDir = Path.Combine(InstallDir, "config");
        foreach (string template in Directory.GetFiles(templates))
        {
            string name = Path.GetFileName(template);
            if (FindIni(InstallDir, name) != null)
                continue;
            Directory.CreateDirectory(configDir);
            File.Copy(template, Path.Combine(configDir, name));
            added.Add(Path.Combine("config", name));
        }
        return added;
    }

    /// <summary>Calisan updater EXE'si kendisinin uzerine yazamaz ama Windows calisan
    /// bir EXE'nin ADINI degistirmeye izin verir: eskisi .old olur, yenisi yerine gecer.</summary>
    private static void ReplaceFile(string src, string dst)
    {
        string? self = Environment.ProcessPath;
        if (self != null && string.Equals(Path.GetFullPath(self), Path.GetFullPath(dst),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            string old = dst + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(dst, old);
        }
        File.Copy(src, dst, overwrite: true);
    }

    private static bool Backup(string src, string dst)
    {
        if (File.Exists(src))
        {
            File.Copy(src, dst, overwrite: true);
            return true;
        }
        if (Directory.Exists(src))
        {
            CopyDirectory(src, dst);
            return true;
        }
        return false;
    }

    private void Rollback(IEnumerable<string> names)
    {
        _log("HATA: yedek geri yukleniyor...");
        foreach (string name in names)
        {
            try
            {
                string src = Path.Combine(BackupDir, name);
                string dst = Path.Combine(InstallDir, name);
                if (Directory.Exists(src))
                {
                    if (Directory.Exists(dst)) DeleteDirectory(dst);
                    CopyDirectory(src, dst);
                }
                else if (File.Exists(src))
                {
                    File.Copy(src, dst, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _log($"  geri yuklenemedi: {name} ({ex.Message}) - yedek: {BackupDir}");
            }
        }
    }

    /// <summary>Klasoru siler; salt-okunur dosyalarin ozniteligini kaldirir ve
    /// kisa sureli kilitler (antivirus taramasi, Explorer onizlemesi) icin birkac
    /// kez yeniden dener.</summary>
    internal static void DeleteDirectory(string path, int attempts = 5, int delayMs = 500)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                ClearReadOnly(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (i < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>Salt-okunur ozniteligi dosyalardan VE klasorlerden kaldirir:
    /// salt-okunur bir klasor de Directory.Delete'te "Access denied" verir.</summary>
    private static void ClearReadOnly(string path)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                     .Append(path))
        {
            try
            {
                var attr = File.GetAttributes(entry);
                if ((attr & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry, attr & ~FileAttributes.ReadOnly);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Klasoru paketteki haliyle degistirir. Once tamamen silip yeniden
    /// kopyalar (eski hash'li dosyalar kalmasin); silinemezse - bir dosyayi baska bir
    /// surec tutuyor olabilir - paketteki dosyalari mevcutlarin uzerine yazar ve
    /// paketin tasimadigi eski dosyalari silmeyi dener. Artik kalan eski bir dosya
    /// zararsizdir; yazilamayan bir dosya ise gercek hatadir ve firlatilir.</summary>
    internal void ReplaceDirectory(string src, string dst)
    {
        if (Directory.Exists(dst))
        {
            try
            {
                DeleteDirectory(dst);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log($"  {Path.GetFileName(dst)} klasoru silinemedi ({ex.Message}); dosyalar uzerine yaziliyor.");
                OverlayDirectory(src, dst);
                return;
            }
        }
        CopyDirectory(src, dst);
    }

    private void OverlayDirectory(string src, string dst)
    {
        ClearReadOnly(dst);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            wanted.Add(Path.GetFullPath(target));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        int left = 0;
        foreach (string stale in Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).ToList())
        {
            if (wanted.Contains(Path.GetFullPath(stale))) continue;
            try { File.Delete(stale); }
            catch (IOException) { left++; }
            catch (UnauthorizedAccessException) { left++; }
        }
        if (left > 0)
            _log($"  {left} eski dosya silinemedi (kullanimda); zararsiz, sonraki guncellemede temizlenir.");
    }

    /// <summary>Bir dosya ya da klasor yazilamadiginda sebebini operatore soyler:
    /// hangi surec tutuyor (Restart Manager) ve yonetici izni gerekip gerekmedigi.</summary>
    private void ReportAccessProblem(Exception ex)
    {
        if (ex is not (IOException or UnauthorizedAccessException))
            return;
        var candidates = new List<string>();
        foreach (string dir in new[] { "panel", "defaults" })
        {
            string d = Path.Combine(InstallDir, dir);
            if (Directory.Exists(d))
                candidates.AddRange(Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Take(48));
        }
        candidates.AddRange(Directory.EnumerateFiles(InstallDir, "*.dll").Take(8));
        candidates.AddRange(Directory.EnumerateFiles(InstallDir, "*.exe"));
        var holders = FileLockInfo.WhoIsLocking(candidates);
        if (holders.Count > 0)
            _log("  Dosyalari tutan surec(ler): " + string.Join(", ", holders) +
                 " - bunlari kapatip tekrar deneyin.");
        else if (ex is UnauthorizedAccessException && !FileLockInfo.IsElevated())
            _log("  Dosyayi tutan bir surec bulunamadi. Kurulum klasorune yazma izni yok olabilir: " +
                 "updater'i 'Yonetici olarak calistir' ile deneyin.");
        else
            _log("  Dosyayi tutan bir surec bulunamadi; klasorun Explorer'da ya da antivirus " +
                 "taramasinda acik olmadigindan emin olun.");
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (string dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    /// <summary>Paket ve acilmis kopyayi siler; son yedek bir sonraki guncellemeye kadar kalir.</summary>
    public void CleanupStaging()
    {
        try
        {
            if (Directory.Exists(StagedDir)) Directory.Delete(StagedDir, recursive: true);
            string zip = Path.Combine(StageRoot, Settings.PackageName);
            if (File.Exists(zip)) File.Delete(zip);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Bir onceki self-update'ten kalan .old EXE.</summary>
    public static void DeleteLeftoverSelf()
    {
        string? self = Environment.ProcessPath;
        if (self == null) return;
        try { if (File.Exists(self + ".old")) File.Delete(self + ".old"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ----------------------------------------------------------- processes

    /// <summary>Bu kurulumdan calisan Host/Server surecleri. Baska bir klasordeki
    /// SphereNet kurulumuna dokunulmaz.</summary>
    public List<Process> FindRunningServer()
    {
        var found = new List<Process>();
        foreach (string name in new[] { "SphereNet.Host", "SphereNet.Server" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                // Yolu okunamayan surec (baska kullanici / yonetici olarak calisan,
                // orn. VDS'te servis) bu kurulumun olabilir: atlamak, acik sunucunun
                // dosyalarini silmek demek. Bekle; gerekirse --kill.
                if (path == null || IsUnderDir(path, InstallDir))
                    found.Add(p);
                else
                    p.Dispose();
            }
        }
        return found;
    }

    /// <summary>Yol, klasorun icinde mi (klasor siniriyla: C:\srv\sphere,
    /// C:\srv\sphere2'yi kapsamaz). Sondaki ayraclar onemsizdir.</summary>
    internal static bool IsUnderDir(string path, string dir)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
