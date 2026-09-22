using System.Diagnostics;
using SphereNet.Updater;

// SphereNet.Updater - panelden bagimsiz, tek EXE guncelleyici / kurucu.
//
// CI her main commit'inde build.ps1 ile publish edip "nightly" release'ine
// spherenet-win-x64.zip yukler. Bu arac o paketi indirir, SHA256 ile dogrular
// ve bulundugu klasore (ya da --dir) uygular. Bos bir klasorde calistirilirsa
// sifirdan kurar; eksik config dosyalarini (sphere.ini dahil) sablonlardan
// tamamlar, var olanlara dokunmaz.

var opts = Options.Parse(args);
if (opts.Help)
{
    Options.PrintHelp();
    return 0;
}

int exit;
try
{
    exit = await RunAsync(opts);
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("Iptal edildi.");
    exit = 1;
}
catch (Exception ex)
{
    Console.WriteLine();
    WriteColor($"HATA: {ex.Message}", ConsoleColor.Red);
    exit = 1;
}

if (!opts.NoPause && !Console.IsInputRedirected)
{
    Console.WriteLine();
    Console.Write("Cikmak icin Enter'a basin...");
    Console.ReadLine();
}
return exit;

static async Task<int> RunAsync(Options opts)
{
    UpdaterEngine.DeleteLeftoverSelf();

    string installDir = Path.GetFullPath(opts.Dir ?? AppContext.BaseDirectory);
    Directory.CreateDirectory(installDir);

    var fromIni = UpdaterEngine.ReadSettings(installDir);
    var settings = fromIni with
    {
        Repo = opts.Repo ?? fromIni.Repo,
        Channel = opts.Channel ?? fromIni.Channel,
        Runtime = opts.Runtime ?? fromIni.Runtime,
        Token = opts.Token ?? fromIni.Token,
    };

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var engine = new UpdaterEngine(installDir, settings, Console.WriteLine);

    WriteColor("SphereNet Updater", ConsoleColor.Cyan);
    Console.WriteLine($"  Kurulum : {installDir}");
    Console.WriteLine($"  Kaynak  : github.com/{settings.Repo} ({settings.Channel}, {settings.Runtime})");
    Console.WriteLine();

    var local = engine.ReadLocalVersion();
    var remote = await engine.FetchRemoteVersionAsync(cts.Token);
    Console.WriteLine($"  Kurulu  : {Describe(local)}");
    Console.WriteLine($"  Son     : {Describe(remote)}");
    Console.WriteLine();

    bool newer = UpdaterEngine.IsNewer(remote, local);
    if (opts.CheckOnly)
    {
        WriteColor(newer ? "Yeni surum var." : "Kurulum guncel.", newer ? ConsoleColor.Yellow : ConsoleColor.Green);
        return 0;
    }

    if (!newer && !opts.Force)
    {
        ReportDefaults(engine.EnsureDefaults());
        WriteColor("Kurulum guncel.", ConsoleColor.Green);
        return 0;
    }

    if (engine.LooksLikeDevBuild() && !opts.Force)
    {
        // Cift tikla acilan pencerede parametre verilemez: etkilesimliyse sor.
        WriteColor("Bu klasorde version.json olmadan SphereNet binari'leri var " +
            "(kaynaktan derlenmis ya da elle kopyalanmis bir kurulum).", ConsoleColor.Yellow);
        bool interactive = !opts.NoPause && !Console.IsInputRedirected;
        if (!interactive)
            throw new InvalidOperationException(
                "Uzerine paket yazmak icin --force ile calistirin.");
        Console.Write("Binari'ler release paketiyle degistirilsin mi? config, save ve scripts'e dokunulmaz. (e/h): ");
        string answer = (Console.ReadLine() ?? "").Trim();
        if (!answer.StartsWith("e", StringComparison.OrdinalIgnoreCase) &&
            !answer.StartsWith("y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Vazgecildi.");
            return 0;
        }
    }

    // Sunucu acikken EXE'ler kilitlidir; habersiz kapatmak da kaydedilmemis dunyayi kaybettirir.
    bool wasRunning = await WaitForServerStopAsync(engine, opts.Kill, cts.Token);

    Console.WriteLine("Paket indiriliyor...");
    int lastShown = -10;
    string staged = await engine.DownloadAndStageAsync(pct =>
    {
        if (pct - lastShown >= 10 || pct == 100)
        {
            lastShown = pct;
            Console.WriteLine($"  %{pct}");
        }
    }, cts.Token);
    Console.WriteLine("  SHA256 dogrulandi, paket acildi.");

    // Indirme surerken biri sunucuyu acmis olabilir.
    await WaitForServerStopAsync(engine, opts.Kill, cts.Token);

    Console.WriteLine("Dosyalar uygulaniyor...");
    var result = engine.ApplyStaged(staged);
    engine.CleanupStaging();
    Console.WriteLine($"  {result.FilesCopied} dosya guncellendi" +
        (result.FoldersReplaced.Count > 0 ? $", klasorler: {string.Join(", ", result.FoldersReplaced)}" : ""));
    ReportDefaults(result.DefaultsAdded);
    WriteColor($"Guncelleme tamamlandi: {Describe(engine.ReadLocalVersion())}", ConsoleColor.Green);

    bool start = opts.Start ?? true;
    if (start)
        StartHost(installDir, wasRunning);
    return 0;
}

