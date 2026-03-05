# =============================================================================
#  ZAPRET AUTO-SETUP  |  Автоматическая установка и выбор лучшей конфигурации
# =============================================================================

$rootDir = Split-Path $PSScriptRoot
$listsDir = Join-Path $rootDir "lists"
$binDir = Join-Path $rootDir "bin"
$winwsExe = Join-Path $binDir  "winws.exe"

# ── Читаем локальную версию из service.bat ────────────────────────────────────
function Get-LocalVersion {
    param([string]$ServiceBat)
    if (-not (Test-Path $ServiceBat)) { return "0.0.0" }
    $line = Get-Content $ServiceBat | Select-String 'LOCAL_VERSION=(.+)' | Select-Object -First 1
    if ($line -and $line.Matches[0].Groups[1].Value) {
        return $line.Matches[0].Groups[1].Value.Trim().Trim('"')
    }
    return "0.0.0"
}

# ── Получаем информацию о последнем релизе через GitHub API ───────────────────
function Get-LatestRelease {
    $apiUrl = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest"
    try {
        $headers = @{ "User-Agent" = "zapret-autosetup/1.0" }
        $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -TimeoutSec 8 -ErrorAction Stop
        $zipAsset = $release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
        return [PSCustomObject]@{
            Version     = $release.tag_name -replace '^v', ''
            ZipUrl      = if ($zipAsset) { $zipAsset.browser_download_url } else { $null }
            ReleasePage = $release.html_url
        }
    }
    catch {
        return $null
    }
}

# ── Получаем информацию о последнем коммите из репозитория 12345 ────────────────
function Get-LatestCommit12345 {
    $apiUrl = "https://api.github.com/repos/WildeSR98/12345/commits?per_page=1"
    try {
        $headers = @{ "User-Agent" = "zapret-autosetup/1.0" }
        $commits = Invoke-RestMethod -Uri $apiUrl -Headers $headers -TimeoutSec 8 -ErrorAction Stop
        if ($commits -and $commits.Count -gt 0) {
            return $commits[0].sha
        }
        return $null
    }
    catch {
        return $null
    }
}

# ── Авто-обновление из репозитория 12345: скачать zip → распаковать → заменить файлы ──
function Invoke-AutoUpdate12345 {
    param([string]$TargetDir)

    $zipUrl = "https://github.com/WildeSR98/12345/archive/refs/heads/main.zip"
    $tmpZip = Join-Path $env:TEMP "12345_update.zip"
    $tmpDir = Join-Path $env:TEMP "12345_update_extracted"

    Write-Info "Скачиваем архив обновления 12345..."
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $tmpZip -TimeoutSec 60 -UseBasicParsing -ErrorAction Stop
    }
    catch {
        Write-Err "Не удалось скачать архив 12345: $_"
        return $false
    }

    Write-Info "Распаковываем 12345..."
    if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
    try {
        Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force -ErrorAction Stop
    }
    catch {
        Write-Err "Не удалось распаковать архив 12345: $_"
        return $false
    }

    $extractedRoot = $tmpDir
    $children = Get-ChildItem $tmpDir
    if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
        $extractedRoot = $children[0].FullName
    }

    Write-Info "Останавливаем службу zapret и WinDivert..."
    Stop-Zapret
    net stop zapret > $null 2>&1
    net stop WinDivert > $null 2>&1
    net stop WinDivert14 > $null 2>&1

    Write-Info "Обновляем файлы из 12345..."
    # Копируем всё с заменой (за исключением папок/файлов git)
    Get-ChildItem $extractedRoot -Recurse | Where-Object { $_.FullName -notmatch '\\\.github\\' -and $_.FullName -notmatch '\\\.git\\' } | ForEach-Object {
        $relativePath = $_.FullName.Substring($extractedRoot.Length + 1)
        $destPath = Join-Path $TargetDir $relativePath

        if ($_.PSIsContainer) {
            if (-not (Test-Path $destPath)) {
                New-Item -ItemType Directory -Path $destPath | Out-Null
            }
        } else {
            Copy-Item $_.FullName -Destination $destPath -Force
        }
    }

    Write-OK "Файлы 12345 обновлены"

    # Чистим временные файлы
    Remove-Item $tmpZip  -Force -ErrorAction SilentlyContinue
    Remove-Item $tmpDir  -Recurse -Force -ErrorAction SilentlyContinue

    return $true
}

