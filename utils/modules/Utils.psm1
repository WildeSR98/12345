#requires -Version 5.1
# =============================================================================
#  Utils.psm1  |  Ядро утилит: логирование, консоль, проверки, прогресс
# =============================================================================

$script:LogFile = $null
$script:VerboseEnabled = $false
$script:SilentMode = $false

# -- Инициализация логгера -----------------------------------------------------
function Initialize-Logger {
    param(
        [string]$RootDir,
        [switch]$Verbose,
        [switch]$Silent
    )
    $script:VerboseEnabled = $Verbose
    $script:SilentMode = $Silent

    $logDir = Join-Path $RootDir "logs"
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

    $timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $script:LogFile = Join-Path $logDir "zapret_$timestamp.log"

    Write-Log -Level "INFO" -Message "=== ZAPRET AUTO-SETUP started ==="
    Write-Log -Level "INFO" -Message "PowerShell: $($PSVersionTable.PSVersion) | OS: $($env:OS) | Admin: $(Test-Admin)"
    if ($Silent) { Write-Log -Level "INFO" -Message "Тихий режим включён" }
}

# -- Запись в лог-файл ---------------------------------------------------------
function Write-Log {
    param(
        [ValidateSet("INFO", "WARN", "ERROR", "DEBUG", "STEP", "OK")]
        [string]$Level = "INFO",
        [string]$Message
    )
    if (-not $script:LogFile) { return }
    $time = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$time] [$Level] $Message"
    try {
        Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch {}
}

# -- Консольные хелперы --------------------------------------------------------
function Write-Header {
    param([string]$Title)
    Write-Log -Level "STEP" -Message "HEADER: $Title"
    if ($script:SilentMode) { return }
    $line = "=" * 60
    Write-Host "`n$line" -ForegroundColor DarkCyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host $line -ForegroundColor DarkCyan
}

function Write-Step {
    param([string]$Title)
    Write-Log -Level "STEP" -Message $Title
    if ($script:SilentMode) { return }
    Write-Host "`n>>  $Title" -ForegroundColor Yellow
}

function Write-OK {
    param([string]$Message)
    Write-Log -Level "OK" -Message $Message
    if ($script:SilentMode) { return }
    Write-Host "   [OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Log -Level "WARN" -Message $Message
    if ($script:SilentMode) { return }
    Write-Host "   [ВНИМ] $Message" -ForegroundColor Yellow
}

function Write-Err {
    param([string]$Message)
    Write-Log -Level "ERROR" -Message $Message
    if ($script:SilentMode) { return }
    Write-Host "   [ОШИБ] $Message" -ForegroundColor Red
}

function Write-Info {
    param([string]$Message)
    Write-Log -Level "INFO" -Message $Message
    if ($script:SilentMode) { return }
    Write-Host "   [ИНФО] $Message" -ForegroundColor Gray
}

function Write-VerboseLog {
    param([string]$Message)
    Write-Log -Level "DEBUG" -Message $Message
    if (-not $script:VerboseEnabled -or $script:SilentMode) { return }
    Write-Host "   [V] $Message" -ForegroundColor DarkGray
}

# -- Прогресс ------------------------------------------------------------------
function Show-Progress {
    param(
        [string]$Activity = "Zapret",
        [string]$Status = "Working...",
        [int]$PercentComplete = -1
    )
    $pctStr = "$PercentComplete pct"
    Write-Log -Level "INFO" -Message "PROGRESS: $Activity - $Status ($pctStr)"
    if ($script:SilentMode) { return }
    Write-Progress -Activity $Activity -Status $Status -PercentComplete $PercentComplete
}

function Hide-Progress {
    if (-not $script:SilentMode) { Write-Progress -Activity "Done" -Completed }
}

# -- Проверка прав администратора ----------------------------------------------
function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# -- Определение PowerShell ----------------------------------------------------
function Get-PowerShellPath {
    $pwsh = Get-Command "pwsh.exe" -ErrorAction SilentlyContinue
    if ($pwsh) { return $pwsh.Source }
    $ps = Get-Command "powershell.exe" -ErrorAction SilentlyContinue
    if ($ps) { return $ps.Source }
    return "powershell.exe"
}

