namespace SphereNet.Panel.Updates;

/// <summary>
/// Host'un disinda calisan dosya-takas scripti.
///
/// NEDEN AYRI SUREC: paket SphereNet.Host.exe'yi de icerir; calisan Host kendi
/// EXE'sini kilitledigi icin uzerine yazamaz. Bu script Host cikana kadar
/// bekler, takasi yapar ve Host'u yeniden baslatir.
///
/// NEDEN C# ICINDE STRING: script, onu baslatan koda birebir bagli (parametre
/// adlari, klasor duzeni). Ayri bir .ps1 dosyasi olsaydi paketle birlikte
/// guncellenirdi — yani guncellemeyi yapan script, guncellemenin ortasinda
/// kendi altindan degisirdi. Burada tutuldugunda calisan surum her zaman
/// takasi baslatan surumdur.
///
/// NEDEN SAF ASCII: bu metin .ps1 olarak diske yazilip powershell.exe (Windows
/// PowerShell 5.1) ile calistiriliyor. 5.1, BOM'suz bir .ps1'i UTF-8 degil ANSI
/// varsayar; ASCII disi tek bir karakter (orn. em-dash) bozulur ve bozuk bayt
/// dizisi bir tirnak icerirse script parse EDILEMEZ. Bu, tam da Host kapanmisken
/// olur: sunucu bir daha acilmaz. UpdateService ayrica BOM ile yazar — iki
/// katmanin ikisi de bilincli. Buraya ASCII disi karakter EKLEME.
/// </summary>
internal static class UpdaterScript
{
    public const string PowerShell = """
        <#
            SphereNet guncelleme uygulayici - SphereNet.Panel tarafindan uretilir.
            ELLE CALISTIRMA: Host surecinin kapanmasini bekler ve dosyalari takas eder.
            Gunluk: logs\update.log
        #>
        param(
            [Parameter(Mandatory)][int]$HostPid,
            [Parameter(Mandatory)][string]$TargetDir,
            [Parameter(Mandatory)][string]$StageDir,
            [Parameter(Mandatory)][string]$HostExe,
            [Parameter(Mandatory)][string]$LogFile
        )

        $ErrorActionPreference = 'Stop'

        function Write-Log([string]$Message) {
            $line = '[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $Message
            try { Add-Content -LiteralPath $LogFile -Value $line -Encoding utf8 } catch { }
            Write-Host $line
        }

        # Yedegi ayri bir kok altinda tut: .update\staged silinirken yedek de
        # ucmasin.
        $backupDir = Join-Path $TargetDir '.update\backup'

        function Backup-Item([string]$RelativePath) {
            $src = Join-Path $TargetDir $RelativePath
            if (-not (Test-Path -LiteralPath $src)) { return }
            $dst = Join-Path $backupDir $RelativePath
            $dstParent = Split-Path $dst -Parent
            if (-not (Test-Path -LiteralPath $dstParent)) {
                New-Item -ItemType Directory -Force -Path $dstParent | Out-Null
            }
            Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
        }

        function Restore-Backup {
            Write-Log 'ROLLBACK: yedek geri yukleniyor...'
            if (-not (Test-Path -LiteralPath $backupDir)) {
                Write-Log 'ROLLBACK: yedek klasoru yok - geri alinacak bir sey bulunamadi.'
                return
            }
            try {
                Get-ChildItem -LiteralPath $backupDir -Force | ForEach-Object {
                    $dst = Join-Path $TargetDir $_.Name
                    if ($_.PSIsContainer -and (Test-Path -LiteralPath $dst)) {
                        Remove-Item -LiteralPath $dst -Recurse -Force -Confirm:$false
                    }
                    Copy-Item -LiteralPath $_.FullName -Destination $dst -Recurse -Force
                }
                Write-Log 'ROLLBACK: onceki surum geri yuklendi.'
            } catch {
                Write-Log "ROLLBACK BASARISIZ: $($_.Exception.Message)"
                Write-Log "Onceki surum elle geri yuklenebilir: $backupDir"
            }
        }

        Write-Log '================ SphereNet guncelleme basliyor ================'
        Write-Log "  Hedef : $TargetDir"
        Write-Log "  Paket : $StageDir"
        Write-Log "  Host  : $HostExe (pid $HostPid)"

        $started = $false

        try {
            # ---- 1. Host'un cikmasini bekle --------------------------------
            # Host kendi kendine kapaniyor (panel OnHostExit'i cagirdi); burada
            # sadece kilidin birakilmasini bekliyoruz.
            $proc = $null
            try { $proc = Get-Process -Id $HostPid -ErrorAction Stop } catch { }

            if ($proc) {
                Write-Log 'Host surecinin cikmasi bekleniyor (en fazla 120 sn)...'
                if (-not $proc.WaitForExit(120000)) {
                    Write-Log 'Host 120 sn icinde cikmadi - sonlandiriliyor.'
                    try { $proc.Kill($true) } catch { Write-Log "Kill basarisiz: $($_.Exception.Message)" }
                    $proc.WaitForExit(15000) | Out-Null
                }
                Write-Log 'Host cikti.'
            } else {
                Write-Log 'Host sureci zaten kapali.'
            }

            # Host, oyun sunucusunu kapatir; yine de kilit birakan bir artik
            # kalmadigindan emin ol - Server.exe acikken uzerine yazilamaz.
            $serverExeName = 'SphereNet.Server'
            Get-Process -Name $serverExeName -ErrorAction SilentlyContinue | ForEach-Object {
                $path = $null
                try { $path = $_.Path } catch { }
                # Yalnizca BU kurulumun surecine dokun: ayni makinede baska bir
                # SphereNet kurulumu calisiyor olabilir.
                if ($path -and $path.StartsWith($TargetDir, [StringComparison]::OrdinalIgnoreCase)) {
                    Write-Log "Artik sunucu sureci sonlandiriliyor (pid $($_.Id))."
                    try { $_.Kill($true); $_.WaitForExit(15000) | Out-Null } catch { }
                }
            }

            # Dosya kilitlerinin gercekten birakilmasi icin kisa bir pay.
            Start-Sleep -Seconds 2

            # ---- 2. Yedek --------------------------------------------------
            if (Test-Path -LiteralPath $backupDir) {
                Remove-Item -LiteralPath $backupDir -Recurse -Force -Confirm:$false
            }
            New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

            Write-Log 'Mevcut surum yedekleniyor...'
            # Yalnizca paketin degistirecegi seyler yedeklenir. config\, save\,
            # scripts\ ve logs\ pakette YOK, dolayisiyla hic dokunulmaz.
            Get-ChildItem -LiteralPath $StageDir -Force | ForEach-Object {
                Backup-Item $_.Name
            }
            Write-Log "Yedek: $backupDir"

            # ---- 3. Takas --------------------------------------------------
            Write-Log 'Yeni dosyalar kopyalaniyor...'
            $fileCount = 0

            Get-ChildItem -LiteralPath $StageDir -Force | ForEach-Object {
                $dst = Join-Path $TargetDir $_.Name

                if ($_.PSIsContainer) {
                    # Klasorleri (panel\) tam tazele: eski hash'li bundle
                    # dosyalari kalirsa index.html olmayan asset'lere isaret eder.
                    if (Test-Path -LiteralPath $dst) {
                        Remove-Item -LiteralPath $dst -Recurse -Force -Confirm:$false
                    }
                    Copy-Item -LiteralPath $_.FullName -Destination $dst -Recurse -Force
                    Write-Log "  klasor: $($_.Name)\"
                } else {
                    Copy-Item -LiteralPath $_.FullName -Destination $dst -Force
                    $fileCount++
                }
            }
            Write-Log "Kopyalandi: $fileCount dosya + klasorler."

            if (-not (Test-Path -LiteralPath $HostExe)) {
                throw "Takas sonrasi Host EXE bulunamadi: $HostExe"
            }

            # ---- 4. Temizlik -----------------------------------------------
            try {
                Remove-Item -LiteralPath $StageDir -Recurse -Force -Confirm:$false
            } catch {
                Write-Log "Uyari: paket klasoru silinemedi ($($_.Exception.Message)) - zararsiz."
            }

            Write-Log 'Guncelleme basarili.'
        }
        catch {
            Write-Log "HATA: $($_.Exception.Message)"
            if ($_.ScriptStackTrace) { Write-Log $_.ScriptStackTrace }
            Restore-Backup
        }
        finally {
            # Basarida da rollback'te de sunucu geri gelmeli: rollback sonrasi
            # calisan (eski) bir sunucu, hic calismayan bir sunucudan iyidir.
            try {
                Write-Log "Host yeniden baslatiliyor: $HostExe"
                Start-Process -FilePath $HostExe -WorkingDirectory $TargetDir | Out-Null
                $started = $true
            } catch {
                Write-Log "Host baslatilamadi: $($_.Exception.Message)"
                Write-Log "Elle baslat: $HostExe"
            }
            Write-Log "================ Bitti (host baslatildi: $started) ================"
        }
        """;
}