# ── Авто-обновление: скачать zip → распаковать → заменить файлы → сохранить списки ──
function Invoke-AutoUpdate {
    param([string]$ZipUrl, [string]$TargetDir)

    $tmpZip = Join-Path $env:TEMP "zapret_update.zip"
    $tmpDir = Join-Path $env:TEMP "zapret_update_extracted"

    Write-Info "Скачиваем архив обновления..."
    try {
        Invoke-WebRequest -Uri $ZipUrl -OutFile $tmpZip -TimeoutSec 60 -UseBasicParsing -ErrorAction Stop
    }
    catch {
        Write-Err "Не удалось скачать архив: $_"
        return $false
    }

    Write-Info "Распаковываем..."
    if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
    try {
        Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force -ErrorAction Stop
    }
    catch {
        Write-Err "Не удалось распаковать архив: $_"
        return $false
    }

    # Ищем корень распакованного архива (может быть папка внутри)
    $extractedRoot = $tmpDir
    $children = Get-ChildItem $tmpDir
    if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
        $extractedRoot = $children[0].FullName
    }

    # ── Остановка службы перед обновлением ──────────────────────────────────────
    Write-Info "Останавливаем службу zapret и WinDivert..."
    Stop-Zapret
    net stop zapret > $null 2>&1
    net stop WinDivert > $null 2>&1
    net stop WinDivert14 > $null 2>&1

    # ── 1. Обновляем bin/ — полная замена ───────────────────────────────────────
    $newBin = Join-Path $extractedRoot "bin"
    if (Test-Path $newBin) {
        Write-Info "Обновляем bin/ (исполняемые файлы)..."
        Copy-Item "$newBin\*" -Destination (Join-Path $TargetDir "bin") -Recurse -Force
        Write-OK "bin/ обновлён"
    }

    # ── 2. Обновляем *.bat (кроме autosetup.bat — это наш файл) ─────────────────
    Write-Info "Обновляем .bat файлы стратегий..."
    Get-ChildItem $extractedRoot -Filter "*.bat" |
    Where-Object { $_.Name -notlike "autosetup*" } |
    ForEach-Object {
        Copy-Item $_.FullName -Destination (Join-Path $TargetDir $_.Name) -Force
    }
    Write-OK ".bat файлы обновлены"

    # ── 3. Обновляем utils/ (кроме autosetup.ps1 — это наш файл) ────────────────
    $newUtils = Join-Path $extractedRoot "utils"
    if (Test-Path $newUtils) {
        Write-Info "Обновляем utils/..."
        Get-ChildItem $newUtils |
        Where-Object { $_.Name -notlike "autosetup*" } |
        ForEach-Object {
            $destDir = Join-Path $TargetDir "utils"
            Copy-Item $_.FullName -Destination (Join-Path $destDir $_.Name) -Force
        }
        Write-OK "utils/ обновлён"
    }

    # ── 4. Списки: МЕРЖИМ новые с существующими (не перезаписываем!) ─────────────
    $newLists = Join-Path $extractedRoot "lists"
    if (Test-Path $newLists) {
        Write-Info "Мержим списки (ваши записи сохраняются)..."
        Get-ChildItem $newLists -Filter "*.txt" |
        Where-Object { $_.Name -notlike "*-user*" } |
        ForEach-Object {
            $localFile = Join-Path $listsDir $_.Name
            $remoteLines = Get-Content $_.FullName -Encoding UTF8
            $merged = Merge-ListFile -FilePath $localFile -NewLines $remoteLines
            [IO.File]::WriteAllLines($localFile, $merged, [Text.UTF8Encoding]::new($false))
            Write-OK "$($_.Name): мерж выполнен ($($merged.Count) строк)"
        }
    }

    # Очищаем ipset-файлы после мержа (на случай если в ZIP были невалидные записи)
    Repair-IpsetFiles -ListsPath $listsDir

    # ── 5. Обновляем service.bat (версия должна обновиться) ─────────────────────
    $newService = Join-Path $extractedRoot "service.bat"
    if (Test-Path $newService) {
        Copy-Item $newService -Destination (Join-Path $TargetDir "service.bat") -Force
        Write-OK "service.bat обновлён"
    }

    # Чистим временные файлы
    Remove-Item $tmpZip  -Force -ErrorAction SilentlyContinue
    Remove-Item $tmpDir  -Recurse -Force -ErrorAction SilentlyContinue

    return $true
}

# ── Цвета ─────────────────────────────────────────────────────────────────────
function Write-Header { param($t) Write-Host "`n$('═'*60)" -ForegroundColor DarkCyan; Write-Host "  $t" -ForegroundColor Cyan; Write-Host $('═' * 60) -ForegroundColor DarkCyan }
function Write-Step { param($t) Write-Host "`n▶  $t" -ForegroundColor Yellow }
function Write-OK { param($t) Write-Host "   ✔  $t" -ForegroundColor Green }
function Write-Warn { param($t) Write-Host "   ⚠  $t" -ForegroundColor Yellow }
function Write-Err { param($t) Write-Host "   ✘  $t" -ForegroundColor Red }
function Write-Info { param($t) Write-Host "   •  $t" -ForegroundColor Gray }

# ── Вспомогательные ───────────────────────────────────────────────────────────
function Stop-Zapret {
    Get-Process -Name "winws" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

function Test-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-BatFiles {
    Get-ChildItem -Path $rootDir -Filter "*.bat" |
    Where-Object { $_.Name -notlike "service*" -and $_.Name -notlike "autosetup*" } |
    Sort-Object { [Regex]::Replace($_.Name, '(\d+)', { $args[0].Value.PadLeft(8, '0') }) }
}

# ── Проверка доступности цели через curl ──────────────────────────────────────
function Test-Url {
    param([string]$Url, [int]$Timeout = 5)
    if (-not (Get-Command "curl.exe" -ErrorAction SilentlyContinue)) { return $false }
    $code = & curl.exe -s -o NUL -w "%{http_code}" -m $Timeout --max-redirs 3 $Url 2>$null
    return ($LASTEXITCODE -eq 0 -and $code -match "^[23]")
}

function Test-Ping {
    param([string]$ComputerName, [int]$Count = 2)
    try {
        $r = Test-Connection -ComputerName $ComputerName -Count $Count -ErrorAction Stop
        return [bool]($r | Where-Object { $_.StatusCode -eq 0 -or $_.Status -eq 'Success' })
    }
    catch { return $false }
}

# ── Тест одного конфига по набору целей ───────────────────────────────────────
function Invoke-ConfigTest {
    param([System.IO.FileInfo]$BatFile, [array]$Targets, [int]$CurlTimeout = 5)

    # Запуск конфига
    $proc = Start-Process -FilePath "cmd.exe" `
        -ArgumentList "/c `"$($BatFile.FullName)`"" `
        -WorkingDirectory $rootDir -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 3   # ждём инициализации winws

    $ok = 0; $fail = 0

    foreach ($t in $Targets) {
        if ($t.Type -eq "ping") {
            if (Test-Ping $t.ComputerName) { $ok++ } else { $fail++ }
        }
        else {
            if (Test-Url $t.Url $CurlTimeout) { $ok++ } else { $fail++ }
        }
    }

    Stop-Zapret
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }

    return [PSCustomObject]@{ Config = $BatFile.Name; FullPath = $BatFile.FullName; OK = $ok; Fail = $fail; Score = $ok }
}

