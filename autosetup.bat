@echo off
chcp 65001 > nul

set "ROOT_DIR=%~dp0"
set "PS_SCRIPT=%~dp0utils\autosetup.ps1"

:: Detect PowerShell 7 (pwsh) or fallback to Windows PowerShell
where pwsh.exe >nul 2>nul
if %errorlevel%==0 (
    set "PSHELL=pwsh.exe"
) else (
    set "PSHELL=powershell.exe"
)

:: Parse arguments for silent mode
set "ARGS="
:parse_args
if "%~1"=="" goto :run
if /i "%~1"=="-silent" set "ARGS=%ARGS% -Silent"
if /i "%~1"=="/silent" set "ARGS=%ARGS% -Silent"
if /i "%~1"=="-strategy" (
    set "ARGS=%ARGS% -Strategy '%~2'"
    shift
)
if /i "%~1"=="-mode" (
    set "ARGS=%ARGS% -Mode '%~2'"
    shift
)
if /i "%~1"=="-verbose" set "ARGS=%ARGS% -Verbose"
shift
goto :parse_args

:run
%PSHELL% -NoProfile -Command "Start-Process %PSHELL% -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File \"%PS_SCRIPT%\"%ARGS%') -Verb RunAs -Wait"

if %errorlevel% neq 0 (
    echo.
    echo  [!] ERROR: Administrator privileges not granted.
    echo      Please run autosetup.bat as Administrator.
    echo.
    pause
)

exit
