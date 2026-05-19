#requires -Version 5.1
# =============================================================================
#  Lists.psm1  |  Merge, repair, CIDR validation, overlap removal, hosts
# =============================================================================

Import-Module "$PSScriptRoot\Utils.psm1" -Global -ErrorAction SilentlyContinue

# -- Merge list file -----------------------------------------------------------
function Merge-ListFile {
    param([string]$FilePath, [string[]]$NewLines)
    $oldLines = @()
    if (Test-Path $FilePath) {
        $oldLines = Get-Content $FilePath -Encoding UTF8 -ErrorAction SilentlyContinue
    }
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($NewLines + $oldLines)) {
        $trimmed = $line.TrimEnd()
        $key = $trimmed.Trim()
        if ($key -eq '' -or $key.StartsWith('#')) {
            if ($seen.Add("__special__$trimmed")) { [void]$result.Add($trimmed) }
            continue
        }
        if ($seen.Add($key)) { [void]$result.Add($trimmed) }
    }
    return , $result.ToArray()
}

# -- CIDR validation -----------------------------------------------------------
function Test-ValidCidr {
    param([string]$Cidr)
    if ($Cidr -match '^\s*(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})/(\d{1,2})\s*$') {
        $octets = @([int]$matches[1], [int]$matches[2], [int]$matches[3], [int]$matches[4])
        $prefix = [int]$matches[5]
        foreach ($o in $octets) { if ($o -lt 0 -or $o -gt 255) { return $false } }
        if ($prefix -lt 0 -or $prefix -gt 32) { return $false }
        return $true
    }
    return $false
}

# -- IP to Int64 conversion ----------------------------------------------------
function Convert-IpToInt {
    param([string]$Ip)
    try {
        $octets = $Ip.Split('.')
        return ([int64]$octets[0] -shl 24) + ([int64]$octets[1] -shl 16) + ([int64]$octets[2] -shl 8) + [int64]$octets[3]
    } catch { return $null }
}

# -- CIDR overlap check --------------------------------------------------------
function Test-CidrOverlap {
    param([string]$Cidr1, [string]$Cidr2)
    $parse = {
        param($c)
        if ($c -match '^(\d+\.\d+\.\d+\.\d+)/(\d+)$') {
            $ip = Convert-IpToInt -Ip $matches[1]
            $prefix = [int]$matches[2]
            $mask = [int64]::MaxValue -shl (32 - $prefix)
            return @{ IP = $ip; Mask = $mask; Prefix = $prefix }
        }
        return $null
    }
    $a = & $parse $Cidr1
    $b = & $parse $Cidr2
    if (-not $a -or -not $b) { return $false }
    $netA = $a.IP -band $a.Mask
    $netB = $b.IP -band $b.Mask
    if ($a.Prefix -le $b.Prefix) {
        return ($b.IP -band $a.Mask) -eq $netA
    } else {
        return ($a.IP -band $b.Mask) -eq $netB
    }
}

# -- Remove overlapping CIDRs O(n log n) ---------------------------------------
function Remove-OverlappingCidrs {
    param([string[]]$Lines)
    $cidrs = [System.Collections.Generic.List[PSObject]]::new()
    $others = [System.Collections.Generic.List[string]]::new()

    foreach ($line in $Lines) {
        $trim = $line.Trim()
        if ($trim -match '^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})/(\d{1,2})$') {
            $ipInt = ([int64]$matches[1] -shl 24) + ([int64]$matches[2] -shl 16) + ([int64]$matches[3] -shl 8) + [int64]$matches[4]
            $prefix = [int]$matches[5]
            $mask = [int64]::MaxValue -shl (32 - $prefix)
            $start = $ipInt -band $mask
            $end = $start + ([int64]1 -shl (32 - $prefix)) - 1
            [void]$cidrs.Add([PSCustomObject]@{ Line = $line; Start = $start; End = $end })
        } else {
            [void]$others.Add($line)
        }
    }

    if ($cidrs.Count -eq 0) { return $others.ToArray() }

    # Сортируем по началу диапазона, затем по длине (больший prefix = меньший диапазон)
    $sorted = $cidrs | Sort-Object Start, @{E={$_.End - $_.Start}; Asc=$true}

    $kept = [System.Collections.Generic.List[PSObject]]::new()
    [void]$kept.Add($sorted[0])

    for ($i = 1; $i -lt $sorted.Count; $i++) {
        $current = $sorted[$i]
        $last = $kept[$kept.Count - 1]
        # Если текущий начинается ВНУТРИ последнего сохранённого — пропускаем (перекрытие)
        if ($current.Start -ge $last.Start -and $current.Start -le $last.End) {
            continue
        }
        [void]$kept.Add($current)
    }

    return $others.ToArray() + ($kept | Select-Object -ExpandProperty Line)
}

