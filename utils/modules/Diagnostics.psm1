#requires -Version 5.1
# =============================================================================
#  Diagnostics.psm1  |  Tests, conflicts, DPI checks, reports
# =============================================================================

Import-Module "$PSScriptRoot\Utils.psm1" -Global -ErrorAction SilentlyContinue
Import-Module "$PSScriptRoot\Service.psm1" -Global -ErrorAction SilentlyContinue

# -- URL test via curl ---------------------------------------------------------
function Test-Url {
    param([string]$Url, [int]$Timeout = 5)
    if (-not (Get-Command "curl.exe" -ErrorAction SilentlyContinue)) { return $false }
    $code = & curl.exe -s -o NUL -w "%{http_code}" -m $Timeout --max-redirs 3 $Url 2>$null
    return ($LASTEXITCODE -eq 0 -and $code -match "^[23]")
}

# -- Ping ----------------------------------------------------------------------
function Test-Ping {
    param([string]$ComputerName, [int]$Count = 2)
    try {
        if (Test-PowerShell7) {
            $r = Test-Connection -TargetName $ComputerName -Count $Count -ErrorAction Stop -TimeoutSeconds 3
        } else {
            $r = Test-Connection -ComputerName $ComputerName -Count $Count -ErrorAction Stop
        }
        return [bool]($r | Where-Object { $_.StatusCode -eq 0 -or $_.Status -eq 'Success' -or $_.PingIsSuccess })
    }
    catch { return $false }
}

# -- Pre-install accessibility check -------------------------------------------
function Test-PreInstallAccess {
    $targets = @(
        @{ Name = "discord.com"; Type = "url"; Url = "https://discord.com"; Host = "discord.com" }
        @{ Name = "youtube.com"; Type = "url"; Url = "https://www.youtube.com"; Host = "www.youtube.com" }
        @{ Name = "gateway.discord"; Type = "url"; Url = "https://gateway.discord.gg"; Host = "gateway.discord.gg" }
        @{ Name = "googlevideo.com"; Type = "ping"; Url = ""; Host = "googlevideo.com" }
    )
    $results = @()
    foreach ($t in $targets) {
        $ok = if ($t.Type -eq "url") { Test-Url $t.Url } else { Test-Ping $t.Host }
        $results += [PSCustomObject]@{ Name = $t.Name; Reachable = $ok; Type = $t.Type }
        $name = $t.Name
        if ($ok) { Write-OK "$name - доступен без обхода" }
        else { Write-Warn "$name - НЕДОСТУПЕН (нужен обход)" }
    }
    return $results
}

# -- Post-install accessibility check ------------------------------------------
function Test-PostInstallAccess {
    Start-Sleep -Seconds 5
    $results = Test-PreInstallAccess
    $okCount = ($results | Where-Object { $_.Reachable }).Count
    $total = $results.Count
    Write-Host ""
    Write-Host $("=" * 60) -ForegroundColor DarkCyan
    if ($okCount -gt $total / 2) {
        Write-Host "  [OK] ZAPRET РАБОТАЕТ! $okCount из $total ресурсов доступны" -ForegroundColor Green
    } elseif ($okCount -gt 0) {
        Write-Host "  [ВНИМ] Частично: $okCount из $total. Попробуйте другую стратегию." -ForegroundColor Yellow
    } else {
        Write-Host "  [ОШИБ] Ни один ресурс недоступен. Попробуйте режим 1 (авто-тест)." -ForegroundColor Red
    }
    Write-Host $("=" * 60) -ForegroundColor DarkCyan
    return $results
}

# -- Conflict detection --------------------------------------------------------
function Test-Conflicts {
    $conflicts = @("GoodbyeDPI", "discordfix_zapret", "winws1", "winws2")
    $found = @()
    foreach ($svc in $conflicts) {
        if (Get-Service -Name $svc -ErrorAction SilentlyContinue) { $found += $svc }
    }
    return $found
}