# ── DPI-тест (паттерн 16-20 KB freeze из тестaзапрет.ps1) ───────────────────────
function Get-DpiSuite {
    # Суит урлов из https://github.com/hyperion-cs/dpi-checkers (Apache-2.0)
    try {
        $suite = Invoke-RestMethod `
            -Uri "https://hyperion-cs.github.io/dpi-checkers/ru/tcp-16-20/suite.json" `
            -TimeoutSec 8 -ErrorAction Stop
        return $suite | ForEach-Object { $_.url }
    }
    catch {
        return @()   # нет сети — вернём пустой массив
    }
}

# Возвращает кол-во целей БЕЗ DPI-блокировки (0 = всё заблокировано)
function Invoke-DpiCheck {
    param(
        [string[]]$DpiUrls,          # URL-ы из суита
        [int]$RangeBytes = 20480,  # 20 KB
        [int]$WarnMinKB = 16,
        [int]$WarnMaxKB = 20,
        [int]$TimeoutSec = 5,
        [int]$MaxUrls = 8      # ограничиваем кол-во URL для скорости
    )

    if (-not (Get-Command "curl.exe" -ErrorAction SilentlyContinue)) { return -1 }  # curl нет — пропускаем
    if ($DpiUrls.Count -eq 0) { return -1 }

    $rangeSpec = "0-$($RangeBytes - 1)"
    $protocols = @(
        @{ Label = "HTTP"; Args = @("--http1.1") }
        @{ Label = "TLS1.2"; Args = @("--tlsv1.2", "--tls-max", "1.2") }
        @{ Label = "TLS1.3"; Args = @("--tlsv1.3", "--tls-max", "1.3") }
    )

    $clean = 0   # цели, где DPI не замечен

    $urlsToTest = if ($DpiUrls.Count -gt $MaxUrls) { $DpiUrls[0..($MaxUrls - 1)] } else { $DpiUrls }

    foreach ($url in $urlsToTest) {
        $urlBlocked = $false
        foreach ($proto in $protocols) {
            $curlArgs = @("-L", "--range", $rangeSpec, "-m", $TimeoutSec,
                "-w", "%{http_code} %{size_download}", "-o", "NUL", "-s",
                "--max-redirs", "3") + $proto.Args + $url
            $out = & curl.exe @curlArgs 2>$null
            $exit = $LASTEXITCODE

            if ($out -match '^(?<code>\d{3})\s+(?<size>\d+)$') {
                $sizeKB = [math]::Round([int64]$matches['size'] / 1024, 1)
                # Паттерн 16-20 KB при ошибке — признак DPI-замораживания
                if ($exit -ne 0 -and $sizeKB -ge $WarnMinKB -and $sizeKB -le $WarnMaxKB) {
                    $urlBlocked = $true; break  # URL заблокирован — дальше протоколы не проверяем
                }
            }
        }
        if (-not $urlBlocked) { $clean++ }
    }

    return $clean
}

# ── Чтение статуса Game Filter (аналог service.bat load_game_filter) ────────────
function Get-GameFilterPorts {
    $flagFile = Join-Path $PSScriptRoot "game_filter.enabled"
    if (-not (Test-Path $flagFile)) { return @{ TCP = "12"; UDP = "12" } }
    
    $mode = (Get-Content $flagFile -TotalCount 1).Trim().ToLower()
    if ($mode -eq "all") { return @{ TCP = "1024-65535"; UDP = "1024-65535" } }
    if ($mode -eq "tcp") { return @{ TCP = "1024-65535"; UDP = "12" } }
    if ($mode -eq "udp") { return @{ TCP = "12"; UDP = "1024-65535" } }
    return @{ TCP = "12"; UDP = "12" }
}

# ── Парсинг аргументов winws из .bat для установки службы ─────────────────────
function Get-WinwsArgs {
    param([string]$BatPath)

    $BIN_PATH = $binDir + "\"
    $LISTS_PATH = $listsDir + "\"
    $winwsArgs = ""
    $capture = $false

    $filter = Get-GameFilterPorts

    foreach ($line in (Get-Content $BatPath)) {
        $line = $line.TrimEnd(" ^")
        if ($line -match 'winws\.exe"') { $capture = $true }
        if ($capture) {
            $line = $line `
                -replace [regex]::Escape('%BIN%'), $BIN_PATH `
                -replace [regex]::Escape('%LISTS%'), $LISTS_PATH `
                -replace '"%~dp0bin\\"', $BIN_PATH `
                -replace '"%~dp0lists\\"', $LISTS_PATH `
                -replace '%GameFilterTCP%', $filter.TCP `
                -replace '%GameFilterUDP%', $filter.UDP

            if ($line -match '"winws\.exe"(.*)') { $line = $matches[1] }
            $winwsArgs += " " + $line.Trim()
        }
    }
    return $winwsArgs.Trim()
}

