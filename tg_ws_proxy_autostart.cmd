@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem UTF-8 Codepage constant
set /a "CP_UTF8=65001"

rem Use UTF-8 for Cyrillic support in console
chcp %CP_UTF8% >nul

rem Check for Administrator privileges
net session >nul 2>&1
if !errorlevel! neq 0 (
    echo [INFO] Запрошены права Администратора для создания задачи...
    powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b 0
)

rem Search for TgWsProxy.exe starting from current directory down into subfolders
set "PROXY_EXE="
set "PROXY_DIR="

for /r "%~dp0" %%F in (TgWsProxy.exe*) do (
    set "PROXY_EXE=%%F"
    set "PROXY_DIR=%%~dpF"
    goto :found
)

rem Target found: sets variables and continues task processing
:found
set "TASK_NAME=TgWsProxy_AutoStart"

rem Verify existence of the executable
if not defined PROXY_EXE (
    echo [ОШИБКА] Файл TgWsProxy.exe не найден в текущей папке или подпапках:
    echo "%~dp0"
    pause
    exit /b 1
)

echo [INFO] Создание задачи планировщика: "%TASK_NAME%"
echo [INFO] Исполняемый файл: "%PROXY_EXE%"
echo [INFO] Рабочая директория: "%PROXY_DIR%"

rem Use PowerShell to register the scheduled task safely
rem We use bypass to ensure the command runs regardless of local environment policy
powershell -NoProfile -ExecutionPolicy RemoteSigned -Command ^
    "$Action = New-ScheduledTaskAction -Execute $env:PROXY_EXE -WorkingDirectory $env:PROXY_DIR;" ^
    "$Triggers = @();" ^
    "$Triggers += New-ScheduledTaskTrigger -AtLogOn;" ^
    "$Triggers += New-ScheduledTaskTrigger -AtStartup;" ^
    "$Triggers += New-CimInstance -ClassName MSFT_ScheduledTaskTrigger -Property @{TriggerType=11; StateChange=8; Enabled=$True} -ClientOnly;" ^
    "$Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries" ^
    " -MultipleInstances IgnoreNew -ExecutionTimeLimit 0;" ^
    "Register-ScheduledTask -TaskName $env:TASK_NAME -Action $Action -Trigger $Triggers" ^
    " -Settings $Settings -RunLevel Highest -Force | Out-Null"

if !errorlevel! equ 0 (
    echo.
    echo [УСПЕХ] Задача автозапуска успешно создана!
    echo Теперь TgWsProxy будет запускаться автоматически при запуске системы,
    echo а также при входе и разблокировке.
    echo Настройка "IgnoreNew" предотвратит запуск дубликатов процесса.
) else (
    echo.
    echo [ОШИБКА] Не удалось создать задачу в Планировщике.
    echo Код ошибки: !errorlevel!
    pause
    exit /b 1
)

echo.
echo Нажмите любую клавишу для завершения...
pause >nul
exit /b 0