function Remove-Conflicts {
    param([string[]]$Services)
    foreach ($svc in $Services) {
        net stop $svc > $null 2>&1
        sc.exe delete $svc > $null 2>&1
        Write-OK "Удалена конфликтующая служба: $svc"
    }
    net stop WinDivert > $null 2>&1; sc.exe delete WinDivert > $null 2>&1
    net stop WinDivert14 > $null 2>&1; sc.exe delete WinDivert14 > $null 2>&1
}

# -- DPI suite download --------------------------------------------------------
function Get-DpiSuite {
    try {
        $suite = Invoke-RestMethod -Uri "https://hyperion-cs.github.io/dpi-checkers/ru/tcp-16-20/suite.json" -TimeoutSec 8 -ErrorAction Stop
        return $suite | ForEach-Object { $_.url }
    }
    catch {
        Write-VerboseLog "DPI suite download failed"
        return @()
    }
}

# -- DPI check (16-20 KB freeze) -----------------------------------------------
function Invoke-DpiCheck {
    param(
        [string[]]$DpiUrls,
        [int]$RangeBytes = 20480,
        [int]$WarnMinKB = 16,
        [int]$WarnMaxKB = 20,
        [int]$TimeoutSec = 5,
        [int]$MaxUrls = 8
    )
    if (-not (Get-Command "curl.exe" -ErrorAction SilentlyContinue)) { return -1 }
    if ($DpiUrls.Count -eq 0) { return -1 }
    $rangeSpec = "0-$($RangeBytes - 1)"
    $protocols = @(
        @{ Label = "HTTP"; Args = @("--http1.1") }
        @{ Label = "TLS1.2"; Args = @("--tlsv1.2", "--tls-max", "1.2") }
        @{ Label = "TLS1.3"; Args = @("--tlsv1.3", "--tls-max", "1.3") }
    )
    $clean = 0
    $urlsToTest = if ($DpiUrls.Count -gt $MaxUrls) { $DpiUrls[0..($MaxUrls - 1)] } else { $DpiUrls }
    foreach ($url in $urlsToTest) {
        $blocked = $false
        foreach ($proto in $protocols) {
            $curlArgs = @("-L", "--range", $rangeSpec, "-m", $TimeoutSec,
                "-w", "%{http_code} %{size_download}", "-o", "NUL", "-s",
                "--max-redirs", "3") + $proto.Args + $url
            $out = & curl.exe @curlArgs 2>$null
            $exit = $LASTEXITCODE
            if ($out -match '^(?<code>\d{3})\s+(?<size>\d+)$') {
                $sizeKB = [math]::Round([int64]$matches['size'] / 1024, 1)
                if ($exit -ne 0 -and $sizeKB -ge $WarnMinKB -and $sizeKB -le $WarnMaxKB) {
                    $blocked = $true; break
                }
            }
        }
        if (-not $blocked) { $clean++ }
    }
    return $clean
}

