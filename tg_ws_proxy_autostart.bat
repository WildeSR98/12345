@echo off
setlocal
chcp 65001 >nul

:: Проверка наличия прав администратора
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Запрошены права Администратора для создания задачи...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

:: Определяем пути к приложению и его рабочей директории
set "PROXY_EXE=%~dp0tg\tg-ws-proxy-main\tg-ws-proxy-main\dist\TgWsProxy.exe"
set "PROXY_DIR=%~dp0tg\tg-ws-proxy-main\tg-ws-proxy-main\dist"

if not exist "%PROXY_EXE%" (
    echo [ОШИБКА] Файл TgWsProxy.exe не найден по ожидаемому пути:
    echo "%PROXY_EXE%"
    echo Проверьте пути.
    pause
    exit /b 1
)

set "TASK_NAME=TgWsProxy_AutoStart"

echo Создание задачи автозапуска "%TASK_NAME%"...
echo Исполняемый файл: "%PROXY_EXE%"
echo Рабочая директория: "%PROXY_DIR%"

:: Создаем задачу средствами PowerShell
:: Use -MultipleInstances IgnoreNew предотвратит запуск, если задача (приложение) уже активна
powershell -NoProfile -ExecutionPolicy Bypass -Command "$Action = New-ScheduledTaskAction -Execute '%PROXY_EXE%' -WorkingDirectory '%PROXY_DIR%'; $Trigger = New-ScheduledTaskTrigger -AtLogOn; $Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit 0; Register-ScheduledTask -TaskName '%TASK_NAME%' -Action $Action -Trigger $Trigger -Settings $Settings -RunLevel Highest -Force | Out-Null"

if %ERRORLEVEL% equ 0 (
    echo.
    echo [УСПЕХ] Задача автозапуска успешно создана!
    echo Теперь TgWsProxy.exe будет автоматически запускаться при входе в Windows от имени Администратора.
    echo В задаче настроено правило: "Не запускать новый экземпляр, если уже запущен" ^(IgnoreNew^).
) else (
    echo.
    echo [ОШИБКА] Возникла непредвиденная ошибка при создании задачи в Планировщике.
)

pause
