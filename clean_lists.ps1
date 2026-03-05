$listsDir = Join-Path $PSScriptRoot "lists"

if (-not (Test-Path $listsDir)) {
    Write-Host "[ERROR] Directory 'lists' not found at: $listsDir" -ForegroundColor Red
    Exit
}

Write-Host "Checking files in 'lists' directory for duplicates and empty lines..." -ForegroundColor Cyan

$files = Get-ChildItem -Path $listsDir -Filter "*.txt" -File
$totalFixed = 0

foreach ($file in $files) {
    try {
        $lines = Get-Content -Path $file.FullName -Encoding UTF8
        
        if ($null -eq $lines) {
            continue
        }
        
        if ($lines -is [string]) {
            $lines = @($lines)
        }
        
        $cleanedLines = @()
        $seen = @{}
        $hasChanged = $false
        
        foreach ($line in $lines) {
            $trimmedLine = $line.Trim()
            
            if ([string]::IsNullOrWhiteSpace($trimmedLine)) {
                $hasChanged = $true
                continue
            }
            
            $lowerLine = $trimmedLine.ToLower()
            if (-not $seen.ContainsKey($lowerLine)) {
                $seen[$lowerLine] = $true
                $cleanedLines += $trimmedLine
            }
            else {
                $hasChanged = $true
            }
        }
        
        if ($hasChanged) {
            Set-Content -Path $file.FullName -Value $cleanedLines -Encoding UTF8
            Write-Host "[ FIXED ] $($file.Name) - removed duplicates or empty lines." -ForegroundColor Green
            $totalFixed++
        }
        else {
            Write-Host "[ OK ] $($file.Name) - clean." -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "[ ERROR ] Failed to process $($file.Name): $_" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Done! Fixed files: $totalFixed" -ForegroundColor Cyan
Write-Host ""