# -- Test single config (runs winws.exe directly) ------------------------------
function Invoke-ConfigTest {
    param(
        [string]$ConfigName,
        [string]$ConfigPath,
        [string]$WinwsExe,
        [string]$WinwsArgs,
        [array]$Targets,
        [int]$CurlTimeout = 5
    )
    $testSvc = "zapret_test"
    # Останавливаем предыдущую тестовую службу если есть
    net stop $testSvc > $null 2>&1
    sc.exe delete $testSvc > $null 2>&1
    Start-Sleep -Milliseconds 300

    # Создаём и запускаем временную службу (как в оригинальном service.bat)
    sc.exe create $testSvc binPath= "`"$WinwsExe`" $WinwsArgs" start= demand > $null 2>&1
    sc.exe start $testSvc > $null 2>&1
    Start-Sleep -Seconds 3

    $ok = 0; $fail = 0
    foreach ($t in $Targets) {
        if ($t.Type -eq "ping") {
            if (Test-Ping $t.Host) { $ok++ } else { $fail++ }
        } else {
            if (Test-Url $t.Url $CurlTimeout) { $ok++ } else { $fail++ }
        }
    }

    # Останавливаем и удаляем тестовую службу
    net stop $testSvc > $null 2>&1
    sc.exe delete $testSvc > $null 2>&1
    Stop-ZapretProcess

    return [PSCustomObject]@{
        Config = $ConfigName
        FullPath = $ConfigPath
        OK = $ok
        Fail = $fail
        Score = $ok
    }
}

# -- Export diagnostics report -------------------------------------------------
function Export-DiagnosticsReport {
    param([string]$RootDir, [string]$OutPath = $null)
    if (-not $OutPath) {
        $OutPath = Join-Path $RootDir "logs\diagnostics_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
    }
    $lines = [System.Collections.Generic.List[string]]::new()
    [void]$lines.Add("ZAPRET DIAGNOSTICS REPORT")
    [void]$lines.Add("Generated: $(Get-Date)")
    [void]$lines.Add("PowerShell: $($PSVersionTable.PSVersion)")
    [void]$lines.Add("=" * 60)
    [void]$lines.Add("")
    [void]$lines.Add("--- SERVICES ---")
    foreach ($svc in @("zapret", "WinDivert", "WinDivert14")) {
        $status = Test-ServiceStatus -ServiceName $svc
        [void]$lines.Add("$svc : $(if($status){$status}else{'NOT INSTALLED'})")
    }
    [void]$lines.Add("")
    [void]$lines.Add("--- PROCESSES ---")
    $winws = Get-Process -Name "winws" -ErrorAction SilentlyContinue
    [void]$lines.Add("winws.exe : $(if($winws){'RUNNING PID=' + $winws.Id}else{'NOT RUNNING'})")
    [void]$lines.Add("")
    [void]$lines.Add("--- CONFLICTS ---")
    $conflicts = Test-Conflicts
    [void]$lines.Add("Found: $(if($conflicts.Count -gt 0){$conflicts -join ', '}else{'none'})")
    [void]$lines.Add("")
    [void]$lines.Add("--- TCP ---")
    $tcpOut = netsh interface tcp show global 2>&1 | Out-String
    [void]$lines.Add($tcpOut)
    [void]$lines.Add("")
    [void]$lines.Add("--- STRATEGY ---")
    $strat = Get-CurrentStrategyName
    [void]$lines.Add("Installed: $(if($strat){$strat}else{'none'})")
    [void]$lines.Add("")
    [void]$lines.Add("--- FILES ---")
    $bin = Join-Path $RootDir "bin\winws.exe"
    [void]$lines.Add("winws.exe : $(if(Test-Path $bin){'EXISTS (' + (Get-Item $bin).Length + ' bytes)'}else{'MISSING'})")
    $hash = Get-FileHash256 -Path $bin
    [void]$lines.Add("SHA256    : $(if($hash){$hash}else{'N/A'})")
    [void]$lines.Add("")
    [void]$lines.Add("--- RECENT LOG ---")
    $logFile = Get-ChildItem (Join-Path $RootDir "logs") -Filter "*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($logFile) {
        Get-Content $logFile.FullName -Tail 20 | ForEach-Object { [void]$lines.Add($_) }
    } else {
        [void]$lines.Add("No log files found")
    }
    $text = $lines -join "`r`n"
    [IO.File]::WriteAllText($OutPath, $text, [Text.UTF8Encoding]::new($false))
    return $OutPath
}

Export-ModuleMember -Function @(
    'Test-Url', 'Test-Ping',
    'Test-PreInstallAccess', 'Test-PostInstallAccess',
    'Test-Conflicts', 'Remove-Conflicts',
    'Get-DpiSuite', 'Invoke-DpiCheck', 'Invoke-ConfigTest',
    'Export-DiagnosticsReport'
)