function Test-PowerShell7 {
    return ($PSVersionTable.PSVersion.Major -ge 7)
}

# -- Хэширование ---------------------------------------------------------------
function Get-FileHash256 {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    return (Get-FileHash -Path $Path -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
}

function Test-FileHash {
    param([string]$Path, [string]$ExpectedHash)
    if (-not $ExpectedHash) { return $true }
    $actual = Get-FileHash256 -Path $Path
    return ($actual -and $actual -eq $ExpectedHash.ToUpper())
}

# -- Безопасная очистка временных файлов ---------------------------------------
function Invoke-SafeTemp {
    param([scriptblock]$ScriptBlock, [string[]]$TempPaths)
    try { return & $ScriptBlock }
    finally {
        foreach ($p in $TempPaths) {
            if (Test-Path $p) {
                try {
                    Remove-Item -Path $p -Recurse -Force -ErrorAction SilentlyContinue
                    Write-VerboseLog "Очищен temp: $p"
                } catch {
                    Write-Log -Level "WARN" -Message "Не удалось очистить temp $p : $_"
                }
            }
        }
    }
}

# -- Бэкап и откат -------------------------------------------------------------
function New-Backup {
    param(
        [string]$SourceDir,
        [string[]]$IncludePatterns = @("bin\*", "*.bat", "utils\*", "lists\*"),
        [string[]]$ExcludePatterns = @("*-user*", "autosetup.ps1", "logs\*", "backups\*")
    )
    $backupDir = Join-Path $SourceDir "backups"
    if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }

    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $backupName = "backup_$timestamp"
    $backupPath = Join-Path $backupDir $backupName
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null

    Write-VerboseLog "Создание бэкапа: $backupName"

    foreach ($pattern in $IncludePatterns) {
        $files = Get-ChildItem -Path (Join-Path $SourceDir $pattern) -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                $f = $_.FullName
                $excluded = $false
                foreach ($ex in $ExcludePatterns) { if ($f -like "*$ex*") { $excluded = $true; break } }
                -not $excluded
            }
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($SourceDir.Length + 1)
            $dest = Join-Path $backupPath $relative
            $destDir = Split-Path $dest -Parent
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            Copy-Item $file.FullName -Destination $dest -Force
        }
    }

    Write-Log -Level "INFO" -Message "Бэкап создан: $backupPath"
    return $backupPath
}

function Restore-Backup {
    param([string]$BackupPath, [string]$TargetDir)
    if (-not (Test-Path $BackupPath)) {
        Write-Err "Бэкап не найден: $BackupPath"
        return $false
    }
    try {
        Get-ChildItem $BackupPath -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($BackupPath.Length + 1)
            $dest = Join-Path $TargetDir $relative
            $destDir = Split-Path $dest -Parent
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            Copy-Item $_.FullName -Destination $dest -Force
        }
        Write-Log -Level "INFO" -Message "Бэкап восстановлен: $BackupPath"
        return $true
    }
    catch {
        Write-Err "Восстановление бэкапа не удалось: $_"
        return $false
    }
}

function Remove-OldBackups {
    param([string]$BackupDir, [int]$KeepCount = 5)
    $backups = Get-ChildItem $BackupDir -Directory -ErrorAction SilentlyContinue | Sort-Object CreationTime -Descending
    if ($backups.Count -gt $KeepCount) {
        $toDelete = $backups | Select-Object -Skip $KeepCount
        foreach ($b in $toDelete) {
            Remove-Item $b.FullName -Recurse -Force -ErrorAction SilentlyContinue
            Write-VerboseLog "Удалён старый бэкап: $($b.Name)"
        }
    }
}

Export-ModuleMember -Function @(
    'Initialize-Logger',
    'Write-Log', 'Write-Header', 'Write-Step', 'Write-OK', 'Write-Warn', 'Write-Err', 'Write-Info', 'Write-VerboseLog',
    'Show-Progress', 'Hide-Progress',
    'Test-Admin', 'Get-PowerShellPath', 'Test-PowerShell7',
    'Get-FileHash256', 'Test-FileHash',
    'Invoke-SafeTemp',
    'New-Backup', 'Restore-Backup', 'Remove-OldBackups'
)
