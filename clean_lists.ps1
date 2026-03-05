$listsDir = Join-Path $PSScriptRoot "lists"

# Проверяем существование основной папки lists
if (-not (Test-Path $listsDir)) {
    Write-Host "[ОШИБКА] Директория 'lists' не найдена по пути: $listsDir" -ForegroundColor Red
    Exit
}

# Список целевых файлов для обработки
$targetFiles = @(
    "ipset-all.txt",
    "ipset-exclude.txt",
    "ipset-exclude-user.txt",
    "list-exclude.txt",
    "list-exclude-user.txt",
    "list-general.txt",
    "list-general-user.txt",
    "list-google.txt"
)

Write-Host "Поиск файлов в папке 'lists' и подпапках..." -ForegroundColor Cyan
Write-Host "Целевые файлы: $($targetFiles -join ', ')" -ForegroundColor Gray
Write-Host ""

# Получаем файлы рекурсивно, фильтруя по именам
$files = Get-ChildItem -Path $listsDir -Include $targetFiles -File -Recurse -ErrorAction SilentlyContinue

if ($null -eq $files -or $files.Count -eq 0) {
    Write-Host "[ПРЕДУПРЕЖДЕНИЕ] Целевые файлы не найдены." -ForegroundColor Yellow
    Exit
}

$totalFixed = 0
$totalProcessed = 0

foreach ($file in $files) {
    $totalProcessed++
    try {
        # Читаем содержимое
        $lines = Get-Content -Path $file.FullName -Encoding UTF8
        
        if ($null -eq $lines) {
            continue
        }
        
        # Если файл содержит всего одну строку, Get-Content вернет строку, а не массив
        if ($lines -is [string]) {
            $lines = @($lines)
        }
        
        $cleanedLines = @()
        $seen = @{}
        $hasChanged = $false
        
        foreach ($line in $lines) {
            $trimmedLine = $line.Trim()
            
            # Пропускаем пустые строки
            if ([string]::IsNullOrWhiteSpace($trimmedLine)) {
                $hasChanged = $true
                continue
            }
            
            # Проверка на дубликаты (без учета регистра)
            $lowerLine = $trimmedLine.ToLower()
            if (-not $seen.ContainsKey($lowerLine)) {
                $seen[$lowerLine] = $true
                $cleanedLines += $trimmedLine
            }
            else {
                $hasChanged = $true
            }
        }
        
        # Если были изменения, записываем файл обратно
        if ($hasChanged) {
            # Добавляем пустую строку в конце, если файл не пустой (опционально, для чистоты)
            if ($cleanedLines.Count -gt 0) {
                Set-Content -Path $file.FullName -Value $cleanedLines -Encoding UTF8
            }
            else {
                # Если файл стал пустым после очистки
                Set-Content -Path $file.FullName -Value $null -Encoding UTF8
            }
            
            Write-Host "[ ИСПРАВЛЕНО ] $($file.FullName)" -ForegroundColor Green
            Write-Host "             Удалены дубликаты или пустые строки." -ForegroundColor Gray
            $totalFixed++
        }
        else {
            Write-Host "[ ОК ] $($file.FullName)" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "[ ОШИБКА ] Не удалось обработать $($file.FullName): $_" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "Готово!" -ForegroundColor Cyan
Write-Host "Всего обработано файлов: $totalProcessed" -ForegroundColor White
Write-Host "Изменено файлов: $totalFixed" -ForegroundColor White
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""