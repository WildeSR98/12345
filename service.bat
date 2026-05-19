@echo off
chcp 65001 > nul
set "LOCAL_VERSION=2.0.0"
set "MODULES_DIR=%~dp0utils\modules"

:: ── Быстрые внешние команды (вызываются из general*.bat) ───────────────────────
if "%~1"=="status_zapret" (
    call :test_service zapret soft
    call :tcp_enable
    exit /b
)

if "%~1"=="check_updates" (
    if defined NO_UPDATE_CHECK exit /b
    if exist "%~dp0utils\check_updates.enabled" (
        if not "%~2"=="soft" (
            start /b "service check_updates" cmd /c "call "%~f0" check_updates soft"
        ) else (
            call :service_check_updates soft
        )
    )
    exit /b
)

if "%~1"=="load_game_filter" (
    call :game_switch_status
    exit /b
)

if "%~1"=="load_user_lists" (
    call :load_user_lists
    exit /b
)

:: ── Проверка прав администратора ──────────────────────────────────────────────────────────
if "%1"=="admin" (
    call :check_command chcp
    call :check_command find
    call :check_command findstr
    call :check_command netsh
    call :load_user_lists
    echo Запущено с правами администратора
) else (
    call :check_extracted
    call :check_command powershell
    echo Requesting admin rights...
    powershell -NoProfile -Command "Start-Process 'cmd.exe' -ArgumentList '/c \"\"%~f0\" admin\"' -Verb RunAs"
    exit
)

:: ── МЕНЮ ─────────────────────────────────────────────────────────────────────
setlocal EnableDelayedExpansion
:menu
cls
call :ipset_switch_status
call :game_switch_status
call :check_updates_switch_status
call :get_strategy_name

set "menu_choice=null"

echo.
echo   МЕНЕДЖЕР СЛУЖБЫ ZAPRET v!LOCAL_VERSION!
echo.  !CurrentStrategy!
echo   ----------------------------------------
echo.
echo   :: СЛУЖБА
echo      1. Установить службу
echo      2. Удалить службы
echo      3. Проверить статус
echo.
echo   :: НАСТРОЙКИ
echo      4. Игровой фильтр      [!GameFilterStatus!]
echo      5. IPSet фильтр        [!IPsetStatus!]
echo      6. Автопроверка обновлений [!CheckUpdatesStatus!]
echo.
echo   :: ОБНОВЛЕНИЯ
echo      7. Обновить список IPSet
echo      8. Обновить файл Hosts
echo      9. Проверить обновления
echo.
echo   :: ИНСТРУМЕНТЫ
echo      10. Запустить диагностику
echo      11. Запустить тесты
echo      12. Экспорт отчёта
echo.
echo   ----------------------------------------
echo      0. Выход
echo.

set /p menu_choice=   Выберите вариант (0-12): 

if "%menu_choice%"=="1" goto service_install
if "%menu_choice%"=="2" goto service_remove
if "%menu_choice%"=="3" goto service_status
if "%menu_choice%"=="4" goto game_switch
if "%menu_choice%"=="5" goto ipset_switch
if "%menu_choice%"=="6" goto check_updates_switch
if "%menu_choice%"=="7" goto ipset_update
if "%menu_choice%"=="8" goto hosts_update
if "%menu_choice%"=="9" goto service_check_updates
if "%menu_choice%"=="10" goto service_diagnostics
if "%menu_choice%"=="11" goto run_tests
if "%menu_choice%"=="12" goto export_report
if "%menu_choice%"=="0" exit /b
goto menu


:: ЗАГРУЗКА СПИСКОВ ПОЛЬЗОВАТЕЛЯ =====================
:load_user_lists
set "LISTS_PATH=%~dp0lists\"
if not exist "%LISTS_PATH%ipset-exclude-user.txt" echo 203.0.113.113/32>"%LISTS_PATH%ipset-exclude-user.txt"
if not exist "%LISTS_PATH%list-general-user.txt" echo domain.example.abc>"%LISTS_PATH%list-general-user.txt"
if not exist "%LISTS_PATH%list-exclude-user.txt" echo domain.example.abc>"%LISTS_PATH%list-exclude-user.txt"
exit /b

