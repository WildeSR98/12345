#requires -Version 5.1
# =============================================================================
#  ZAPRET AUTO-SETUP v2.1
# =============================================================================
[CmdletBinding()]
param(
    [switch]$Silent,
    [string]$Strategy = $null,
    [ValidateSet("install", "remove", "reinstall", "test", "diagnostics", "")]
    [string]$Mode = ""
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
cmd /c chcp 65001 > $null

# Fix PS5.1 console codepage via P/Invoke (chcp in subprocess doesn't affect parent)
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ConsoleCP {
    [DllImport("kernel32.dll")]
    public static extern bool SetConsoleOutputCP(uint wCodePageID);
    [DllImport("kernel32.dll")]
    public static extern bool SetConsoleCP(uint wCodePageID);
}
'@ -ErrorAction SilentlyContinue
    [ConsoleCP]::SetConsoleOutputCP(65001) | Out-Null
    [ConsoleCP]::SetConsoleCP(65001) | Out-Null
}

# -- Пути (относительно расположения скрипта) ----------------------------------
$rootDir    = Split-Path $PSScriptRoot
$modulesDir = Join-Path $PSScriptRoot "modules"
$listsDir   = Join-Path $rootDir "lists"
$binDir     = Join-Path $rootDir "bin"
$winwsExe   = Join-Path $binDir "winws.exe"
$configPath = Join-Path $rootDir "config.json"

# -- Загрузка модулей ----------------------------------------------------------
$modules = @("Utils", "Update", "Service", "Lists", "Diagnostics", "Strategy")
$modulesLoaded = @()
$modulesFailed = @()

foreach ($mod in $modules) {
    $modPath = Join-Path $modulesDir "$mod.psm1"
    if (-not (Test-Path $modPath)) {
        Write-Host "[КРИТИЧНО] Модуль не найден: $modPath" -ForegroundColor Red
        exit 1
    }
    try {
        Import-Module $modPath -Force -ErrorAction Stop | Out-Null
        $modulesLoaded += $mod
    }
    catch {
        Write-Host "[КРИТИЧНО] Ошибка загрузки модуля '$mod': $_" -ForegroundColor Red
        $modulesFailed += $mod
    }
}

if ($modulesFailed.Count -gt 0) {
    Write-Host "`n[КРИТИЧНО] Не удалось загрузить: $($modulesFailed -join ', ')" -ForegroundColor Red
    Write-Host "Проверьте файлы в папке: $modulesDir" -ForegroundColor Yellow
    Read-Host "Нажмите Enter для выхода"
    exit 1
}

