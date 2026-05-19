#requires -Version 5.1
# =============================================================================
#  Service.psm1  |  Управление службой zapret, WinDivert, Game Filter
# =============================================================================

Import-Module "$PSScriptRoot\Utils.psm1" -Force -Global

# -- Остановка winws.exe -------------------------------------------------------
function Stop-ZapretProcess {
    Get-Process -Name "winws" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Write-VerboseLog "winws.exe остановлен"
}

# -- Полная остановка и удаление служб zapret + WinDivert ---------------------
function Stop-ZapretService {
    Stop-ZapretProcess
    $services = @("zapret", "WinDivert", "WinDivert14")
    foreach ($svc in $services) {
        net stop $svc > $null 2>&1
        sc.exe delete $svc > $null 2>&1
        Write-VerboseLog "Служба удалена: $svc"
    }
}

# -- Проверка статуса службы ---------------------------------------------------
function Test-ServiceStatus {
    param([string]$ServiceName)
    $status = $null
    try {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc) { $status = $svc.Status.ToString() }
    } catch { }
    return $status
}

# -- Чтение Game Filter --------------------------------------------------------
function Get-GameFilterPorts {
    param([string]$UtilsDir)
    $flagFile = Join-Path $UtilsDir "game_filter.enabled"
    if (-not (Test-Path $flagFile)) { return @{ TCP = "12"; UDP = "12"; Mode = "disabled" } }

    $mode = (Get-Content $flagFile -TotalCount 1).Trim().ToLower()
    switch ($mode) {
        "all" { return @{ TCP = "1024-65535"; UDP = "1024-65535"; Mode = "all" } }
        "tcp" { return @{ TCP = "1024-65535"; UDP = "12"; Mode = "tcp" } }
        "udp" { return @{ TCP = "12"; UDP = "1024-65535"; Mode = "udp" } }
        default { return @{ TCP = "12"; UDP = "12"; Mode = "disabled" } }
    }
}

# -- Установка Game Filter -----------------------------------------------------
function Set-GameFilter {
    param(
        [string]$UtilsDir,
        [ValidateSet("disabled", "all", "tcp", "udp")]
        [string]$Mode
    )
    $flagFile = Join-Path $UtilsDir "game_filter.enabled"
    if ($Mode -eq "disabled") {
        if (Test-Path $flagFile) { Remove-Item $flagFile -Force }
    } else {
        $Mode | Out-File $flagFile -Encoding ascii -Force
    }
}

# -- Парсинг аргументов winws из .bat ------------------------------------------
function Get-WinwsArgs {
    param(
        [string]$BatPath,
        [string]$BinDir,
        [string]$ListsDir,
        [string]$UtilsDir
    )
    $BIN_PATH = $BinDir + "\"
    $LISTS_PATH = $ListsDir + "\"
    $filter = Get-GameFilterPorts -UtilsDir $UtilsDir

    $winwsArgs = ""
    $capture = $false

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

# -- Установка службы Windows --------------------------------------------------
function Install-ZapretService {
    param(
        [string]$BatPath,
        [string]$BinDir,
        [string]$ListsDir,
        [string]$UtilsDir,
        [string]$WinwsExe
    )

    $SRVCNAME = "zapret"
    $winwsArgs = Get-WinwsArgs -BatPath $BatPath -BinDir $BinDir -ListsDir $ListsDir -UtilsDir $UtilsDir

    # TCP timestamps
    netsh interface tcp set global timestamps=enabled > $null 2>&1
    Write-VerboseLog "TCP timestamps включены"

    # Останавливаем и удаляем старую службу
    net stop $SRVCNAME > $null 2>&1
    sc.exe delete $SRVCNAME > $null 2>&1
    Start-Sleep -Milliseconds 500

    # Создаём службу
    $createResult = sc.exe create $SRVCNAME `
        binPath= "`"$WinwsExe`" $winwsArgs" `
        DisplayName= "zapret" `
        start= auto 2>&1

    sc.exe description $SRVCNAME "Zapret DPI bypass software" > $null 2>&1
    $startResult = sc.exe start $SRVCNAME 2>&1

    $configName = [System.IO.Path]::GetFileNameWithoutExtension($BatPath)
    reg add "HKLM\System\CurrentControlSet\Services\zapret" /v zapret-discord-youtube /t REG_SZ /d "$configName" /f > $null 2>&1

    $success = ($LASTEXITCODE -eq 0 -or $startResult -match "1056")
    if ($success) {
        Write-Log -Level "OK" -Message "Служба установлена: $configName"
    } else {
        Write-Log -Level "ERROR" -Message "Ошибка установки службы: $createResult | $startResult"
    }
    return $success
}

# -- Получение текущей стратегии из реестра ------------------------------------
function Get-CurrentStrategyName {
    try {
        $val = Get-ItemProperty -Path "HKLM:\System\CurrentControlSet\Services\zapret" -Name "zapret-discord-youtube" -ErrorAction SilentlyContinue
        if ($val) { return $val."zapret-discord-youtube" }
    } catch { }
    return $null
}

# -- Загрузка пользовательских списков-заглушек --------------------------------
function Initialize-UserLists {
    param([string]$ListsDir)
    $defaults = @{
        "ipset-exclude-user.txt" = "203.0.113.113/32"
        "list-general-user.txt"  = "domain.example.abc"
        "list-exclude-user.txt"  = "domain.example.abc"
    }
    foreach ($file in $defaults.Keys) {
        $path = Join-Path $ListsDir $file
        if (-not (Test-Path $path)) {
            $defaults[$file] | Out-File $path -Encoding UTF8 -Force
            Write-VerboseLog "Создан пользовательский список: $file"
        }
    }
}

Export-ModuleMember -Function @(
    'Stop-ZapretProcess', 'Stop-ZapretService', 'Test-ServiceStatus',
    'Get-GameFilterPorts', 'Set-GameFilter',
    'Get-WinwsArgs', 'Install-ZapretService', 'Get-CurrentStrategyName',
    'Initialize-UserLists'
)