:: TCP ENABLE ==========================
:tcp_enable
chcp 437 > nul
netsh interface tcp show global | findstr /i "timestamps" | findstr /i "enabled" > nul || netsh interface tcp set global timestamps=enabled > nul 2>&1
chcp 65001 > nul
exit /b

:: СТАТУС ==============================
:service_status
cls
chcp 437 > nul
sc query "zapret" >nul 2>&1
if !errorlevel!==0 (
    for /f "tokens=2*" %%A in ('reg query "HKLM\System\CurrentControlSet\Services\zapret" /v zapret-discord-youtube 2^>nul') do echo Стратегия службы установлена из "%%B"
)
call :test_service zapret
call :test_service WinDivert
set "BIN_PATH=%~dp0bin\"
if not exist "%BIN_PATH%\*.sys" call :PrintRed "Файл WinDivert64.sys НЕ найден."
echo:
tasklist /FI "IMAGENAME eq winws.exe" | find /I "winws.exe" > nul
if !errorlevel!==0 (
    call :PrintGreen "Обход (winws.exe) ЗАПУЩЕН."
) else (
    call :PrintRed "Обход (winws.exe) НЕ запущен."
)
chcp 65001 > nul
pause
goto menu

:test_service
chcp 437 > nul
set "ServiceName=%~1"
set "ServiceStatus="
for /f "tokens=3 delims=: " %%A in ('sc query "%ServiceName%" ^| findstr /i "STATE"') do set "ServiceStatus=%%A"
set "ServiceStatus=%ServiceStatus: =%"
if "%ServiceStatus%"=="RUNNING" (
    if "%~2"=="soft" (
        echo "%ServiceName%" уже ЗАПУЩЕН как служба, используйте "service.bat" и выберите "Удалить службы", если хотите запустить bat отдельно.
        pause
        exit /b
    ) else (
        echo Служба "%ServiceName%" ЗАПУЩЕНА.
    )
) else if "%ServiceStatus%"=="STOP_PENDING" (
    call :PrintYellow "!ServiceName! в состоянии ОСТАНОВКА, это может быть вызвано конфликтом с другим обходом. Запустите диагностику для устранения конфликтов"
) else if not "%~2"=="soft" (
    echo Служба "%ServiceName%" НЕ запущена.
)
chcp 65001 > nul
exit /b

:: УДАЛЕНИЕ ==============================
:service_remove
cls
chcp 65001 > nul
set SRVCNAME=zapret
sc query "!SRVCNAME!" >nul 2>&1
if !errorlevel!==0 (
    net stop %SRVCNAME%
    sc delete %SRVCNAME%
) else (
    echo Служба "%SRVCNAME%" не установлена.
)
tasklist /FI "IMAGENAME eq winws.exe" | find /I "winws.exe" > nul
if !errorlevel!==0 taskkill /IM winws.exe /F > nul
sc query "WinDivert" >nul 2>&1
if !errorlevel!==0 (
    net stop "WinDivert"
    sc query "WinDivert" >nul 2>&1
    if !errorlevel!==0 sc delete "WinDivert"
)
net stop "WinDivert14" >nul 2>&1
sc delete "WinDivert14" >nul 2>&1
pause
goto menu

:: УСТАНОВКА =============================
:service_install
cls
chcp 437 > nul
cd /d "%~dp0"
set "BIN_PATH=%~dp0bin\"
set "LISTS_PATH=%~dp0lists\"

echo Выберите один из вариантов:
set "count=0"
for /f "delims=" %%F in ('powershell -NoProfile -Command "Get-ChildItem -LiteralPath 'strategies' -Filter 'general*.bat' | Sort-Object { [Regex]::Replace($_.Name, '(\d+)', { $args[0].Value.PadLeft(8, '0') }) } | ForEach-Object { $_.Name }"') do (
    set /a count+=1
    echo !count!. %%F
    set "file!count!=%%F"
)