# ── Установка как службы Windows ──────────────────────────────────────────────
function Install-ZapretService {
    param([string]$BatPath)

    $SRVCNAME = "zapret"
    $winwsArgs = Get-WinwsArgs -BatPath $BatPath

    # TCP timestamps
    netsh interface tcp set global timestamps=enabled > $null 2>&1

    net stop $SRVCNAME > $null 2>&1
    sc.exe delete $SRVCNAME > $null 2>&1
    Start-Sleep -Milliseconds 500

    $null = sc.exe create $SRVCNAME `
        binPath= "`"$winwsExe`" $winwsArgs" `
        DisplayName= "zapret" `
        start= auto 2>&1

    sc.exe description $SRVCNAME "Zapret DPI bypass software" > $null 2>&1
    $startResult = sc.exe start $SRVCNAME 2>&1
    $configName = [System.IO.Path]::GetFileNameWithoutExtension($BatPath)
    reg add "HKLM\System\CurrentControlSet\Services\zapret" /v zapret-discord-youtube /t REG_SZ /d "$configName" /f > $null 2>&1

    return ($LASTEXITCODE -eq 0 -or $startResult -match "1056")  # 1056 = уже запущен
}

# ── Слияние файлов списков (merge + дедупликация) ─────────────────────────────
# Объединяет строки существующего файла и нового содержимого.
# Комментарии (#) и пустые строки из нового содержимого сохраняются;
# дубликаты удаляются по нормализованной строке (trim + lower).
function Merge-ListFile {
    param(
        [string]$FilePath,       # путь к файлу на диске
        [string[]]$NewLines      # строки из нового (скачанного) источника
    )

    # Читаем существующие строки (если файл есть)
    $oldLines = @()
    if (Test-Path $FilePath) {
        $oldLines = Get-Content $FilePath -Encoding UTF8 -ErrorAction SilentlyContinue
    }

    # Собираем множество нормализованных строк для дедупликации
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $result = [System.Collections.Generic.List[string]]::new()

    foreach ($line in ($NewLines + $oldLines)) {
        $trimmed = $line.TrimEnd()
        $key = $trimmed.Trim()

        # Пустые строки и комментарии — пропускаем дедупликацию, добавляем один раз
        if ($key -eq '' -or $key.StartsWith('#')) {
            if ($seen.Add("__special__$trimmed")) { [void]$result.Add($trimmed) }
            continue
        }

        if ($seen.Add($key)) { [void]$result.Add($trimmed) }
    }

    return , $result.ToArray()   # возвращаем как массив
}

# ── Очистка ipset-файлов от записей которые winws не принимает ──────────────────
function Repair-IpsetFiles {
    param([string]$ListsPath)

    # Удаляем ТОЛЬКО записи которые winws точно отклоняет:
    #   0.x.x.x/n — сеть THIS-NET (0.0.0.0/8 и подобные)
    #   1.0.0.0/24 — конкретная запись которую winws отклоняет (баг парсера)
    # IPv6 адреса (::1, fc00::/7, 2001::/32 и др.) — НЕ удаляем,
    # winws поддерживает IPv6, они нужны для обхода IPv6 трафика!
    $badPatterns = @(
        '^\s*0\.\d+\.\d+\.\d+',   # 0.x.x.x — THIS-NET, winws отклоняет
        '^\s*1\.0\.0\.0/'          # 1.0.0.0/x — winws отклоняет несмотря на корректность
    )

    $ipsetFiles = Get-ChildItem $ListsPath -Filter "ipset-*.txt" -ErrorAction SilentlyContinue
    foreach ($file in $ipsetFiles) {
        $lines = Get-Content $file.FullName -Encoding UTF8 -ErrorAction SilentlyContinue
        $cleaned = $lines | Where-Object {
            $line = $_.Trim()
            if ($line -eq '' -or $line.StartsWith('#')) { return $true }  # комментарии сохраняем
            foreach ($pat in $badPatterns) {
                if ($line -match $pat) { return $false }
            }
            return $true
        }
        $removed = $lines.Count - $cleaned.Count
        if ($removed -gt 0) {
            [IO.File]::WriteAllLines($file.FullName, $cleaned, [Text.UTF8Encoding]::new($false))
            Write-OK "$($file.Name): удалено $removed записей (0.x.x.x и 1.0.0.0/x — не поддерживаются winws)"
        }
    }
}


Write-Header "ZAPRET AUTO-SETUP v1.0"
Write-Info "Рабочая папка: $rootDir"

# 1. Проверка прав администратора
Write-Step "Проверка прав администратора"
if (-not (Test-Admin)) {
    Write-Err "Скрипт запущен без прав администратора. Перезапустите через autosetup.bat"
    Read-Host "`nНажмите Enter для выхода"; exit 1
}
Write-OK "Права администратора подтверждены"

# 1.5 Главное меню
Write-Host ""
Write-Host ("  " + "═" * 54) -ForegroundColor DarkCyan
Write-Host "  ГЛАВНОЕ МЕНЮ" -ForegroundColor Cyan
Write-Host ("  " + "═" * 54) -ForegroundColor DarkCyan
Write-Host "    [1]  Установить / обновить конфиг" -ForegroundColor Gray
Write-Host "    [2]  Удалить zapret (деинсталляция, выход)" -ForegroundColor Gray
Write-Host "    [3]  Переустановить (сначала удалить, затем настроить заново)" -ForegroundColor Gray
Write-Host ""

$mainOpt = Read-Host "  Выберите (1/2/3, по умолчанию 1)"

# Блок удаления (shared для вариантов 2 и 3)
if ($mainOpt -eq "2" -or $mainOpt -eq "3") {
    Write-Step "Удаление службы zapret и WinDivert"
    Stop-Zapret
    net stop zapret      > $null 2>&1; sc.exe delete zapret      > $null 2>&1
    net stop WinDivert   > $null 2>&1; sc.exe delete WinDivert   > $null 2>&1
    net stop WinDivert14 > $null 2>&1; sc.exe delete WinDivert14 > $null 2>&1
    Write-OK "Службы удалены. (Ваши списки IP/доменов не тронуты)"

    if ($mainOpt -eq "2") {
        Write-Host "`n  Нажмите Enter для выхода." -ForegroundColor Cyan
        Read-Host; exit 0
    }

    Write-Host ""
    Write-Info "Продолжаем установку с чистого листа..."
    Start-Sleep -Seconds 1
}

# 1.8 Проверка версии и автообновление 12345
Write-Step "Проверка версии скриптов (WildeSR98/12345)"
$localCommitFile = Join-Path $rootDir "utils\12345_version.txt"
$localCommit = if (Test-Path $localCommitFile) { (Get-Content $localCommitFile).Trim() } else { "none" }