# -- Repair list files (dedup, clean, validate) --------------------------------
function Repair-ListFiles {
    param([string]$ListsPath, [switch]$RemoveOverlap)
    $badPatterns = @('^\s*0\.\d+\.\d+\.\d+', '^\s*1\.0\.0\.0/')
    $allFiles = @(Get-ChildItem $ListsPath -Filter "*.txt" -File -ErrorAction SilentlyContinue)
    if ($allFiles.Count -eq 0) { return }

    $startTime = Get-Date
    $processed = 0
    $total = $allFiles.Count

    foreach ($file in $allFiles) {
        $processed++
        Show-Progress -Activity "Очистка списков" -Status "[$processed/$total] $($file.Name)" -PercentComplete ([math]::Round(($processed/$total)*100))

        try {
            $encoding = New-Object System.Text.UTF8Encoding($false)
            $lines = [System.IO.File]::ReadAllLines($file.FullName, $encoding)
            if ($lines.Length -eq 0) { continue }

            # Быстрый ранний выход: проверяем, есть ли вообще дубли/пустые строки
            $needsProcessing = $false
            $uniqueCheck = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($line in $lines) {
                $t = $line.Trim()
                if ([string]::IsNullOrWhiteSpace($t)) { $needsProcessing = $true; break }
                if (-not $t.StartsWith("#") -and -not $uniqueCheck.Add($t)) { $needsProcessing = $true; break }
            }

            if (-not $needsProcessing -and -not ($RemoveOverlap -and $file.Name -eq "ipset-all.txt")) {
                Write-VerboseLog "$($file.Name): уже чист"
                continue
            }

            $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $cleaned = [System.Collections.Generic.List[string]]::new($lines.Length)
            $hasChanged = $false
            $removedInvalid = 0

            foreach ($line in $lines) {
                $trimmed = $line.Trim()
                if ($trimmed.StartsWith("#")) {
                    if ($seen.Add("__com__$trimmed")) { [void]$cleaned.Add($line) }
                    else { $hasChanged = $true }
                    continue
                }
                if ([string]::IsNullOrWhiteSpace($trimmed)) {
                    $hasChanged = $true
                    continue
                }
                if ($file.Name.StartsWith("ipset-")) {
                    $isBad = $false
                    foreach ($pat in $badPatterns) {
                        if ($trimmed -match $pat) { $isBad = $true; break }
                    }
                    if ($isBad) { $removedInvalid++; $hasChanged = $true; continue }
                }
                if ($seen.Add($trimmed)) { [void]$cleaned.Add($trimmed) }
                else { $hasChanged = $true }
            }

            if ($RemoveOverlap -and $file.Name -eq "ipset-all.txt" -and $cleaned.Count -gt 0) {
                $before = $cleaned.Count
                $deduped = Remove-OverlappingCidrs -Lines $cleaned.ToArray()
                if ($deduped.Count -ne $before) {
                    $cleaned = [System.Collections.Generic.List[string]]::new($deduped)
                    $hasChanged = $true
                    Write-VerboseLog "Удалены перекрывающиеся CIDR: $before -> $($cleaned.Count)"
                }
            }

            if ($hasChanged) {
                [System.IO.File]::WriteAllLines($file.FullName, $cleaned.ToArray(), $encoding)
                $msg = "$($file.Name): очищен ($($cleaned.Count) строк)"
                if ($removedInvalid -gt 0) { $msg += ", удалено $removedInvalid невалидных" }
                Write-OK $msg
            }
        }
        catch {
            Write-Warn "Ошибка обработки $($file.Name): $($_.Exception.Message)"
        }
    }

    Hide-Progress
    $elapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 1)
    Write-Info "Очистка завершена за $elapsed сек."
}

