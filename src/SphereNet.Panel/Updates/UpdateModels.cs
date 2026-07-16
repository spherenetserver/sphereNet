namespace SphereNet.Panel.Updates;

/// <summary>
/// Bir build'in kimligi. CI (.github/workflows/release.yml) bu JSON'u hem
/// paketin icine hem de release asset'i olarak ayri ayri koyar; calisan kurulum
/// kendi kopyasini EXE'nin yanindaki version.json'dan okur.
/// </summary>
/// <param name="Sha">Tam commit SHA'si.</param>
/// <param name="ShortSha">Kisa SHA — yalnizca gosterim icin.</param>
/// <param name="Branch">Paketin uretildigi dal.</param>
/// <param name="BuildNumber">
/// github.run_number. Monoton artar; "uzaktaki surum benimkinden yeni mi"
/// karsilastirmasi SHA ile yapilamayacagi (siralanamaz) icin buna dayanir.
/// </param>
/// <param name="BuiltAt">Paketin uretildigi an (UTC).</param>
/// <param name="Runtime">Hedef RID, orn. win-x64.</param>
/// <param name="CommitSubject">Commit basligi — panelde "neler degisti" satiri.</param>
public sealed record BuildVersion(
    string Sha,
    string ShortSha,
    string Branch,
    long BuildNumber,
    DateTime BuiltAt,
    string Runtime,
    string CommitSubject
);

/// <summary>
/// sphere.ini [SPHERE] bolumunden okunan guncelleme ayarlari.
/// </summary>
/// <param name="Repo">GitHub deposu, "sahip/ad" formatinda.</param>
/// <param name="Channel">Asset'lerin asili oldugu release tag'i.</param>
/// <param name="Runtime">Indirilecek paketin RID'i — asset adini belirler.</param>
/// <param name="CheckMinutes">Arka plan kontrol araligi. 0 = otomatik kontrol kapali.</param>
/// <param name="Token">
/// Istege bagli GitHub token'i. Public repoda GEREKMEZ — release asset'leri
/// anonim indirilir. Yalnizca depo private yapilirsa gerekir.
/// </param>
public sealed record UpdateSettings(
    string Repo,
    string Channel = "nightly",
    string Runtime = "win-x64",
    int CheckMinutes = 15,
    string? Token = null
);

/// <summary>Guncelleme akisinin o anki adimi.</summary>
public enum UpdateState
{
    /// <summary>Bos — kontrol edilmis ya da hic denenmemis.</summary>
    Idle,
    Checking,
    Downloading,
    Verifying,
    Extracting,
    /// <summary>Paket dogrulandi ve acildi; updater baslatiliyor.</summary>
    Staged,
    /// <summary>Updater calisiyor — Host birazdan kapanip yeniden baslayacak.</summary>
    Applying,
    Failed,
}

/// <summary>
/// Panelin /api/update/status'tan gordugu tam durum.
/// </summary>
/// <param name="Current">Calisan kurulumun surumu. Kaynaktan derlenmisse null.</param>
/// <param name="Latest">Release'teki son surum. Hic kontrol edilmediyse null.</param>
/// <param name="UpdateAvailable">Uzaktaki build numarasi yerelden buyuk mu.</param>
/// <param name="IsDevBuild">version.json yok — yerel/kaynak build.</param>
/// <param name="State">UpdateState'in string hali.</param>
/// <param name="ProgressPercent">Indirme yuzdesi; diger adimlarda 0.</param>
/// <param name="Message">Son bilgi ya da hata mesaji.</param>
/// <param name="LastCheckedUtc">En son basarili kontrol.</param>
/// <param name="CanApply">
/// Guncelleme uygulanabilir mi. Standalone (Host'suz) modda false — Host
/// surecini durdurup yeniden baslatacak kimse yok.
/// </param>
/// <param name="Busy">Bir indirme/uygulama akisi surmekte mi.</param>
public sealed record UpdateStatus(
    BuildVersion? Current,
    BuildVersion? Latest,
    bool UpdateAvailable,
    bool IsDevBuild,
    string State,
    int ProgressPercent,
    string? Message,
    DateTime? LastCheckedUtc,
    bool CanApply,
    bool Busy,
    string Repo,
    string Channel,
    string Runtime
);