Write-Info "Запрашиваем последний коммит 12345 с GitHub..."
$remoteCommit = Get-LatestCommit12345

if (-not $remoteCommit) {
    Write-Warn "Не удалось проверить версию 12345 (нет сети или GitHub недоступен)"
}
elseif ($localCommit -eq $remoteCommit) {
    Write-OK "Версия 12345 актуальна!"
}
else {
    Write-Host ""
    Write-Host ("  " + "─" * 56) -ForegroundColor DarkYellow
    Write-Host "  🆕 ДОСТУПНО ОБНОВЛЕНИЕ СКРИПТОВ 12345" -ForegroundColor Yellow
    Write-Host ("  " + "─" * 56) -ForegroundColor DarkYellow
    
    $vc = Read-Host "  [1] Обновить и перезапустить / [Любая другая клавиша] Пропустить"
    if ($vc -eq "1") {
        Write-Step "Обновление файлов из репозитория 12345"
        $ok = Invoke-AutoUpdate12345 -TargetDir $rootDir
        if ($ok) {
            # Сохраняем новый хэш
            $remoteCommit | Out-File $localCommitFile -Encoding ascii
            
            Write-OK "Обновление из 12345 завершено!"
            Write-Info "Требуется перезапуск скрипта для применения обновлений."
            Write-Host "`nНажмите Enter для выхода... После этого запустите autosetup.bat заново." -ForegroundColor Cyan
            Read-Host
            
            [Environment]::Exit(0)
        }
        else {
            Write-Err "Обновление 12345 не удалось. Продолжаем со старой версией."
        }
    }
}

# 2. Проверка версии и автообновление zapret
Write-Step "Проверка версии zapret"
$serviceBat = Join-Path $rootDir "service.bat"
$localVersion = Get-LocalVersion -ServiceBat $serviceBat
Write-Info "Установленная версия: $localVersion"
Write-Info "Запрашиваем последнюю версию с GitHub..."

$latestRelease = Get-LatestRelease
if (-not $latestRelease) {
    Write-Warn "Не удалось проверить версию (нет сети или GitHub недоступен)"
}
elseif ($localVersion -eq $latestRelease.Version) {
    Write-OK "Версия актуальна: $localVersion"
}
else {
    Write-Host ""
    Write-Host ("  " + "─" * 56) -ForegroundColor DarkYellow
    Write-Host "  🆕 ДОСТУПНА НОВАЯ ВЕРСИЯ: $($latestRelease.Version)  (у вас: $localVersion)" -ForegroundColor Yellow
    Write-Host ("  " + "─" * 56) -ForegroundColor DarkYellow
    Write-Host ""
    Write-Host "  Что сделать?" -ForegroundColor Cyan
    Write-Host "    [1]  Авто-обновление (скачать и установить, списки сохранятся)" -ForegroundColor Gray
    Write-Host "    [2]  Открыть страницу релиза в браузере" -ForegroundColor Gray
    Write-Host "    [3]  Пропустить, продолжить с текущей версией" -ForegroundColor Gray
    Write-Host ""

    $vc = Read-Host "  Выберите (1/2/3)"
    switch ($vc) {
        "1" {
            if (-not $latestRelease.ZipUrl) {
                Write-Err "ZIP-файл не найден в релизе. Перейдите на страницу релиза вручную."
                Start-Process $latestRelease.ReleasePage
            }
            else {
                Write-Step "Авто-обновление до версии $($latestRelease.Version)"
                $ok = Invoke-AutoUpdate -ZipUrl $latestRelease.ZipUrl -TargetDir $rootDir
                if ($ok) {
                    Write-OK "Обновление завершено! Версия $($latestRelease.Version) установлена."
                    Write-Info "Скрипт продолжит работу с новыми файлами."
                    # Перечитываем версию после обновления
                    $localVersion = Get-LocalVersion -ServiceBat $serviceBat
                }
                else {
                    Write-Err "Авто-обновление не удалось. Продолжаем с текущей версией."
                }
            }
        }
        "2" {
            Start-Process $latestRelease.ReleasePage
            Write-Info "Страница релиза открыта в браузере. Продолжаем с текущей версией."
        }
        default {
            Write-Info "Пропускаем обновление."
        }
    }
}

Write-Step "Проверка наличия файлов"
if (-not (Test-Path $winwsExe)) {
    Write-Err "winws.exe не найден в $binDir"
    Write-Warn "Убедитесь что архив распакован правильно"
    Read-Host "`nНажмите Enter для выхода"; exit 1
}
Write-OK "winws.exe найден"

if (-not (Get-Command "curl.exe" -ErrorAction SilentlyContinue)) {
    Write-Warn "curl.exe не найден в PATH — тестирование HTTP будет ограничено"
}
else {
    Write-OK "curl.exe найден"
}

# 3. Проверка конфликтующих служб
Write-Step "Проверка конфликтующих служб"
$conflicts = @("GoodbyeDPI", "discordfix_zapret", "winws1", "winws2")
$foundConflicts = @()
foreach ($svc in $conflicts) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if ($s) { $foundConflicts += $svc }
}
if ($foundConflicts.Count -gt 0) {
    Write-Warn "Найдены конфликтующие службы: $($foundConflicts -join ', ')"
    $ans = Read-Host "   Удалить их автоматически? (Y/N, по умолчанию Y)"
    if ($ans -eq "" -or $ans -match "^[Yy]") {
        foreach ($svc in $foundConflicts) {
            net stop $svc > $null 2>&1
            sc.exe delete $svc > $null 2>&1
            Write-OK "Удалена служба: $svc"
        }
        # Очистка WinDivert
        net stop WinDivert   > $null 2>&1; sc.exe delete WinDivert   > $null 2>&1
        net stop WinDivert14 > $null 2>&1; sc.exe delete WinDivert14 > $null 2>&1
    }
}
else {
    Write-OK "Конфликтующие службы не найдены"
}

