@echo off
REM SphereNet tam publish baslaticisi (cift tikla calistir).
REM PowerShell ExecutionPolicy'yi atlar ve pencere is bitince acik kalir.
REM Tum argumanlar publish.ps1'e aktarilir, orn:  publish.cmd -Run

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -NoPause %*

echo.
pause
