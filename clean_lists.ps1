$listsDir = Join-Path $PSScriptRoot "lists"

if (-not (Test-Path $listsDir)) {
    Write-Host "[ERROR] Directory 'lists' not found at: $listsDir" -ForegroundColor Red
    Exit
}

Write-Host "Checking files in 'lists' directory for duplicates and empty lines..." -ForegroundColor Cyan

$files = Get-ChildItem -Path $listsDir -Filter "*.txt" -File -Recurse
$totalFixed = 0

foreach ($file in $files) {
    try {
        # Using [IO.File] for better performance and explicit encoding
        $encoding = New-Object System.Text.UTF8Encoding($false) # UTF8 without BOM
        $lines = [System.IO.File]::ReadAllLines($file.FullName, $encoding)
        
        if ($lines.Length -eq 0) { continue }
        
        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $cleanedLines = [System.Collections.Generic.List[string]]::new()
        $hasChanged = $false
        
        foreach ($line in $lines) {
            $trimmed = $line.Trim()
            
            # Keep comments as they are, but check for duplicate comments if they are exactly the same
            if ($trimmed.StartsWith("#")) {
                if ($seen.Add("__comment__$trimmed")) {
                    $cleanedLines.Add($line) # Keep original line with leading spaces if any
                } else {
                    $hasChanged = $true
                }
                continue
            }
            
            # Skip empty or whitespace-only lines
            if ([string]::IsNullOrWhiteSpace($trimmed)) {
                $hasChanged = $true
                continue
            }
            
            # Deduplicate by normalized (trimmed, lowercase) key
            if ($seen.Add($trimmed)) {
                $cleanedLines.Add($trimmed)
            } else {
                $hasChanged = $true
            }
        }
        
        if ($hasChanged -or ($lines.Length -ne $cleanedLines.Count)) {
            [System.IO.File]::WriteAllLines($file.FullName, $cleanedLines.ToArray(), $encoding)
            Write-Host "[ FIXED ] $($file.FullName) - removed duplicates or empty lines." -ForegroundColor Green
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

Write-Host "`nDone! Fixed files: $totalFixed" -ForegroundColor Cyan