# 4. Настройка Game Filter
Write-Step "Настройка фильтрации для игр (Game Filter)"
$filterFile = Join-Path $PSScriptRoot "game_filter.enabled"
$currentMode = if (Test-Path $filterFile) { (Get-Content $filterFile -TotalCount 1).Trim().ToLower() } else { "disabled" }

Write-Host "  Текущий статус: " -ForegroundColor DarkGray -NoNewline
if ($currentMode -eq "all") { Write-Host "ВКЛЮЧЕН (TCP и UDP)" -ForegroundColor Green }
elseif ($currentMode -eq "tcp") { Write-Host "ВКЛЮЧЕН (только TCP)" -ForegroundColor Yellow }
elseif ($currentMode -eq "udp") { Write-Host "ВКЛЮЧЕН (только UDP)" -ForegroundColor Yellow }
else { Write-Host "ОТКЛЮЧЕН" -ForegroundColor Red }

Write-Host ""
Write-Host "  Что сделать?" -ForegroundColor Cyan
Write-Host "    [1]  Оставить как есть (пропустить)" -ForegroundColor Gray
Write-Host "    [2]  Отключить Game Filter" -ForegroundColor Gray
Write-Host "    [3]  Включить для всех игр (TCP + UDP)" -ForegroundColor Gray
Write-Host "    [4]  Включить только для TCP" -ForegroundColor Gray
Write-Host "    [5]  Включить только для UDP" -ForegroundColor Gray
Write-Host ""

$gfModeId = Read-Host "  Выберите (1..5)"
switch ($gfModeId) {
    "2" { Remove-Item $filterFile -Force -ErrorAction SilentlyContinue; Write-OK "Game Filter отключен" }
    "3" { "all" | Out-File $filterFile -Encoding ascii; Write-OK "Game Filter: TCP и UDP" }
    "4" { "tcp" | Out-File $filterFile -Encoding ascii; Write-OK "Game Filter: только TCP" }
    "5" { "udp" | Out-File $filterFile -Encoding ascii; Write-OK "Game Filter: только UDP" }
    default { Write-Info "Статус Game Filter не изменён" }
}

# 5. TCP timestamps
Write-Step "Включение TCP timestamps"
netsh interface tcp show global | findstr /i "timestamps" | findstr /i "enabled" > $null 2>&1
if ($LASTEXITCODE -ne 0) {
    netsh interface tcp set global timestamps=enabled > $null 2>&1
    Write-OK "TCP timestamps включены"
}
else {
    Write-OK "TCP timestamps уже включены"
}

# 5 + 6. Обновление и слияние ВСЕХ ipset и list файлов
Write-Step "Обновление и слияние списков IP и доменов"

$repoBase = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main"
$repoLists = "$repoBase/lists"
$repoService = "$repoBase/.service"

# ── Таблица всех файлов ────────────────────────────────────────────────────────
# Remote = URL источника в репозитории (пустой → только заглушка, не качаем)
# Stub   = значение по умолчанию для создания пустого файла
# User   = $true → файл пользователя: только создаём заглушку, мерж не делаем
$allLists = @(
    # ipset — IP-файлы
    @{ Local = "ipset-all.txt"; Remote = "$repoService/ipset-service.txt"; Stub = "# Добавьте сюда IP/подсети, например: 1.2.3.4/32"; User = $false }
    @{ Local = "ipset-exclude.txt"; Remote = "$repoLists/ipset-exclude.txt"; Stub = "# Добавьте сюда исключения IP, например: 10.0.0.0/8"; User = $false }
    @{ Local = "ipset-exclude-user.txt"; Remote = ""; Stub = "# Добавьте сюда ваши исключения IP, например: 10.0.0.0/8"; User = $true }

    # list — доменные файлы
    @{ Local = "list-general.txt"; Remote = "$repoLists/list-general.txt"; Stub = "# Добавьте сюда домены, например: example.com"; User = $false }
    @{ Local = "list-google.txt"; Remote = "$repoLists/list-google.txt"; Stub = "# Добавьте сюда домены Google, например: example.com"; User = $false }
    @{ Local = "list-exclude.txt"; Remote = "$repoLists/list-exclude.txt"; Stub = "# Добавьте сюда домены-исключения, например: example.com"; User = $false }
    @{ Local = "list-general-user.txt"; Remote = ""; Stub = "# Добавьте сюда ваши домены, например: example.com"; User = $true }
    @{ Local = "list-exclude-user.txt"; Remote = ""; Stub = "# Добавьте сюда ваши домены-исключения, например: example.com"; User = $true }
)

foreach ($entry in $allLists) {
    $localPath = Join-Path $listsDir $entry.Local

    # ── Пользовательский файл: создаём заглушку если нет, не трогаем если есть ──
    if ($entry.User) {
        if (-not (Test-Path $localPath)) {
            [IO.File]::WriteAllLines($localPath, @($entry.Stub), [Text.UTF8Encoding]::new($false))
            Write-OK "Создан: $($entry.Local) (пользовательский файл — заглушка)"
        }
        else {
            Write-Info "Пользовательский: $($entry.Local) ($((Get-Content $localPath).Count) строк) — не изменяется"
        }
        continue
    }

    # ── Файл без Remote: создаём заглушку если нет ──────────────────────────────
    if ($entry.Remote -eq "") {
        if (-not (Test-Path $localPath)) {
            [IO.File]::WriteAllLines($localPath, @($entry.Stub), [Text.UTF8Encoding]::new($false))
            Write-OK "Создан: $($entry.Local)"
        }
        continue
    }

    # ── Файл с Remote: скачиваем и мержим ───────────────────────────────────────
    try {
        $resp = Invoke-WebRequest -Uri $entry.Remote -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
        if ($resp.StatusCode -eq 200) {
            $remoteLines = ($resp.Content -split "`r?`n")
            $before = if (Test-Path $localPath) { (Get-Content $localPath -Encoding UTF8).Count } else { 0 }
            $merged = Merge-ListFile -FilePath $localPath -NewLines $remoteLines
            [IO.File]::WriteAllLines($localPath, $merged, [Text.UTF8Encoding]::new($false))

            $kept = $merged.Count - ($remoteLines | Where-Object { $_.Trim() -ne '' -and -not $_.TrimStart().StartsWith('#') }).Count
            $msg = "$($entry.Local): $before → $($merged.Count) строк"
            if ($kept -gt 0) { $msg += " (сохранено уникальных: $kept)" }
            Write-OK $msg
        }
        else {
            Write-Warn "$($entry.Local): сервер вернул $($resp.StatusCode), файл не обновлён"
        }
    }
    catch {
        Write-Warn "$($entry.Local): не удалось скачать ($_)"
        if (-not (Test-Path $localPath)) {
            # Файла нет вообще — создаём заглушку
            if ($entry.Stub -ne "") { [IO.File]::WriteAllLines($localPath, @($entry.Stub), [Text.UTF8Encoding]::new($false)) }
            Write-Info "$($entry.Local): создана заглушка (нет сети)"
        }
        else {
            Write-Info "$($entry.Local): используется существующий файл"
        }
    }
}

