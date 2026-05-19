#requires -Version 5.1
# =============================================================================
#  Update.psm1  |  GitHub updates, commits, transactions, hashes
# =============================================================================

Import-Module "$PSScriptRoot\Utils.psm1" -Force -Global
Import-Module "$PSScriptRoot\Service.psm1" -Force -Global

# -- Local version from service.bat --------------------------------------------
function Get-LocalVersion {
    param([string]$ServiceBatPath)
    if (-not (Test-Path $ServiceBatPath)) { return "0.0.0" }
    $line = Get-Content $ServiceBatPath | Select-String 'LOCAL_VERSION=(.+)' | Select-Object -First 1
    if ($line -and $line.Matches[0].Groups[1].Value) {
        return $line.Matches[0].Groups[1].Value.Trim().Trim('"')
    }
    return "0.0.0"
}

# -- GitHub API: latest release ------------------------------------------------
function Get-LatestRelease {
    param([string]$Repo = "Flowseal/zapret-discord-youtube")
    $apiUrl = "https://api.github.com/repos/$Repo/releases/latest"
    try {
        $headers = @{ "User-Agent" = "zapret-autosetup/2.1" }
        $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -TimeoutSec 10 -ErrorAction Stop
        $zipAsset = $release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
        return [PSCustomObject]@{
            Version     = $release.tag_name -replace '^v', ''
            ZipUrl      = if ($zipAsset) { $zipAsset.browser_download_url } else { $null }
            ReleasePage = $release.html_url
            Published   = $release.published_at
        }
    }
    catch {
        Write-Log -Level "WARN" -Message "GitHub API error: $($_.Exception.Message)"
        return $null
    }
}

# -- GitHub API: latest commit from 12345 --------------------------------------
function Get-LatestCommit12345 {
    param([string]$Repo = "WildeSR98/12345")
    $apiUrl = "https://api.github.com/repos/$Repo/commits?per_page=1"
    try {
        $headers = @{ "User-Agent" = "zapret-autosetup/2.1" }
        $commits = Invoke-RestMethod -Uri $apiUrl -Headers $headers -TimeoutSec 10 -ErrorAction Stop
        if ($commits -and $commits.Count -gt 0) { return $commits[0].sha }
        return $null
    }
    catch {
        Write-Log -Level "WARN" -Message "GitHub commit check error: $($_.Exception.Message)"
        return $null
    }
}

# -- Download file with optional SHA256 check ----------------------------------
function Download-FileWithHash {
    param(
        [string]$Uri,
        [string]$OutFile,
        [string]$ExpectedHash = $null,
        [int]$TimeoutSec = 60
    )
    Write-VerboseLog "Downloading $Uri -> $OutFile"
    try {
        Invoke-WebRequest -Uri $Uri -OutFile $OutFile -TimeoutSec $TimeoutSec -UseBasicParsing -ErrorAction Stop
    }
    catch {
        Write-Err "Download failed: $($_.Exception.Message)"
        return $false
    }
    if ($ExpectedHash) {
        $actual = Get-FileHash256 -Path $OutFile
        if (-not $actual -or $actual -ne $ExpectedHash.ToUpper()) {
            Write-Err "Hash mismatch! Expected: $ExpectedHash | Got: $actual"
            Remove-Item $OutFile -Force -ErrorAction SilentlyContinue
            return $false
        }
        Write-VerboseLog "Hash verified: $actual"
    }
    return $true
}

