<#
.SYNOPSIS
    SphereNet tam yayin (publish) scripti.
    HER ZAMAN her seyi publish eder: Vue panel + SphereNet.Server + SphereNet.Host.

    Bu script build.ps1'i tam (ServerOnly OLMADAN) Release modunda calistirir,
    ardindan calistirilabilir SphereNet.Host.exe'nin gercekten uretildigini dogrular.

    NEDEN AYRI: panel ve update kartini SADECE SphereNet.Host serve eder.
    Server'i tek basina calistirirsan panel acilmaz. Bu script Host dahil
    hepsini uretir; sonra SphereNet.Host.exe'yi calistirman yeterlidir.

    "Run with PowerShell" / cift tikla calistirilabilir: is bitince (veya hata
    verince) pencere kapanmaz, Enter'a basana kadar acik kalir.

.PARAMETER Runtime
    Hedef platform. Varsayilan: win-x64
    Ornekler: win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64

.PARAMETER FrameworkDependent
    Self-contained yerine framework-dependent publish eder (daha kucuk, .NET runtime gerektirir).

.PARAMETER NoClean
    Onceki ciktiyi temizlemeden publish eder. Varsayilan: temiz publish.

.PARAMETER Run
    Publish bittikten sonra SphereNet.Host.exe'yi otomatik baslatir (yalnizca Windows).

.PARAMETER NoPause
    Bitince Enter beklemeden cikar (otomasyon/CI icin).

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Run
    .\publish.ps1 -Runtime linux-x64
    .\publish.ps1 -FrameworkDependent -NoClean
#>
param(
    [string]$Runtime = "win-x64",

    [switch]$FrameworkDependent,

    [switch]$NoClean,

    [switch]$Run,

    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$root   = $PSScriptRoot
$outDir = "$root\bin\Release"

# Cift tikla / "Run with PowerShell" ile acildiysa pencere hemen kapanmasin diye
# bitiste Enter bekleriz. -NoPause veya etkilesimsiz ortamda atlanir.
$interactive = -not $NoPause -and [Environment]::UserInteractive
$failed = $false

try {
    # build.ps1'e tam publish argumanlarini hazirla (ServerOnly / SkipPanel YOK).
    # Hashtable splatting kullaniyoruz: array splat ("-Configuration" gibi) script
    # dosyasinda pozisyonel deger olarak baglanip ValidateSet hatasi veriyor.
    $buildArgs = @{
        Configuration = "Release"
        Runtime       = $Runtime
    }
    if (-not $NoClean)        { $buildArgs.Clean = $true }
    if ($FrameworkDependent)  { $buildArgs.FrameworkDependent = $true }

    Write-Host "SphereNet tam publish basliyor (panel + Server + Host)..." -ForegroundColor Cyan
    Write-Host "  Runtime: $Runtime" -ForegroundColor DarkGray
    Write-Host ""

    & "$root\build.ps1" @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 basarisiz oldu (exit $LASTEXITCODE)." }

    # --- Host exe dogrulama ---
    $exeExt  = if ($Runtime -like "win-*") { ".exe" } else { "" }
    $hostExe = "$outDir\SphereNet.Host$exeExt"

    if (-not (Test-Path $hostExe)) {
        throw "Publish tamamlandi ama $hostExe bulunamadi. Panel/update icin Host exe sart."
    }

    Write-Host ""
    Write-Host "Publish tamamlandi." -ForegroundColor Green
    Write-Host "  Cikti : $outDir" -ForegroundColor Yellow
    Write-Host "  Host  : $hostExe" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Calistirmak icin (paneli ve update'i Host serve eder, server'i kendisi baslatir):" -ForegroundColor White
    Write-Host "  $hostExe" -ForegroundColor White
    Write-Host ""
    Write-Host "Not: SphereNet.Server.exe'yi ELLE calistirma -- o zaman panel/update acilmaz." -ForegroundColor DarkGray
    Write-Host "     sphere.ini ve scripts klasorunu $outDir altina koymayi unutma." -ForegroundColor DarkGray

    # --- Istege bagli otomatik baslat ---
    if ($Run) {
        if ($Runtime -like "win-*") {
            Write-Host ""
            Write-Host "SphereNet.Host baslatiliyor..." -ForegroundColor Cyan
            Start-Process -FilePath $hostExe -WorkingDirectory $outDir
        } else {
            Write-Host ""
            Write-Host "-Run yalnizca Windows runtime icin desteklenir; atlandi." -ForegroundColor DarkYellow
        }
    }
}
catch {
    Write-Host ""
    Write-Host "HATA: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ScriptStackTrace) {
        Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
    }
    $failed = $true
}
finally {
    if ($interactive) {
        Write-Host ""
        Read-Host "Cikmak icin Enter'a bas"
    }
}

if ($failed) { exit 1 }