# Очистка ipset-файлов от записей которые winws не поддерживает
Write-Step "Очистка ipset-файлов (невалидные IP)"
Repair-IpsetFiles -ListsPath $listsDir

# 7. Обновление файла hosts
Write-Step "Обновление файла hosts"

$hostsFile = "$env:SystemRoot\System32\drivers\etc\hosts"
$hostsUrl = "$repoService/hosts"
$marker = "# --- zapret-discord-youtube ---"

try {
    $resp = Invoke-WebRequest -Uri $hostsUrl -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
    if ($resp.StatusCode -ne 200) { throw "HTTP $($resp.StatusCode)" }

    # Строки из репозитория (только непустые и не-комментарии)
    $repoLines = ($resp.Content -split "`r?`n") |
    Where-Object { $_.Trim() -ne '' -and -not $_.TrimStart().StartsWith('#') }

    # Читаем текущий hosts
    $currentHosts = Get-Content $hostsFile -Encoding UTF8 -ErrorAction Stop

    # Находим строки которых ещё нет в hosts
    $missing = $repoLines | Where-Object {
        $line = $_.Trim()
        -not ($currentHosts | Where-Object { $_.Trim() -eq $line })
    }

    if ($missing.Count -eq 0) {
        Write-OK "hosts: все записи zapret уже присутствуют"
    }
    else {
        # Проверяем наличие маркерного блока — если нет, добавляем его
        $hasMarker = $currentHosts | Where-Object { $_.Trim() -eq $marker }

        $appendLines = @()
        if (-not $hasMarker) {
            $appendLines += ""
            $appendLines += $marker
        }
        $appendLines += $missing

        # Дописываем в конец файла (без BOM)
        $addText = "`n" + ($appendLines -join "`n")
        [IO.File]::AppendAllText($hostsFile, $addText, [Text.UTF8Encoding]::new($false))
        Write-OK "hosts: добавлено $($missing.Count) новых записей zapret"
        $missing | ForEach-Object { Write-Info "  + $_" }
    }
}
catch {
    Write-Warn "hosts: не удалось обновить ($_)"
    Write-Info "Обновите вручную через service.bat -> пункт 8"
}

# 8. Проверка текущей доступности сайтов (без zapret)
Write-Step "Проверка доступности сайтов БЕЗ обхода"
Stop-Zapret

$checkTargets = @(
    @{ Name = "discord.com"; Type = "url"; Url = "https://discord.com"; ComputerName = "discord.com" }
    @{ Name = "youtube.com"; Type = "url"; Url = "https://www.youtube.com"; ComputerName = "www.youtube.com" }
    @{ Name = "gateway.discord"; Type = "url"; Url = "https://gateway.discord.gg"; ComputerName = "gateway.discord.gg" }
    @{ Name = "googlevideo.com"; Type = "ping"; Url = ""; ComputerName = "googlevideo.com" }
)

$alreadyOK = @()
$needBypass = @()

foreach ($t in $checkTargets) {
    $reachable = $false
    if ($t.Type -eq "url") { $reachable = Test-Url $t.Url }
    else { $reachable = Test-Ping $t.ComputerName }

    if ($reachable) {
        Write-OK "$($t.Name) — доступен без обхода"
        $alreadyOK += $t.Name
    }
    else {
        Write-Warn "$($t.Name) — НЕДОСТУПЕН (нужен обход)"
        $needBypass += $t.Name
    }
}

if ($needBypass.Count -eq 0) {
    Write-Host "`n" -NoNewline
    Write-OK "Все сайты доступны без обхода! Установка zapret может не понадобиться."
    $ans = Read-Host "   Всё равно продолжить установку и поиск лучшего конфига? (Y/N, по умолчанию N)"
    if ($ans -notmatch "^[Yy]") {
        Write-Host "`n  Установка отменена. Нажмите Enter для выхода." -ForegroundColor Cyan
        Read-Host; exit 0
    }
}

# 8. Получение списка конфигов
$batFiles = @(Get-BatFiles)
if ($batFiles.Count -eq 0) {
    Write-Err "Не найдены файлы general*.bat"
    Read-Host "`nНажмите Enter для выхода"; exit 1
}

Write-Host ""
Write-Host $('─' * 60) -ForegroundColor DarkGray
Write-Host "  Найдено конфигов: $($batFiles.Count)" -ForegroundColor Cyan
Write-Host $('─' * 60) -ForegroundColor DarkGray

# 9. Выбор режима установки
Write-Host @"

  Выберите режим:
    [1]  Быстрый тест — протестировать ВСЕ конфиги и выбрать лучший
    [2]  Ручной выбор конфига из списка
    [3]  Использовать рекомендуемый (FAKE TLS AUTO) без тестов
