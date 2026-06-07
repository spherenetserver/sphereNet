@echo off
REM SphereNet tek-komut guncelleme baslaticisi (cift tikla calistir).
REM ONEMLI: SERVER KAPALIYKEN calistir (Host.exe acikken uzerine yazilamaz).
REM
REM Yaptigi is: git pull -> tam build (panel+Server+Host) -> deploy klasorune
REM kopyala -> SphereNet.Host.exe baslat.
REM
REM Tum argumanlar update.ps1'e aktarilir, orn:  update.cmd -NoRun

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" -NoPause %*

echo.
pause
