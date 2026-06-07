<#
.SYNOPSIS
    SphereNet tek-komut guncelleme. SERVER KAPALIYKEN calistir.

    Yaptigi is sirayla:
      1. git pull --ff-only  (kaynak repo'da)
      2. Tam build           (panel + SphereNet.Server + SphereNet.Host)
      3. Uretilen binari'leri calisma (deploy) klasorune kopyala
      4. (istege bagli) SphereNet.Host.exe'yi baslat

    NEDEN KAPALIYKEN: calisan Host.exe kilitli olur ve uzerine yazilamaz; ayrica
    shutdown'da otomatik save kapalidir. Bu yuzden once server'i kapat, sonra bu
    scripti calistir. Script hicbir sureci durdurmaz/oldurmez.

    NEREDEN CALISIR:
      - Deploy klasorunde (update.cmd oraya da kopyalanir): repo'yu sphere.ini
        APPUPDATEREPODIR'dan bulur, build'i orada yapar, sonucu kendi klasorune kopyalar.
      - Repo kokunde: build ciktisi (bin\Release) zaten calisma klasoru sayilir,
        kopyalama atlanir.

.PARAMETER RepoDir
    git kaynak klasoru. Verilmezse: deploy sphere.ini'deki APPUPDATEREPODIR,
    o da yoksa scriptin bulundugu klasor (build.ps1 iceriyorsa).

.PARAMETER DeployDir
    Binari'lerin kopyalanacagi calisma klasoru. Varsayilan: scriptin klasoru
    (repo kokunden calisilirsa repo\bin\Release).

.PARAMETER Runtime
    Hedef platform. Varsayilan: win-x64

.PARAMETER NoRun
    Guncelleme bitince SphereNet.Host.exe'yi otomatik baslatma.

.PARAMETER NoPull
    git pull adimini atla (sadece build + deploy).

.PARAMETER NoPause
    Bitince Enter beklemeden cik (otomasyon/CI icin).

.EXAMPLE
    .\update.cmd
    .\update.cmd -NoRun
    .\update.ps1 -RepoDir D:\Projeler\Yunus\sphereNet -DeployDir C:\sphereNet
#>
param(
    [string]$RepoDir,
    [string]$DeployDir,
    [string]$Runtime = "win-x64",
    [switch]$NoRun,
    [switch]$NoPull,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$scriptDir   = $PSScriptRoot
$interactive = -not $NoPause -and [Environment]::UserInteractive
$failed      = $false

function Find-Ini([string]$dir) {
    foreach ($c in @("$dir\config\sphere.ini", "$dir\sphere.ini")) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

function Get-IniValue([string]$iniPath, [string]$key) {
    if (-not $iniPath -or -not (Test-Path $iniPath)) { return $null }
    foreach ($line in Get-Content $iniPath) {
        $t = $line.Trim()
        if ($t.StartsWith("//") -or $t.StartsWith("#") -or $t.StartsWith("\")) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -lt 1) { continue }
        if ($t.Substring(0, $eq).Trim() -ieq $key) {
            return $t.Substring($eq + 1).Trim()
        }
    }
    return $null
}

function Test-PathsEqual([string]$a, [string]$b) {
    $na = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($a))
    $nb = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($b))
    return $na -ieq $nb
}

try {
    # ── 1. Kaynak repo'yu coz ─────────────────────────────────────────────────
    if (-not $RepoDir) {
        $ini = Find-Ini $scriptDir
        $fromIni = Get-IniValue $ini "APPUPDATEREPODIR"
        if ($fromIni) {
            $RepoDir = if ([IO.Path]::IsPathRooted($fromIni)) { $fromIni }
                       else { Join-Path (Split-Path $ini -Parent) $fromIni }
        }
    }
    if (-not $RepoDir -and (Test-Path "$scriptDir\build.ps1")) { $RepoDir = $scriptDir }
    if (-not $RepoDir) {
        throw "Kaynak repo bulunamadi. -RepoDir ver veya deploy sphere.ini'ye APPUPDATEREPODIR ekle."
    }
    $RepoDir = [IO.Path]::GetFullPath($RepoDir)
    if (-not (Test-Path "$RepoDir\.git"))      { throw "'$RepoDir' bir git calisma kopyasi degil (.git yok)." }
    if (-not (Test-Path "$RepoDir\build.ps1")) { throw "'$RepoDir' icinde build.ps1 yok." }

    # ── 2. Deploy (calisma) klasorunu coz ─────────────────────────────────────
    $outDir = "$RepoDir\bin\Release"
    if (-not $DeployDir) {
        # Repo kokunden calisiliyorsa build ciktisi zaten calisma klasorudur.
        $DeployDir = if (Test-Path "$scriptDir\build.ps1") { $outDir } else { $scriptDir }
    }
    $DeployDir = [IO.Path]::GetFullPath($DeployDir)

    Write-Host "SphereNet guncelleme" -ForegroundColor Cyan
    Write-Host "  Repo   : $RepoDir"   -ForegroundColor DarkGray
    Write-Host "  Deploy : $DeployDir" -ForegroundColor DarkGray
    Write-Host "  Runtime: $Runtime"   -ForegroundColor DarkGray
    Write-Host ""

    # ── 3. git pull ───────────────────────────────────────────────────────────
    if (-not $NoPull) {
        Write-Host "[1/3] git pull --ff-only" -ForegroundColor Cyan
        $dirty = git -C $RepoDir status --porcelain
        if ($LASTEXITCODE -ne 0) { throw "git status basarisiz (exit $LASTEXITCODE)." }
        if ($dirty) {
            throw "Repo'da kaydedilmemis yerel degisiklik var. Once commit/stash et: $RepoDir"
        }
        git -C $RepoDir pull --ff-only
        if ($LASTEXITCODE -ne 0) { throw "git pull basarisiz (exit $LASTEXITCODE)." }
    } else {
        Write-Host "[1/3] git pull atlandi (-NoPull)." -ForegroundColor DarkGray
    }

    # ── 4. Tam build (panel + Server + Host) ──────────────────────────────────
    Write-Host ""
    Write-Host "[2/3] build.ps1 (panel + Server + Host, Release, $Runtime)" -ForegroundColor Cyan
    & "$RepoDir\build.ps1" -Configuration Release -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 basarisiz oldu (exit $LASTEXITCODE)." }

    $exeExt  = if ($Runtime -like "win-*") { ".exe" } else { "" }
    $hostExe = "$outDir\SphereNet.Host$exeExt"
    if (-not (Test-Path $hostExe)) {
        throw "Build tamamlandi ama '$hostExe' bulunamadi. Panel/Host icin sart."
    }

    # ── 5. Deploy klasorune kopyala ───────────────────────────────────────────
    Write-Host ""
    if (Test-PathsEqual $outDir $DeployDir) {
        Write-Host "[3/3] Deploy klasoru build ciktisi ile ayni; kopyalama atlandi." -ForegroundColor DarkGray
    } else {
        Write-Host "[3/3] Binari'ler kopyalaniyor -> $DeployDir" -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null

        # panel/ klasorunu tazele (eski asset'ler kalmasin). sphere.ini, scripts\,
        # save\ gibi calisma verileri bin\Release'te olmadigindan kopyada korunur.
        if (Test-Path "$DeployDir\panel") {
            Remove-Item "$DeployDir\panel" -Recurse -Force -Confirm:$false
        }
        Copy-Item "$outDir\*" $DeployDir -Recurse -Force
        Write-Host "  Kopyalandi: $(@(Get-ChildItem $outDir -File).Count) dosya + panel\" -ForegroundColor DarkGray
    }

    Write-Host ""
    Write-Host "Guncelleme tamamlandi." -ForegroundColor Green

    # ── 6. Istege bagli: Host'u baslat ────────────────────────────────────────
    if (-not $NoRun) {
        $deployHost = "$DeployDir\SphereNet.Host$exeExt"
        if ($Runtime -like "win-*" -and (Test-Path $deployHost)) {
            Write-Host ""
            Write-Host "SphereNet.Host baslatiliyor..." -ForegroundColor Cyan
            Start-Process -FilePath $deployHost -WorkingDirectory $DeployDir
        } elseif ($Runtime -notlike "win-*") {
            Write-Host "-Run yalnizca Windows runtime icin desteklenir; atlandi." -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "Host baslatilmadi (-NoRun). Calistir: $DeployDir\SphereNet.Host$exeExt" -ForegroundColor DarkGray
    }
}
catch {
    Write-Host ""
    Write-Host "HATA: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed }
    $failed = $true
}
finally {
    if ($interactive) {
        Write-Host ""
        Read-Host "Cikmak icin Enter'a bas"
    }
}

if ($failed) { exit 1 }
