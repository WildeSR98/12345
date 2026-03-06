@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================================
rem TgWsProxy Auto-start Task Creation Script
rem Description: Creates a Windows Scheduled Task to run TgWsProxy.exe at logon
rem              with highest privileges.
rem ============================================================================


rem Set UTF-8 encoding for Cyrillic support in console
chcp 65001 >nul

rem Check for Administrator privileges
net session >nul 2>&1
if !errorlevel! neq 0 (
    echo [INFO] Запрошены права Администратора для создания задачи...
    powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b 0
)

rem Define application paths. Now looking for the EXE in the same folder as this script.
set "PROXY_EXE=%~dp0TgWsProxy.exe"
set "PROXY_DIR=%~dp0"
set "TASK_NAME=TgWsProxy_AutoStart"

rem Verify existence of the executable
if not exist "%PROXY_EXE%" (
    echo [ОШИБКА] Файл TgWsProxy.exe не найден по пути:
    echo "%PROXY_EXE%"
    echo Пожалуйста, убедитесь, что приложение собрано или путь указан верно.
    pause
    exit /b 1
)

echo [INFO] Создание задачи планировщика: "%TASK_NAME%"
echo [INFO] Исполняемый файл: "%PROXY_EXE%"
echo [INFO] Рабочая директория: "%PROXY_DIR%"

rem Use PowerShell to register the scheduled task safely
rem We use bypass to ensure the command runs regardless of local environment policy
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$Action = New-ScheduledTaskAction -Execute $env:PROXY_EXE -WorkingDirectory $env:PROXY_DIR;" ^
    "$Trigger = New-ScheduledTaskTrigger -AtLogOn;" ^
    "$Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries" ^
    " -MultipleInstances IgnoreNew -ExecutionTimeLimit 0;" ^
    "Register-ScheduledTask -TaskName $env:TASK_NAME -Action $Action -Trigger $Trigger" ^
    " -Settings $Settings -RunLevel Highest -Force | Out-Null"

if !errorlevel! equ 0 (
    echo.
    echo [УСПЕХ] Задача автозапуска успешно создана!
    echo Теперь TgWsProxy будет запускаться автоматически при входе в систему.
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