# -- Transactional Zapret core update ------------------------------------------
function Invoke-AutoUpdate {
    param(
        [string]$ZipUrl,
        [string]$TargetDir,
        [string]$ListsDir,
        [string]$ExpectedHash = $null
    )
    $tmpZip = Join-Path $env:TEMP "zapret_update_$(Get-Random).zip"
    $tmpDir = Join-Path $env:TEMP "zapret_update_extracted_$(Get-Random)"

    return Invoke-SafeTemp -TempPaths @($tmpZip, $tmpDir) -ScriptBlock {
        Show-Progress -Activity "Auto-Update" -Status "Downloading..." -PercentComplete 10
        if (-not (Download-FileWithHash -Uri $ZipUrl -OutFile $tmpZip -ExpectedHash $ExpectedHash -TimeoutSec 60)) {
            return $false
        }
        Show-Progress -Activity "Auto-Update" -Status "Extracting..." -PercentComplete 30
        try {
            Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force -ErrorAction Stop
        }
        catch {
            Write-Err "Extraction failed: $($_.Exception.Message)"
            return $false
        }
        $extractedRoot = $tmpDir
        $children = Get-ChildItem $tmpDir
        if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
            $extractedRoot = $children[0].FullName
        }
        Show-Progress -Activity "Auto-Update" -Status "Creating backup..." -PercentComplete 40
        $backupPath = New-Backup -SourceDir $TargetDir
        Show-Progress -Activity "Auto-Update" -Status "Stopping services..." -PercentComplete 50
        Stop-ZapretService
        Show-Progress -Activity "Auto-Update" -Status "Updating files..." -PercentComplete 60
        try {
            $newBin = Join-Path $extractedRoot "bin"
            if (Test-Path $newBin) {
                Copy-Item "$newBin\*" -Destination (Join-Path $TargetDir "bin") -Recurse -Force -ErrorAction Stop
                Write-OK "bin/ updated"
            }
            Get-ChildItem $extractedRoot -Filter "*.bat" |
                Where-Object { $_.Name -notlike "autosetup*" } |
                ForEach-Object {
                    Copy-Item $_.FullName -Destination (Join-Path $TargetDir $_.Name) -Force -ErrorAction Stop
                }
            Write-OK ".bat strategies updated"
            $newUtils = Join-Path $extractedRoot "utils"
            if (Test-Path $newUtils) {
                Get-ChildItem $newUtils |
                    Where-Object { $_.Name -notlike "autosetup*" -and $_.Name -notlike "modules" } |
                    ForEach-Object {
                        $destDir = Join-Path $TargetDir "utils"
                        if ($_.PSIsContainer) {
                            Copy-Item $_.FullName -Destination (Join-Path $destDir $_.Name) -Force -Recurse -ErrorAction Stop
                        } else {
                            Copy-Item $_.FullName -Destination (Join-Path $destDir $_.Name) -Force -ErrorAction Stop
                        }
                    }
                Write-OK "utils/ updated"
            }
            $newLists = Join-Path $extractedRoot "lists"
            if (Test-Path $newLists -and $ListsDir) {
                Import-Module "$PSScriptRoot\Lists.psm1" -Force -Global
                Get-ChildItem $newLists -Filter "*.txt" |
                    Where-Object { $_.Name -notlike "*-user*" } |
                    ForEach-Object {
                        $localFile = Join-Path $ListsDir $_.Name
                        $remoteLines = Get-Content $_.FullName -Encoding UTF8
                        $merged = Merge-ListFile -FilePath $localFile -NewLines $remoteLines
                        [IO.File]::WriteAllLines($localFile, $merged, [Text.UTF8Encoding]::new($false))
                        Write-OK "$($_.Name): merged ($($merged.Count) lines)"
                    }
                Repair-ListFiles -ListsPath $ListsDir
            }
            $newService = Join-Path $extractedRoot "service.bat"
            if (Test-Path $newService) {
                Copy-Item $newService -Destination (Join-Path $TargetDir "service.bat") -Force
                Write-OK "service.bat updated"
            }
            Show-Progress -Activity "Auto-Update" -Status "Done" -PercentComplete 100
            Remove-OldBackups -BackupDir (Join-Path $TargetDir "backups")
            return $true
        }
        catch {
            Write-Err "Update failed: $($_.Exception.Message). Restoring backup..."
            Restore-Backup -BackupPath $backupPath -TargetDir $TargetDir
            return $false
        }
        finally {
            Hide-Progress
        }
    }
}

