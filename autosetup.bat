@echo off
chcp 65001 > nul

set "PS_SCRIPT=%~dp0utils\autosetup.ps1"

powershell -NoProfile -Command "Start-Process powershell.exe -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File \"{0}\"' -f $env:PS_SCRIPT) -Verb RunAs -Wait"

if %errorlevel% neq 0 (
    echo.
    echo  [!] ERROR: Administrator privileges not granted.
    echo      Please run autosetup.bat as Administrator.
    echo.
    pause
)

exit