# -- Fallback если Utils не загрузился -----------------------------------------
if (-not (Get-Command Initialize-Logger -ErrorAction SilentlyContinue)) {
    function Initialize-Logger { param([string]$RootDir,[switch]$Verbose,[switch]$Silent) }
    function Write-Header { param([string]$Title) Write-Host "`n=== $Title ===" -ForegroundColor Cyan }
    function Write-Step { param([string]$Title) Write-Host "`n>> $Title" -ForegroundColor Yellow }
    function Write-OK { param([string]$Message) Write-Host "[OK] $Message" -ForegroundColor Green }
    function Write-Warn { param([string]$Message) Write-Host "[ВНИМ] $Message" -ForegroundColor Yellow }
    function Write-Err { param([string]$Message) Write-Host "[ОШИБ] $Message" -ForegroundColor Red }
    function Write-Info { param([string]$Message) Write-Host "[ИНФО] $Message" -ForegroundColor Gray }
    function Test-Admin {
        $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    function Show-Progress { param([string]$Activity,[string]$Status,[int]$PercentComplete) }
    function Hide-Progress {}
}

# -- Загрузка конфигурации -----------------------------------------------------
$Config = @{}
if (Test-Path $configPath) {
    try {
        $Config = Get-Content $configPath -Encoding UTF8 -Raw | ConvertFrom-Json
    }
    catch {
        Write-Warn "Ошибка чтения config.json, используются настройки по умолчанию"
    }
}

# -- Инициализация логгера -----------------------------------------------------
Initialize-Logger -RootDir $rootDir -Verbose:$Verbose -Silent:$Silent

# -- Фоновая проверка обновлений (запускается в начале, не блокирует) ----------
if (-not $Silent -and -not $Mode) {
    Start-BackgroundUpdateCheck -RootDir $rootDir -ListsDir $listsDir -PSScriptRoot $PSScriptRoot -ServiceBatPath (Join-Path $rootDir "service.bat")
}

Write-Header "ZAPRET AUTO-SETUP v2.1"
Write-Info "Рабочая папка: $rootDir"
Write-Info "PowerShell: $($PSVersionTable.PSVersion)"

# -- Проверка прав администратора ----------------------------------------------
Write-Step "Проверка прав администратора"
if (-not (Test-Admin)) {
    Write-Err "Требуются права администратора. Запустите через autosetup.bat"
    if (-not $Silent) { Read-Host "`nНажмите Enter для выхода" }
    exit 1
}
Write-OK "Права администратора подтверждены"

# -- Проверка готовых обновлений (из фоновой задачи) ---------------------------
$pendingUpdate = Get-PendingUpdate -RootDir $rootDir
if ($pendingUpdate) {
    if ($pendingUpdate.type -eq "12345") {
        Write-Header "ДОСТУПНО ОБНОВЛЕНИЕ СКРИПТОВ 12345"
        Write-Info "Новая версия: $($pendingUpdate.version)"
    }
    elseif ($pendingUpdate.type -eq "zapret") {
        Write-Header "ДОСТУПНО ОБНОВЛЕНИЕ ZAPRET"
        Write-Info "Новая версия: $($pendingUpdate.version) (скачана заранее)"
    }
    if (-not $Silent) {
        $updOpt = Read-Host "  [1] Установить сейчас / [Любая клавиша] Пропустить"
        if ($updOpt -eq "1") {
            $ok = Install-PendingUpdate -RootDir $rootDir -ListsDir $listsDir -PSScriptRoot $PSScriptRoot
            if ($ok) {
                Write-OK "Обновление установлено! Перезапустите autosetup.bat"
                Read-Host; exit 0
            } else {
                Write-Err "Обновление не удалось, продолжаем..."
            }
        }
    }
}

# -- Главное меню --------------------------------------------------------------
$mainOpt = "1"
if ($Mode -eq "remove") { $mainOpt = "2" }
elseif ($Mode -eq "reinstall") { $mainOpt = "3" }
elseif ($Mode -eq "test") { $mainOpt = "5" }
elseif ($Silent -and -not $Mode) { $mainOpt = "1" }
elseif (-not $Silent -and -not $Mode) {
    Write-Host ""
    Write-Host ("  " + "=" * 54) -ForegroundColor DarkCyan
    Write-Host "  ГЛАВНОЕ МЕНЮ" -ForegroundColor Cyan
    Write-Host ("  " + "=" * 54) -ForegroundColor DarkCyan
    Write-Host "    [1]  Установить / Обновить конфигурацию" -ForegroundColor Gray
    Write-Host "    [2]  Удалить zapret (полная деинсталляция)" -ForegroundColor Gray
    Write-Host "    [3]  Переустановить (удалить и настроить заново)" -ForegroundColor Gray
    Write-Host "    [4]  Диагностика и отчёт" -ForegroundColor Gray
    Write-Host "    [5]  Тест стратегий и установка" -ForegroundColor Gray
    Write-Host ""
    $mainOpt = Read-Host "  Выберите (1/2/3/4/5, по умолчанию 1)"
}

if ($mainOpt -eq "4") {
    Write-Step "Запуск диагностики"
    $reportPath = Export-DiagnosticsReport -RootDir $rootDir
    Write-OK "Отчёт сохранён: $reportPath"
    if (-not $Silent) {
        Write-Host "`n  Нажмите Enter для выхода..." -ForegroundColor Cyan
        Read-Host
    }
    exit 0
}

if ($mainOpt -eq "5") {
    Write-Header "ТЕСТ СТРАТЕГИЙ И УСТАНОВКА"
    $bestBat = Invoke-StrategyTestSuite -RootDir $rootDir -ListsDir $listsDir
    if ($bestBat) {
        Write-Step "Установка лучшей стратегии: $(Split-Path $bestBat -Leaf)"
        $installed = Install-ZapretService -BatPath $bestBat -BinDir $binDir -ListsDir $listsDir -UtilsDir $PSScriptRoot -WinwsExe $winwsExe
        if ($installed) {
            Write-OK "Служба zapret установлена с лучшей стратегией!"
            Test-PostInstallAccess
        } else {
            Write-Warn "Статус установки неопределён"
        }
    } else {
        Write-Info "Стратегия для установки не выбрана"
    }
    if (-not $Silent) {
        Write-Host "`n  Нажмите Enter для выхода..." -ForegroundColor Cyan
        Read-Host
    }
    exit 0
}

# -- Удаление (варианты 2 и 3) -------------------------------------------------
if ($mainOpt -eq "2" -or $mainOpt -eq "3") {
    Write-Step "Удаление служб zapret"
    Stop-ZapretService
    Write-OK "Службы удалены. Ваши списки сохранены."
    if ($mainOpt -eq "2") {
        if (-not $Silent) { Read-Host "`nНажмите Enter для выхода" }
        exit 0
    }
    Write-Info "Продолжаем установку с чистого листа..."
    Start-Sleep -Seconds 1
}

# -- Загрузка пользовательских списков-заглушек --------------------------------
Initialize-UserLists -ListsDir $listsDir

# -- Проверка наличия файлов ---------------------------------------------------
Write-Step "Проверка необходимых файлов"
if (-not (Test-Path $winwsExe)) {
    Write-Err "winws.exe не найден в $binDir"
    Write-Warn "Убедитесь, что архив распакован корректно"
    if (-not $Silent) { Read-Host "`nНажмите Enter для выхода" }
    exit 1
}
Write-OK "winws.exe найден"

if (-not (Get-Command "curl.exe" -ErrorAction SilentlyContinue)) {
    Write-Warn "curl.exe не найден в PATH — HTTP-тесты будут ограничены"
} else {
    Write-OK "curl.exe найден"
}

# -- Конфликтующие службы ------------------------------------------------------
Write-Step "Проверка конфликтующих служб"
$foundConflicts = Test-Conflicts
if ($foundConflicts.Count -gt 0) {
    Write-Warn "Найдены конфликтующие службы: $($foundConflicts -join ', ')"
    if ($Silent) {
        Remove-Conflicts -Services $foundConflicts
    } else {
        $ans = Read-Host "  Удалить их автоматически? (Y/N, по умолчанию Y)"
        if ($ans -eq "" -or $ans -match "^[Yy]") { Remove-Conflicts -Services $foundConflicts }
    }
} else {
    Write-OK "Конфликтующие службы не найдены"
}

# -- Game Filter ---------------------------------------------------------------
Write-Step "Настройка Game Filter"
$currentMode = (Get-GameFilterPorts -UtilsDir $PSScriptRoot).Mode
Write-Info "Текущий статус: $currentMode"
if (-not $Silent) {
    Write-Host "  [1] Оставить как есть  [2] Отключить  [3] TCP+UDP  [4] Только TCP  [5] Только UDP" -ForegroundColor Gray
    $gf = Read-Host "  Выберите (1..5)"
    switch ($gf) {
        "2" { Set-GameFilter -UtilsDir $PSScriptRoot -Mode "disabled"; Write-OK "Game Filter отключён" }
        "3" { Set-GameFilter -UtilsDir $PSScriptRoot -Mode "all"; Write-OK "Game Filter: TCP+UDP" }
        "4" { Set-GameFilter -UtilsDir $PSScriptRoot -Mode "tcp"; Write-OK "Game Filter: только TCP" }
        "5" { Set-GameFilter -UtilsDir $PSScriptRoot -Mode "udp"; Write-OK "Game Filter: только UDP" }
        default { Write-Info "Game Filter не изменён" }
    }
}

# -- TCP timestamps ------------------------------------------------------------
Write-Step "Включение TCP timestamps"
netsh interface tcp set global timestamps=enabled > $null 2>&1
Write-OK "TCP timestamps включены"

# -- Загрузка списков с GitHub -------------------------------------------------
Write-Step "Загрузка списков IP и доменов с GitHub"
$allLists = if ($Config -and $Config.lists -and $Config.lists.files) { $Config.lists.files } else { @() }
$repoBase = if ($Config -and $Config.repositories -and $Config.repositories.zapret_core) { "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main" } else { "" }

foreach ($entry in $allLists) {
    if ($entry.Remote -and -not $entry.Remote.StartsWith("http")) {
        $entry.Remote = "$repoBase/$($entry.Remote)"
    }
}

$downloaded = Download-ListsParallel -ListEntries $allLists -ListsDir $listsDir

foreach ($entry in $allLists) {
    $localPath = Join-Path $listsDir $entry.Local
    if ($entry.User) {
        if (-not (Test-Path $localPath)) {
            [IO.File]::WriteAllLines($localPath, @($entry.Stub), [Text.UTF8Encoding]::new($false))
            Write-OK "Создан пользовательский файл: $($entry.Local)"
        }
        continue
    }
    if ($entry.Remote -eq "") {
        if (-not (Test-Path $localPath)) {
            [IO.File]::WriteAllLines($localPath, @($entry.Stub), [Text.UTF8Encoding]::new($false))
            Write-OK "Создана заглушка: $($entry.Local)"
        }
        continue
    }
    $dl = $downloaded[$entry.Local]
    if ($dl -and $dl.Success -and $dl.Lines.Count -gt 0) {
        $merged = Merge-ListFile -FilePath $localPath -NewLines $dl.Lines
        [IO.File]::WriteAllLines($localPath, $merged, [Text.UTF8Encoding]::new($false))
        Write-OK "$($entry.Local): объединён ($($merged.Count) строк)"
    } elseif (-not (Test-Path $localPath)) {
        [IO.File]::WriteAllLines($localPath, @($entry.Stub), [Text.UTF8Encoding]::new($false))
        Write-Warn "$($entry.Local): загрузка не удалась, создана заглушка"
    } else {
        Write-Info "$($entry.Local): используется существующий файл"
    }
}

# -- Очистка списков -----------------------------------------------------------
Write-Step "Очистка списков (дубликаты, пустые строки, невалидные IP)"
$removeOverlap = ($Config -and $Config.features -and $Config.features.remove_cidr_overlap -eq $true)
Repair-ListFiles -ListsPath $listsDir -RemoveOverlap:$removeOverlap

# -- Обновление hosts ----------------------------------------------------------
Write-Step "Обновление файла hosts"
$hostsUrl = if ($Config -and $Config.repositories -and $Config.repositories.zapret_core -and $Config.repositories.zapret_core.hosts_service) { $Config.repositories.zapret_core.hosts_service } else { "" }
if ($hostsUrl) {
    Update-HostsFile -HostsUrl $hostsUrl
}

# -- Проверка доступности БЕЗ обхода -------------------------------------------
Write-Step "Проверка доступности сайтов БЕЗ обхода"
Stop-ZapretProcess
$preResults = Test-PreInstallAccess
if (($preResults | Where-Object { $_.Reachable }).Count -eq $preResults.Count) {
    Write-OK "Все сайты доступны без обхода!"
    if (-not $Silent) {
        $ans = Read-Host "  Всё равно продолжить установку? (Y/N, по умолчанию N)"
        if ($ans -notmatch "^[Yy]") { Read-Host "Нажмите Enter для выхода"; exit 0 }
    }
}

# -- Получение списка конфигов -------------------------------------------------
$batFiles = @(Get-BatFiles -RootDir $rootDir)
if ($batFiles.Count -eq 0) {
    Write-Err "Не найдены файлы general*.bat"
    if (-not $Silent) { Read-Host "Нажмите Enter для выхода" }
    exit 1
}

$chosenBat = $null

# Silent mode с указанной стратегией
if ($Silent -and $Strategy) {
    $match = $batFiles | Where-Object { $_.Name -like "*$Strategy*" } | Select-Object -First 1
    if ($match) { $chosenBat = $match.FullName }
    else { Write-Warn "Стратегия '$Strategy' не найдена, используется первая доступная" }
}

if (-not $chosenBat -and $Silent) {
    Write-Info "Тихий режим: требуется явный выбор стратегии"
    exit 1
}

if (-not $chosenBat -and -not $Silent) {
    Write-Host ""
    Write-Host ("-" * 60) -ForegroundColor DarkGray
    Write-Host "  Найдено конфигов: $($batFiles.Count)" -ForegroundColor Cyan
    Write-Host ("-" * 60) -ForegroundColor DarkGray

    Write-Host @"

  Выберите режим:
    [1]  Быстрый тест — протестировать ВСЕ конфиги и выбрать лучший
    [2]  Ручной выбор конфига из списка
    [3]  Отмена, не устанавливать службу
"@ -ForegroundColor Gray

    $modeChoice = Read-Host "  Введите номер (1/2/3)"

    switch ($modeChoice) {
        "1" {
            $testResult = Invoke-StrategyTest -RootDir $rootDir -ListsDir $listsDir
            if ($testResult) {
                $chosenBat = Select-StrategyFromResults -Sorted $testResult.Sorted -BestConfig $testResult.Best.Config
            }
        }
        "2" {
            Write-Host ""
            for ($i = 0; $i -lt $batFiles.Count; $i++) {
                Write-Host "  [$($i+1)] $($batFiles[$i].Name)" -ForegroundColor Gray
            }
            $pick = Read-Host "`n  Введите номер конфига"
            $idx = [int]$pick - 1
            if ($idx -ge 0 -and $idx -lt $batFiles.Count) { $chosenBat = $batFiles[$idx].FullName }
        }
        default {
            Write-Info "Установка отменена"
            Read-Host "Нажмите Enter для выхода"
            exit 0
        }
    }
}

if (-not $chosenBat) {
    Write-Err "Конфиг не выбран"
    if (-not $Silent) { Read-Host "Нажмите Enter для выхода" }
    exit 1
}

# -- Установка службы ----------------------------------------------------------
Write-Step "Установка службы Windows: $(Split-Path $chosenBat -Leaf)"
$installed = Install-ZapretService -BatPath $chosenBat -BinDir $binDir -ListsDir $listsDir -UtilsDir $PSScriptRoot -WinwsExe $winwsExe
if ($installed) {
    Write-OK "Служба 'zapret' установлена и запущена"
} else {
    Write-Warn "Служба установлена, но статус запуска неопределён. Проверьте service.bat → пункт 3"
}

# -- Финальная проверка --------------------------------------------------------
if (-not $Silent) {
    Write-Step "Проверка доступности С обходом (ждём 5 сек...)"
    Test-PostInstallAccess

    # Уведомление в трее
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
        $balloon = New-Object System.Windows.Forms.NotifyIcon
        $balloon.Icon = [System.Drawing.SystemIcons]::Information
        $balloon.BalloonTipTitle = "Zapret"
        $balloon.BalloonTipText = "Служба установлена: $(Split-Path $chosenBat -Leaf)"
        $balloon.Visible = $true
        $balloon.ShowBalloonTip(5000)
        $balloon.Dispose()
    } catch {}

    Write-Host @"

  Дальнейшие команды:
    service.bat  — менеджер службы (меню)
    service.bat  → пункт 10 — диагностика
    service.bat  → пункт 11 — тест всех конфигов
    service.bat  → пункт 12 — экспорт отчёта
"@ -ForegroundColor DarkGray

    Write-Host "`n  Нажмите любую клавишу для выхода..." -ForegroundColor Cyan
    [void][System.Console]::ReadKey($true)
}

Write-Log -Level "INFO" -Message "=== Установка завершена ==="
exit 0
