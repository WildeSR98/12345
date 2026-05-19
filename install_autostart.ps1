# Requires admin privileges, let's auto-elevate if not running as admin
$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin) {
    Write-Host "Please wait, requesting administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    Exit
}

$ProxyExePath = Join-Path $PSScriptRoot "tg\tg-ws-proxy-main\tg-ws-proxy-main\dist\TgWsProxy.exe"
$ProxyDirPath = Join-Path $PSScriptRoot "tg\tg-ws-proxy-main\tg-ws-proxy-main\dist"

if (-not (Test-Path $ProxyExePath)) {
    Write-Host "[ERROR] TgWsProxy.exe not found at:`n$ProxyExePath" -ForegroundColor Red
    Write-Host "Please ensure you run this script from the root folder of zapret-discord-youtube." -ForegroundColor Yellow
    Read-Host "Press Enter to exit"
    Exit
}

$TaskName = "TgWsProxy_AutoStart"

Write-Host "Creating Task: $TaskName" -ForegroundColor Cyan
Write-Host "Executable: $ProxyExePath"
Write-Host "Working Directory: $ProxyDirPath"

# Define the action to run the executable
$Action = New-ScheduledTaskAction -Execute $ProxyExePath -WorkingDirectory $ProxyDirPath

# Define the trigger to run at user logon
$Trigger = New-ScheduledTaskTrigger -AtLogOn

# Define settings: MultipleInstances IgnoreNew ensures it won't start if already running
$Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit 0

# Register the task
try {
    Register-ScheduledTask -TaskName $TaskName -Action $Action -Trigger $Trigger -Settings $Settings -RunLevel Highest -Force | Out-Null
    Write-Host ""
    Write-Host "[SUCCESS] Scheduled task created successfully!" -ForegroundColor Green
    Write-Host "TgWsProxy.exe will now start automatically when you log into Windows." -ForegroundColor Green
    Write-Host "The task is configured to NOT start a new instance if it is already running." -ForegroundColor Green
} catch {
    Write-Host ""
    Write-Host "[ERROR] Failed to create scheduled task: $_" -ForegroundColor Red
}

Write-Host ""
Read-Host "Press Enter to exit"