# -- Download list with retry --------------------------------------------------
function Download-List {
    param([string]$Uri, [int]$MaxRetries = 2)
    for ($i = 0; $i -lt $MaxRetries; $i++) {
        try {
            $resp = Invoke-WebRequest -Uri $Uri -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
            if ($resp.StatusCode -eq 200) { return ($resp.Content -split "`r?`n") }
        }
        catch {
            if ($i -lt $MaxRetries - 1) { Start-Sleep -Seconds 1 }
        }
    }
    return $null
}

# -- Parallel list download (PS7+) ---------------------------------------------
function Download-ListsParallel {
    param([array]$ListEntries, [string]$ListsDir)
    $results = @{}
    if ((Test-PowerShell7)) {
        $remoteEntries = $ListEntries | Where-Object { $_.Remote -ne "" -and -not $_.User }
        if ($remoteEntries.Count -gt 0) {
            $remoteEntries | ForEach-Object -Parallel {
                try {
                    $resp = Invoke-WebRequest -Uri $_.Remote -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
                    if ($resp.StatusCode -eq 200) {
                        @{ Key = $_.Local; Success = $true; Lines = ($resp.Content -split "`r?`n"); Stub = $_.Stub }
                    } else {
                        @{ Key = $_.Local; Success = $false; Lines = @(); Stub = $_.Stub }
                    }
                }
                catch {
                    @{ Key = $_.Local; Success = $false; Lines = @(); Stub = $_.Stub }
                }
            } -ThrottleLimit 4 | ForEach-Object { $results[$_.Key] = $_ }
        }
    }
    foreach ($entry in $ListEntries) {
        if ($entry.User -or $entry.Remote -eq "") { continue }
        if ($results.ContainsKey($entry.Local)) { continue }
        Show-Progress -Activity "Updating lists" -Status "Downloading $($entry.Local)..." -PercentComplete -1
        $lines = Download-List -Uri $entry.Remote
        $results[$entry.Local] = @{ Key = $entry.Local; Success = ($lines -ne $null); Lines = if ($lines) { $lines } else { @() }; Stub = $entry.Stub }
    }
    Hide-Progress
    return $results
}

# -- Update hosts file ---------------------------------------------------------
function Update-HostsFile {
    param([string]$HostsUrl, [string]$Marker = "# --- zapret-discord-youtube ---")
    $hostsFile = "$env:SystemRoot\System32\drivers\etc\hosts"
    try {
        $resp = Invoke-WebRequest -Uri $HostsUrl -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
        if ($resp.StatusCode -ne 200) { throw "HTTP $($resp.StatusCode)" }
        $repoLines = ($resp.Content -split "`r?`n") |
            Where-Object { $_.Trim() -ne '' -and -not $_.TrimStart().StartsWith('#') }
        $currentHosts = Get-Content $hostsFile -Encoding UTF8 -ErrorAction Stop
        $missing = $repoLines | Where-Object {
            $line = $_.Trim()
            -not ($currentHosts | Where-Object { $_.Trim() -eq $line })
        }
        if ($missing.Count -eq 0) {
            Write-OK "hosts: all zapret entries present"
            return
        }
        $hasMarker = $currentHosts | Where-Object { $_.Trim() -eq $Marker }
        $appendLines = @()
        if (-not $hasMarker) { $appendLines += ""; $appendLines += $Marker }
        $appendLines += $missing
        $addText = "`n" + ($appendLines -join "`n")
        [IO.File]::AppendAllText($hostsFile, $addText, [Text.UTF8Encoding]::new($false))
        Write-OK "hosts: added $($missing.Count) entries"
    }
    catch {
        Write-Warn "hosts update failed: $($_.Exception.Message)"
    }
}

Export-ModuleMember -Function @(
    'Merge-ListFile', 'Test-ValidCidr', 'Convert-IpToInt', 'Test-CidrOverlap',
    'Remove-OverlappingCidrs', 'Repair-ListFiles',
    'Download-List', 'Download-ListsParallel', 'Update-HostsFile'
)