set "choice="
set /p "choice=Введите номер файла: "
if "!choice!"=="" (
    echo Выбор не сделан, выход...
    pause
    goto menu
)
set "selectedFile=strategies\!file%choice%!"
if not defined selectedFile (
    echo Неверный выбор, выход...
    pause
    goto menu
)

:: Parse args from BAT (simplified — delegates to PowerShell for complex parsing)
set "args_with_value=sni host altorder"
set "args="
set "capture=0"
set "mergeargs=0"
set QUOTE="

for /f "tokens=*" %%a in ('type "!selectedFile!"') do (
    set "line=%%a"
    call set "line=%%line:^!=EXCL_MARK%%"
    echo !line! | findstr /i "%BIN%winws.exe" >nul
    if not errorlevel 1 set "capture=1"
    if !capture!==1 (
        if not defined args set "line=!line:*%BIN%winws.exe"=!"
        set "temp_args="
        for %%i in (!line!) do (
            set "arg=%%i"
            if not "!arg!"=="^" (
                if "!arg:~0,2!" EQU "--" if not !mergeargs!==0 set "mergeargs=0"
                if "!arg:~0,1!" EQU "!QUOTE!" (
                    set "arg=!arg:~1,-1!"
                    echo !arg! | findstr ":" >nul
                    if !errorlevel!==0 (
                        set "arg=\!QUOTE!!arg!\!QUOTE!"
                    ) else if "!arg:~0,1!"=="@" (
                        set "arg=\!QUOTE!@%~dp0!arg:~1!\!QUOTE!"
                    ) else if "!arg:~0,5!"=="%%BIN%%" (
                        set "arg=\!QUOTE!!BIN_PATH!!arg:~5!\!QUOTE!"
                    ) else if "!arg:~0,7!"=="%%LISTS%%" (
                        set "arg=\!QUOTE!!LISTS_PATH!!arg:~7!\!QUOTE!"
                    ) else (
                        set "arg=\!QUOTE!%~dp0!arg!\!QUOTE!"
                    )
                ) else if "!arg:~0,12!" EQU "%%GameFilter%%" (
                    set "arg=%GameFilter%"
                ) else if "!arg:~0,15!" EQU "%%GameFilterTCP%%" (
                    set "arg=%GameFilterTCP%"
                ) else if "!arg:~0,15!" EQU "%%GameFilterUDP%%" (
                    set "arg=%GameFilterUDP%"
                )
                if !mergeargs!==1 (set "temp_args=!temp_args!,!arg!") else if !mergeargs!==3 (
                    set "temp_args=!temp_args!=!arg!"
                    set "mergeargs=1"
                ) else (set "temp_args=!temp_args! !arg!")
                if "!arg:~0,2!" EQU "--" (set "mergeargs=2") else if !mergeargs! GEQ 1 (
                    if !mergeargs!==2 set "mergeargs=1"
                    for %%x in (!args_with_value!) do (
                        if /i "%%x"=="!arg!" set "mergeargs=3"
                    )
                )
            )
        )
        if not "!temp_args!"=="" set "args=!args! !temp_args!"
    )
)

call :tcp_enable
set ARGS=%args%
call set "ARGS=%%ARGS:EXCL_MARK=^!%%"
echo Итоговые аргументы: !ARGS!
set SRVCNAME=zapret

net stop %SRVCNAME% >nul 2>&1
sc delete %SRVCNAME% >nul 2>&1
sc create %SRVCNAME% binPath= "\"%BIN_PATH%winws.exe\" !ARGS!" DisplayName= "zapret" start= auto
sc description %SRVCNAME% "Zapret DPI bypass software"
sc start %SRVCNAME%
for %%F in ("!file%choice%!") do set "filename=%%~nF"
reg add "HKLM\System\CurrentControlSet\Services\zapret" /v zapret-discord-youtube /t REG_SZ /d "!filename!" /f