# -- Transactional 12345 update ------------------------------------------------
function Invoke-AutoUpdate12345 {
    param([string]$TargetDir, [string]$Repo = "WildeSR98/12345")
    $zipUrl = "https://github.com/$Repo/archive/refs/heads/main.zip"
    $tmpZip = Join-Path $env:TEMP "12345_update_$(Get-Random).zip"
    $tmpDir = Join-Path $env:TEMP "12345_update_extracted_$(Get-Random)"

    return Invoke-SafeTemp -TempPaths @($tmpZip, $tmpDir) -ScriptBlock {
        Write-Info "Downloading 12345 archive..."
        if (-not (Download-FileWithHash -Uri $zipUrl -OutFile $tmpZip -TimeoutSec 60)) {
            return $false
        }
        Write-Info "Extracting 12345..."
        try {
            Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force -ErrorAction Stop
        }
        catch {
            Write-Err "Extraction failed: $($_.Exception.Message)"
            return $false
        }
        $extractedRoot = $tmpDir
        $children = Get-ChildItem $tmpDir
        if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
            $extractedRoot = $children[0].FullName
        }
        $backupPath = New-Backup -SourceDir $TargetDir
        Write-Info "Stopping services before update..."
        Stop-ZapretService
        try {
            Get-ChildItem $extractedRoot -Recurse |
                Where-Object { $_.FullName -notmatch '\\\.github\\' -and $_.FullName -notmatch '\\\.git\\' } |
                ForEach-Object {
                    $relativePath = $_.FullName.Substring($extractedRoot.Length + 1)
                    $destPath = Join-Path $TargetDir $relativePath
                    if ($_.PSIsContainer) {
                        if (-not (Test-Path $destPath)) { New-Item -ItemType Directory -Path $destPath | Out-Null }
                    } else {
                        Copy-Item $_.FullName -Destination $destPath -Force
                    }
                }
            Write-OK "12345 files updated"
            Remove-OldBackups -BackupDir (Join-Path $TargetDir "backups")
            return $true
        }
        catch {
            Write-Err "12345 update failed: $($_.Exception.Message). Restoring backup..."
            Restore-Backup -BackupPath $backupPath -TargetDir $TargetDir
            return $false
        }
    }
}

# -- Background update checker -------------------------------------------------
function Start-BackgroundUpdateCheck {
    param(
        [string]$RootDir,
        [string]$ListsDir,
        [string]$PSScriptRoot,
        [string]$ServiceBatPath
    )
    $flagDir = Join-Path $RootDir "utils\update_cache"
    if (-not (Test-Path $flagDir)) { New-Item -ItemType Directory -Path $flagDir -Force | Out-Null }

    Start-Job -Name "ZapretUpdateCheck" -ScriptBlock {
        param($rootDir, $listsDir, $psScriptRoot, $serviceBatPath, $flagDir)
        try {
            Import-Module "$psScriptRoot\modules\Utils.psm1" -Force -Global
            Import-Module "$psScriptRoot\modules\Update.psm1" -Force -Global

            $cacheFile = Join-Path $flagDir "pending.json"
            $pending = $null

            # Check 12345 scripts
            $localCommitFile = Join-Path $psScriptRoot "12345_version.txt"
            $localCommit = if (Test-Path $localCommitFile) { (Get-Content $localCommitFile).Trim() } else { "none" }
            $remoteCommit = Get-LatestCommit12345
            if ($remoteCommit -and $localCommit -ne $remoteCommit) {
                $pending = @{
                    type = "12345"
                    version = $remoteCommit.Substring(0, [Math]::Min(7, $remoteCommit.Length))
                    commit = $remoteCommit
                    timestamp = (Get-Date).ToString("o")
                }
            }

            # Check zapret core (only if no 12345 update found)
            if (-not $pending) {
                $localVersion = Get-LocalVersion -ServiceBatPath $serviceBatPath
                $latest = Get-LatestRelease
                if ($latest -and $localVersion -ne $latest.Version -and $latest.ZipUrl) {
                    $zipPath = Join-Path $flagDir "zapret_update.zip"
                    if (Download-FileWithHash -Uri $latest.ZipUrl -OutFile $zipPath -TimeoutSec 120) {
                        $pending = @{
                            type = "zapret"
                            version = $latest.Version
                            zipPath = $zipPath
                            releasePage = $latest.ReleasePage
                            timestamp = (Get-Date).ToString("o")
                        }
                    }
                }
            }

            if ($pending) {
                $pending | ConvertTo-Json -Depth 3 | Out-File $cacheFile -Encoding UTF8 -Force
            } else {
                if (Test-Path $cacheFile) { Remove-Item $cacheFile -Force }
            }
        }
        catch {
            # Silent failure — background job should never crash the main script
        }
    } -ArgumentList $RootDir, $ListsDir, $PSScriptRoot, $ServiceBatPath, $flagDir | Out-Null
}

