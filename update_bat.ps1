$script = @"
@echo off
chcp 65001 > nul

:: Передаём путь к скрипту через переменную среды, чтобы избежать проблем с кавычками
set "PS_SCRIPT=%~dp0utils\autosetup.ps1"

:run_setup
:: Открываем PowerShell-окно с правами администратора напрямую
powershell -NoProfile -Command "`$p=Start-Process powershell.exe -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File \`"{0}\`"' -f `$env:PS_SCRIPT) -Verb RunAs -Wait -PassThru; exit `$p.ExitCode"

:: Если код возврата 99 — значит было обновление, нужно перезапуститься
if %errorlevel% equ 99 (
    echo.
    echo  [*] Обновление скриптов успешно завершено! Системный перезапуск...
    timeout /t 2 /nobreak >nul
    goto run_setup
)

:: Если пользователь отклонил UAC — показываем сообщение в этом окне
if %errorlevel% neq 0 (
    echo.
    echo  [!] Права администратора не предоставлены.
    echo      Запустите autosetup.bat от имени администратора.
    echo.
    pause
)
"@

# Пишем в файл в кодировке UTF-8 без BOM, чтобы русский текст корректно отображался (из-за chcp 65001)
$utf8NoBom = New-Object System.Text.UTF8Encoding $False
[System.IO.File]::WriteAllText("$PSScriptRoot\autosetup.bat", $script, $utf8NoBom)