pause
goto menu

:: ПРОВЕРКА ОБНОВЛЕНИЙ =======================
:service_check_updates
chcp 65001 > nul
cls
set "GITHUB_VERSION_URL=https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt"
set "GITHUB_RELEASE_URL=https://github.com/Flowseal/zapret-discord-youtube/releases/tag/"
set "GITHUB_DOWNLOAD_URL=https://github.com/Flowseal/zapret-discord-youtube/releases/latest"

for /f "delims=" %%A in ('powershell -NoProfile -Command "(Invoke-WebRequest -Uri \"%GITHUB_VERSION_URL%\" -Headers @{\"Cache-Control\"=\"no-cache\"} -UseBasicParsing -TimeoutSec 5).Content.Trim()" 2^>nul') do set "GITHUB_VERSION=%%A"

if not defined GITHUB_VERSION (
    echo Предупреждение: не удалось получить последнюю версию.
    timeout /T 9
    if "%1"=="soft" exit
    goto menu
)
if "%LOCAL_VERSION%"=="%GITHUB_VERSION%" (
    echo Установлена последняя версия: %LOCAL_VERSION%
    if "%1"=="soft" exit
    pause
    goto menu
)
echo Доступна новая версия: %GITHUB_VERSION%
echo Страница релиза: %GITHUB_RELEASE_URL%%GITHUB_VERSION%
echo Открываю страницу загрузки...
start "" "%GITHUB_DOWNLOAD_URL%"
if "%1"=="soft" exit
pause
goto menu

:: ДИАГНОСТИКА (передаётся в модуль PowerShell) ===
:service_diagnostics
cls
echo Запуск диагностики PowerShell...
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { $m='%~dp0utils\modules'; ipmo "$m\Utils.psm1","$m\Diagnostics.psm1","$m\Service.psm1" -Force; Initialize-Logger -RootDir '%~dp0'; Export-DiagnosticsReport -RootDir '%~dp0' | ForEach-Object { Write-Host "Report: $_" -ForegroundColor Green } }"
pause
goto menu

:: ЗАПУСК ТЕСТОВ ===========================
:run_tests
chcp 65001 >nul
cls
powershell -NoProfile -Command "if ($PSVersionTable -and $PSVersionTable.PSVersion -and $PSVersionTable.PSVersion.Major -ge 3) { exit 0 } else { exit 1 }" >nul 2>&1
if %errorLevel% neq 0 (
    echo Требуется PowerShell 3.0 или новее.
    pause
    goto menu
)
echo Запуск тестов стратегий в окне PowerShell...
echo Будут протестированы все конфигурации general*.bat и показаны предыдущие результаты.
start "" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0utils\autosetup.ps1" -Mode test
pause
goto menu

:: ЭКСПОРТ ОТЧЁТА =======================
:export_report
cls
echo Экспорт отчёта диагностики...
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { $m='%~dp0utils\modules'; ipmo "$m\Utils.psm1","$m\Diagnostics.psm1","$m\Service.psm1" -Force; Initialize-Logger -RootDir '%~dp0'; $r=Export-DiagnosticsReport -RootDir '%~dp0'; Write-Host "Report saved to: $r" -ForegroundColor Green }"
pause
goto menu

:: ПЕРЕКЛЮЧАТЕЛЬ ИГРОВОГО ФИЛЬТРА ========================
:game_switch_status
chcp 65001 > nul
set "gameFlagFile=%~dp0utils\game_filter.enabled"
if not exist "%gameFlagFile%" (
    set "GameFilterStatus=disabled"
    set "GameFilter=12"
    set "GameFilterTCP=12"
    set "GameFilterUDP=12"
    exit /b
)
set "GameFilterMode="
for /f "usebackq delims=" %%A in ("%gameFlagFile%") do if not defined GameFilterMode set "GameFilterMode=%%A"
if /i "%GameFilterMode%"=="all" (
    set "GameFilterStatus=enabled (TCP and UDP)"
    set "GameFilter=1024-65535"
    set "GameFilterTCP=1024-65535"
    set "GameFilterUDP=1024-65535"
) else if /i "%GameFilterMode%"=="tcp" (
    set "GameFilterStatus=enabled (TCP)"
    set "GameFilter=1024-65535"
    set "GameFilterTCP=1024-65535"
    set "GameFilterUDP=12"
) else (
    set "GameFilterStatus=enabled (UDP)"
    set "GameFilter=1024-65535"
    set "GameFilterTCP=12"
    set "GameFilterUDP=1024-65535"
)
exit /b