static async Task<bool> WaitForServerStopAsync(UpdaterEngine engine, bool kill, CancellationToken ct)
{
    var running = engine.FindRunningServer();
    if (running.Count == 0)
        return false;

    if (kill)
    {
        WriteColor("Calisan sunucu zorla kapatiliyor (--kill). Kaydedilmemis dunya kaybolabilir.", ConsoleColor.Yellow);
        foreach (var p in running)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(15000); } catch { }
            p.Dispose();
        }
        await Task.Delay(2000, ct);
        return true;
    }

    WriteColor("Sunucu bu kurulumdan calisiyor. Lutfen sunucuyu (kaydederek) kapatin;", ConsoleColor.Yellow);
    WriteColor("kapaninca guncelleme kendiliginden devam edecek. (Iptal: Ctrl+C)", ConsoleColor.Yellow);
    foreach (var p in running) p.Dispose();
    while (true)
    {
        await Task.Delay(2000, ct);
        var still = engine.FindRunningServer();
        bool any = still.Count > 0;
        foreach (var p in still) p.Dispose();
        if (!any) break;
    }
    // Dosya kilitlerinin birakilmasi icin kisa pay.
    await Task.Delay(2000, ct);
    Console.WriteLine("  Sunucu kapandi.");
    return true;
}

static void StartHost(string installDir, bool wasRunning)
{
    string exe = Path.Combine(installDir, OperatingSystem.IsWindows() ? "SphereNet.Host.exe" : "SphereNet.Host");
    if (!File.Exists(exe))
        return;
    if (UpdaterEngine.FindIni(installDir, "sphere.ini") == null)
    {
        WriteColor("sphere.ini yok; Host baslatilmadi.", ConsoleColor.Yellow);
        return;
    }
    try
    {
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDir, UseShellExecute = true });
        Console.WriteLine(wasRunning ? "SphereNet.Host yeniden baslatildi." : "SphereNet.Host baslatildi.");
    }
    catch (Exception ex)
    {
        WriteColor($"Host baslatilamadi: {ex.Message}", ConsoleColor.Yellow);
    }
}

static void ReportDefaults(IReadOnlyList<string> added)
{
    foreach (string f in added)
        WriteColor($"  eksik oldugu icin eklendi: {f}", ConsoleColor.DarkYellow);
    if (added.Any(f => f.EndsWith("sphere.ini", StringComparison.OrdinalIgnoreCase)))
        WriteColor("  sphere.ini varsayilan degerlerle olusturuldu - SCPFILES/MULFILES yollarini kontrol edin.",
            ConsoleColor.DarkYellow);
}

static string Describe(BuildVersion? v) =>
    v == null ? "(yok)" : $"build #{v.BuildNumber} {v.ShortSha} - {v.CommitSubject}";

static void WriteColor(string text, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = prev;
}

internal sealed class Options
{
    public string? Dir { get; private set; }
    public string? Repo { get; private set; }
    public string? Channel { get; private set; }
    public string? Runtime { get; private set; }
    public string? Token { get; private set; }
    public bool CheckOnly { get; private set; }
    public bool Force { get; private set; }
    public bool Kill { get; private set; }
    public bool? Start { get; private set; }
    public bool NoPause { get; private set; }
    public bool Help { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].TrimStart('-', '/').ToLowerInvariant();
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "dir": o.Dir = Next(); break;
                case "repo": o.Repo = Next(); break;
                case "channel": o.Channel = Next(); break;
                case "runtime": o.Runtime = Next(); break;
                case "token": o.Token = Next(); break;
                case "check": o.CheckOnly = true; break;
                case "force": o.Force = true; break;
                case "kill": o.Kill = true; break;
                case "no-start": o.Start = false; break;
                case "no-pause": case "yes": o.NoPause = true; break;
                case "help": case "h": case "?": o.Help = true; break;
                default:
                    Console.WriteLine($"Bilinmeyen secenek: {args[i]}");
                    o.Help = true;
                    break;
            }
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            SphereNet.Updater - GitHub release'inden guncelle / kur

            Kullanim: SphereNet.Updater.exe [secenekler]

              --dir <klasor>     Kurulum klasoru (varsayilan: bu EXE'nin klasoru)
              --check            Sadece surum kontrolu, indirme yok
              --force            Guncel olsa ya da kaynaktan derlenmis olsa da yeniden kur
              --kill             Calisan sunucuyu beklemeden zorla kapat (kayit kaybi riski)
              --no-start         Bitince SphereNet.Host'u baslatma
              --no-pause         Bitince Enter bekleme (otomasyon icin)
              --repo <sahip/ad>  GitHub deposu   (sphere.ini APPUPDATEREPO)
              --channel <tag>    Release tag'i   (sphere.ini APPUPDATECHANNEL, varsayilan nightly)
              --runtime <rid>    Paket platformu (sphere.ini APPUPDATERUNTIME, varsayilan win-x64)
              --token <token>    Private depo icin GitHub token'i (APPUPDATETOKEN)

            config\, save\, scripts\, logs\ ve accounts\ hic ezilmez. Eksik config
            dosyalari (sphere.ini, sphereCrypt.ini) sablonlardan eklenir, var olanlara
            dokunulmaz. Degisen dosyalar once .update\backup'a yedeklenir.
            """);
    }
}