"@ -ForegroundColor Gray

$mode = Read-Host "  Введите номер (1/2/3)"

$chosenBat = $null

switch ($mode) {

    "1" {
        # ── ЗАПУСК ОРИГИНАЛЬНОГО ТЕСТИРОВЩИКА ────────────────────────────────────
        Write-Header "ТЕСТ КОНФИГОВ — оригинальный тестировщик"

        $testScript = Join-Path $PSScriptRoot "test zapret.ps1"
        if (-not (Test-Path $testScript)) {
            Write-Err "Файл 'test zapret.ps1' не найден в utils/"
            Read-Host; exit 1
        }

        Write-Info "Запускаем оригинальный тест всех конфигов..."
        Write-Info "Когда тест завершится — вернитесь сюда для выбора конфига."
        Write-Host ""

        # Запускаем тест в том же окне PowerShell и ждём завершения
        & powershell.exe -NoProfile -ExecutionPolicy Bypass `
            -File $testScript `
            -WorkingDirectory $rootDir

        # После теста: показываем нумерованный список конфигов для выбора
        Write-Host ""
        Write-Host $('─' * 60) -ForegroundColor DarkGray
        Write-Host "  Выберите конфиг для установки (по результатам теста):" -ForegroundColor Cyan
        Write-Host $('─' * 60) -ForegroundColor DarkGray
        for ($i = 0; $i -lt $batFiles.Count; $i++) {
            Write-Host ("  [" + ($i + 1) + "] " + $batFiles[$i].Name) -ForegroundColor Gray
        }
        Write-Host ""
        $pick = Read-Host "  Введите номер конфига"
        $idx = [int]$pick - 1
        if ($idx -ge 0 -and $idx -lt $batFiles.Count) {
            $chosenBat = $batFiles[$idx].FullName
            Write-OK "Выбран: $(Split-Path $chosenBat -Leaf)"
        }
        else {
            Write-Err "Неверный номер"
            Read-Host; exit 1
        }
    }


    "2" {
        # ── РУЧНОЙ ВЫБОР ────────────────────────────────────────────────────────
        Write-Host ""
        for ($i = 0; $i -lt $batFiles.Count; $i++) {
            Write-Host ("  [" + ($i + 1) + "] " + $batFiles[$i].Name) -ForegroundColor Gray
        }
        $pick = Read-Host "`n  Введите номер конфига"
        $idx = [int]$pick - 1
        if ($idx -ge 0 -and $idx -lt $batFiles.Count) {
            $chosenBat = $batFiles[$idx].FullName
        }
        else {
            Write-Err "Неверный номер"
            Read-Host; exit 1
        }
    }

    "3" {
        # ── РЕКОМЕНДУЕМЫЙ КОНФИГ ─────────────────────────────────────────────
        $preferred = @(
            "general (FAKE TLS AUTO).bat"
            "general (FAKE TLS AUTO ALT).bat"
            "general (ALT).bat"
            "general.bat"
        )
        foreach ($name in $preferred) {
            $fp = Join-Path $rootDir $name
            if (Test-Path $fp) { $chosenBat = $fp; break }
        }
        if (-not $chosenBat) { $chosenBat = $batFiles[0].FullName }
        Write-OK "Выбран конфиг: $(Split-Path $chosenBat -Leaf)"
    }

    default {
        Write-Err "Неверный ввод"
        Read-Host; exit 1
    }
}

# 10. Установка выбранного конфига как службы
Write-Step "Установка службы Windows: $(Split-Path $chosenBat -Leaf)"
$installed = Install-ZapretService -BatPath $chosenBat
if ($installed) { Write-OK "Служба zapret успешно установлена и запущена" }
else { Write-Warn "Служба установлена, но статус запуска неизвестен. Проверьте service.bat → пункт 3" }

# 11. Проверка доступности ПОСЛЕ установки
Write-Step "Проверка доступности С обходом (ждём 5 сек...)"
Start-Sleep -Seconds 5

$afterResults = @()
foreach ($t in $checkTargets) {
    $reachable = $false
    if ($t.Type -eq "url") { $reachable = Test-Url $t.Url }
    else { $reachable = Test-Ping $t.ComputerName }
    $afterResults += [PSCustomObject]@{ Name = $t.Name; OK = $reachable }
}

$okCount = ($afterResults | Where-Object { $_.OK }).Count
$failCount = ($afterResults | Where-Object { -not $_.OK }).Count

foreach ($r in $afterResults) {
    if ($r.OK) { Write-OK "$($r.Name) — доступен ✔" }
    else { Write-Warn "$($r.Name) — всё ещё недоступен" }
}

Write-Host ""
Write-Host $('═' * 60) -ForegroundColor DarkCyan
if ($okCount -gt $failCount) {
    Write-Host "  ✔  ZAPRET РАБОТАЕТ! Доступно $okCount из $($afterResults.Count) ресурсов" -ForegroundColor Green
}
elseif ($okCount -gt 0) {
    Write-Host "  ⚠  Частичный результат: $okCount из $($afterResults.Count). Попробуйте другой конфиг." -ForegroundColor Yellow
}
else {
    Write-Host "  ✘  Ни один ресурс не стал доступен. Попробуйте режим 1 (авто-тест)." -ForegroundColor Red
}
Write-Host $('═' * 60) -ForegroundColor DarkCyan

Write-Host @"

  Команды для дальнейшей настройки:
    service.bat  — менеджер службы (меню)
    service.bat  → пункт 10 — диагностика
    service.bat  → пункт 11 — тест всех конфигов
"@ -ForegroundColor DarkGray

Write-Host "`n  Нажмите любую клавишу для выхода..." -ForegroundColor Cyan
[void][System.Console]::ReadKey($true)