function Get-PendingUpdate {
    param([string]$RootDir)
    $cacheFile = Join-Path $RootDir "utils\update_cache\pending.json"
    if (Test-Path $cacheFile) {
        try {
            $data = Get-Content $cacheFile -Raw -Encoding UTF8 | ConvertFrom-Json
            $ts = [DateTime]::Parse($data.timestamp, $null, [System.Globalization.DateTimeStyles]::RoundtripKind)
            $age = (Get-Date) - $ts
            if ($age.TotalHours -lt 24) {
                return $data
            }
            Remove-Item $cacheFile -Force -ErrorAction SilentlyContinue
        }
        catch {}
    }
    return $null
}

function Install-PendingUpdate {
    param(
        [string]$RootDir,
        [string]$ListsDir,
        [string]$PSScriptRoot
    )
    $pending = Get-PendingUpdate -RootDir $RootDir
    if (-not $pending) { return $false }

    $flagDir = Join-Path $RootDir "utils\update_cache"
    $cacheFile = Join-Path $flagDir "pending.json"

    try {
        if ($pending.type -eq "12345") {
            Write-Info "Обновление скриптов 12345..."
            $ok = Invoke-AutoUpdate12345 -TargetDir $RootDir
            if ($ok -and $pending.commit) {
                $commitFile = Join-Path $PSScriptRoot "12345_version.txt"
                $pending.commit | Out-File $commitFile -Encoding ascii
            }
        }
        elseif ($pending.type -eq "zapret") {
            if ($pending.zipPath -and (Test-Path $pending.zipPath)) {
                Write-Info "Установка обновления zapret..."
                $ok = Invoke-AutoUpdate -ZipUrl $pending.zipPath -TargetDir $RootDir -ListsDir $ListsDir
            } else {
                Write-Warn "Кэш обновления повреждён, скачиваю заново..."
                $latest = Get-LatestRelease
                if ($latest -and $latest.ZipUrl) {
                    $ok = Invoke-AutoUpdate -ZipUrl $latest.ZipUrl -TargetDir $RootDir -ListsDir $ListsDir
                }
            }
        }

        if ($ok) {
            Remove-Item $cacheFile -Force -ErrorAction SilentlyContinue
            if (Test-Path $flagDir) {
                Get-ChildItem $flagDir -File | Remove-Item -Force -ErrorAction SilentlyContinue
            }
        }
        return $ok
    }
    catch {
        Write-Err "Ошибка установки обновления: $($_.Exception.Message)"
        return $false
    }
}

Export-ModuleMember -Function @(
    'Get-LocalVersion', 'Get-LatestRelease', 'Get-LatestCommit12345',
    'Download-FileWithHash',
    'Invoke-AutoUpdate', 'Invoke-AutoUpdate12345',
    'Start-BackgroundUpdateCheck', 'Get-PendingUpdate', 'Install-PendingUpdate'
)