:game_switch
chcp 65001 > nul
cls
echo Выберите режим игрового фильтра:
echo   0. Отключить
echo   1. TCP и UDP
echo   2. Только TCP
echo   3. Только UDP
echo.
set "GameFilterChoice=0"
set /p "GameFilterChoice=Выберите вариант (0-3, по умолчанию: 0): "
if "%GameFilterChoice%"=="" set "GameFilterChoice=0"
if "%GameFilterChoice%"=="0" (
    if exist "%gameFlagFile%" (del /f /q "%gameFlagFile%") else (goto menu)
) else if "%GameFilterChoice%"=="1" (
    echo all>"%gameFlagFile%"
) else if "%GameFilterChoice%"=="2" (
    echo tcp>"%gameFlagFile%"
) else if "%GameFilterChoice%"=="3" (
    echo udp>"%gameFlagFile%"
) else (
    echo Неверный выбор, выход...
    pause
    goto menu
)
call :PrintYellow "Перезапустите zapret для применения изменений"
pause
goto menu

:: ПЕРЕКЛЮЧАТЕЛЬ АВТООБНОВЛЕНИЙ ================
:check_updates_switch_status
chcp 65001 > nul
set "checkUpdatesFlag=%~dp0utils\check_updates.enabled"
if exist "%checkUpdatesFlag%" (set "CheckUpdatesStatus=enabled") else (set "CheckUpdatesStatus=disabled")
exit /b

:check_updates_switch
chcp 65001 > nul
cls
if not exist "%checkUpdatesFlag%" (
    echo Включение проверки обновлений...
    echo ВКЛЮЧЕНО > "%checkUpdatesFlag%"
) else (
    echo Отключение проверки обновлений...
    del /f /q "%checkUpdatesFlag%"
)
pause
goto menu

:: ПЕРЕКЛЮЧАТЕЛЬ IPSET =======================
:ipset_switch_status
chcp 65001 > nul
set "listFile=%~dp0lists\ipset-all.txt"
for /f %%i in ('type "%listFile%" 2^>nul ^| find /c /v ""') do set "lineCount=%%i"
if !lineCount!==0 (
    set "IPsetStatus=any"
) else (
    findstr /R "^203\.0\.113\.113/32$" "%listFile%" >nul
    if !errorlevel!==0 (set "IPsetStatus=none") else (set "IPsetStatus=loaded")
)
exit /b

:ipset_switch
chcp 65001 > nul
cls
set "listFile=%~dp0lists\ipset-all.txt"
set "backupFile=%listFile%.backup"
if "%IPsetStatus%"=="loaded" (
    echo Переключение в режим 'none'...
    if not exist "%backupFile%" (ren "%listFile%" "ipset-all.txt.backup") else (del /f /q "%backupFile%" & ren "%listFile%" "ipset-all.txt.backup")
    >"%listFile%" (echo 203.0.113.113/32)
) else if "%IPsetStatus%"=="none" (
    echo Переключение в режим 'any'...
    >"%listFile%" (echo rem Creating empty file)
) else if "%IPsetStatus%"=="any" (
    echo Переключение в режим 'loaded'...
    if exist "%backupFile%" (
        del /f /q "%listFile%"
        ren "%backupFile%" "ipset-all.txt"
    ) else (
        echo Ошибка: нет резервной копии для восстановления. Сначала обновите список из меню службы
        pause
        goto menu
    )
)
pause
goto menu

