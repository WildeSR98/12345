#requires -Version 5.1
# =============================================================================
#  Strategy.psm1  |  BAT file discovery, strategy testing, selection
# =============================================================================

Import-Module "$PSScriptRoot\Utils.psm1" -Force
Import-Module "$PSScriptRoot\Diagnostics.psm1" -Force
Import-Module "$PSScriptRoot\Service.psm1" -Force

# -- Get list of strategy BAT files --------------------------------------------
function Get-BatFiles {
    param([string]$RootDir)
    return Get-ChildItem -Path $RootDir -Filter "general*.bat" |
        Sort-Object { [Regex]::Replace($_.Name, '(\d+)', { $args[0].Value.PadLeft(8, '0') }) }
}

# -- Test all strategies and return sorted results -----------------------------
function Invoke-StrategyTest {
    param(
        [string]$RootDir,
        [string]$ListsDir
    )
    $binDir    = Join-Path $RootDir "bin"
    $winwsExe  = Join-Path $binDir "winws.exe"
    $utilsDir  = Join-Path $RootDir "utils"
    $configPath = Join-Path $RootDir "config.json"

    if (-not (Test-Path $winwsExe)) {
        Write-Err "winws.exe not found"
        return $null
    }

    $targets = @()
    if (Test-Path $configPath) {
        try {
            $cfg = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($cfg.diagnostics -and $cfg.diagnostics.check_targets) {
                $targets = @($cfg.diagnostics.check_targets) | ForEach-Object {
                    [PSCustomObject]@{
                        Name = $_.name
                        Type = $_.type
                        Url  = if ($_.url) { $_.url } else { "" }
                        Host = if ($_.host) { $_.host } else { "" }
                    }
                }
            }
        }
        catch {}
    }
    if ($targets.Count -eq 0) {
        $targets = @(
            [PSCustomObject]@{ Name="discord.com"; Type="url"; Url="https://discord.com"; Host="discord.com" },
            [PSCustomObject]@{ Name="youtube.com"; Type="url"; Url="https://www.youtube.com"; Host="www.youtube.com" }
        )
    }

    $batFiles = @(Get-BatFiles -RootDir $RootDir)
    if ($batFiles.Count -eq 0) {
        Write-Err "No general*.bat files found"
        return $null
    }

    $results = @()
    $total = $batFiles.Count
    $idx = 0

    Write-Host ""
    Write-Host ("  " + "=" * 54) -ForegroundColor DarkCyan
    Write-Host "  TESTING STRATEGIES ($total configs)" -ForegroundColor Cyan
    Write-Host ("  " + "=" * 54) -ForegroundColor DarkCyan
    Write-Host ""

    foreach ($bat in $batFiles) {
        $idx++
        $pct = [math]::Round(($idx / $total) * 100)
        Write-Progress -Activity "Testing strategies" -Status $bat.Name -PercentComplete $pct

        Write-Host "  [$idx/$total] $($bat.Name)..." -NoNewline
        try {
            $wArgs = Get-WinwsArgs -BatPath $bat.FullName -BinDir $binDir -ListsDir $listsDir -UtilsDir $utilsDir
            $res = Invoke-ConfigTest -ConfigName $bat.Name -ConfigPath $bat.FullName -WinwsExe $winwsExe -WinwsArgs $wArgs -Targets $targets
            $results += $res
            $color = if ($res.OK -gt 0) { "Green" } else { "Red" }
            Write-Host " OK=$($res.OK) Fail=$($res.Fail)" -ForegroundColor $color
        }
        catch {
            Write-Host " ERROR: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    Write-Progress -Activity "Testing strategies" -Completed
    Write-Host ""

    if ($results.Count -eq 0) { return $null }

    $sorted = $results | Sort-Object { $_.Score } -Descending
    $best = $sorted | Select-Object -First 1

    return [PSCustomObject]@{
        Sorted = $sorted
        Best   = $best
    }
}

# -- Alias for mode=5 entry point ----------------------------------------------
function Invoke-StrategyTestSuite {
    param([string]$RootDir, [string]$ListsDir)
    $result = Invoke-StrategyTest -RootDir $RootDir -ListsDir $ListsDir
    if (-not $result) { return $null }
    return Select-StrategyFromResults -Sorted $result.Sorted -BestConfig $result.Best.Config
}

# -- Show results and let user pick --------------------------------------------
function Select-StrategyFromResults {
    param(
        [array]$Sorted,
        [string]$BestConfig
    )
    Write-Host ("  " + "-" * 54) -ForegroundColor DarkGray
    Write-Host "  TEST RESULTS" -ForegroundColor Cyan
    Write-Host ("  " + "-" * 54) -ForegroundColor DarkGray
    Write-Host ""

    for ($i = 0; $i -lt $Sorted.Count; $i++) {
        $r = $Sorted[$i]
        $marker = if ($r.Config -eq $BestConfig) { " => BEST" } else { "" }
        $color = if ($r.OK -gt 0) { "White" } else { "DarkGray" }
        Write-Host ("  [{0}] {1}  OK={2} Fail={3}{4}" -f ($i+1), $r.Config, $r.OK, $r.Fail, $marker) -ForegroundColor $color
    }
    Write-Host ""

    $pick = Read-Host "  Install best (Enter) or pick number"
    if ($pick -eq "" -or $pick -eq "1") {
        $match = $Sorted | Where-Object { $_.Config -eq $BestConfig } | Select-Object -First 1
        if ($match) { return $match.FullPath }
    }
    $idx = [int]$pick - 1
    if ($idx -ge 0 -and $idx -lt $Sorted.Count) {
        return $Sorted[$idx].FullPath
    }
    Write-Warn "Invalid choice"
    return $null
}

Export-ModuleMember -Function @(
    'Get-BatFiles',
    'Invoke-StrategyTest',
    'Invoke-StrategyTestSuite',
    'Select-StrategyFromResults'
)