:: ОБНОВЛЕНИЕ IPSET =======================
:ipset_update
chcp 65001 > nul
cls
set "listFile=%~dp0lists\ipset-all.txt"
set "url=https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt"
echo Обновление ipset-all...
if exist "%SystemRoot%\System32\curl.exe" (
    curl --version | find "libcurl/7"
    if !errorlevel!==0 (curl --ssl-no-revoke -L -o "%listFile%" "%url%") else (curl --ssl-revoke-best-effort -L -o "%listFile%" "%url%")
) else (
    powershell -NoProfile -Command "$url='%url%'; $out='%listFile%'; $dir=Split-Path -Parent $out; if(-not(Test-Path $dir)){New-Item -ItemType Directory -Path $dir|Out-Null}; $res=Invoke-WebRequest -Uri $url -TimeoutSec 10 -UseBasicParsing; if($res.StatusCode -eq 200){$res.Content|Out-File -FilePath $out -Encoding UTF8}else{exit 1}"
)
echo Завершено
pause
goto menu

:: ОБНОВЛЕНИЕ HOSTS =======================
:hosts_update
chcp 65001 > nul
cls
set "hostsFile=%SystemRoot%\System32\drivers\etc\hosts"
set "hostsUrl=https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts"
set "tempFile=%TEMP%\zapret_hosts.txt"
set "needsUpdate=0"
echo Проверка файла hosts...
if exist "%SystemRoot%\System32\curl.exe" (curl -L -s -o "%tempFile%" "%hostsUrl%") else (
    powershell -NoProfile -Command "$url='%hostsUrl%'; $out='%tempFile%'; $res=Invoke-WebRequest -Uri $url -TimeoutSec 10 -UseBasicParsing; if($res.StatusCode -eq 200){$res.Content|Out-File -FilePath $out -Encoding UTF8}else{exit 1}"
)
if not exist "%tempFile%" (
    call :PrintRed "Не удалось скачать файл hosts"
    pause
    goto menu
)
set "firstLine=" & set "lastLine="
for /f "usebackq delims=" %%a in ("%tempFile%") do (
    if not defined firstLine set "firstLine=%%a"
    set "lastLine=%%a"
)
findstr /C:"!firstLine!" "%hostsFile%" >nul 2>&1
if !errorlevel! neq 0 (set "needsUpdate=1")
findstr /C:"!lastLine!" "%hostsFile%" >nul 2>&1
if !errorlevel! neq 0 (set "needsUpdate=1")
if "%needsUpdate%"=="1" (
    call :PrintYellow "Файл hosts требует обновления"
    start notepad "%tempFile%"
    explorer /select,"%hostsFile%"
) else (
    call :PrintGreen "Файл hosts актуален"
    if exist "%tempFile%" del /f /q "%tempFile%"
)
pause
goto menu

:: Получение имени стратегии ===================
:get_strategy_name
set "CurrentStrategy="
for /f "tokens=2*" %%A in ('reg query "HKLM\System\CurrentControlSet\Services\zapret" /v zapret-discord-youtube 2^>nul') do set "CurrentStrategy=Strategy: %%B"
exit /b

:: Вспомогательные функции ===================
:PrintGreen
powershell -NoProfile -Command "Write-Host \"%~1\" -ForegroundColor Green"
exit /b

:PrintRed
powershell -NoProfile -Command "Write-Host \"%~1\" -ForegroundColor Red"
exit /b

:PrintYellow
powershell -NoProfile -Command "Write-Host \"%~1\" -ForegroundColor Yellow"
exit /b

:check_command
where %1 >nul 2>&1
if %errorLevel% neq 0 (
    echo [ОШИБКА] %1 не найден в PATH
    pause
    exit /b 1
)
exit /b 0

:check_extracted
if not exist "%~dp0bin\" (
    echo Zapret должен быть сначала распакован из архива
    pause
    exit
)
exit /b 0
