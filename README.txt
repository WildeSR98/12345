# Zapret Autosetup v2.0 (12345)

Automated installation, configuration and update of Zapret (DPI bypass software based on packet routing and modification) with self-update feature.

## What's New in v2.0

- **Modular Architecture**: PowerShell modules (Utils, Update, Service, Lists, Diagnostics, Strategy) instead of 1100+ line monolith
- **Silent Install**: `autosetup.bat -silent -strategy "FAKE TLS AUTO"` for automation
- **Transactional Updates**: backup before update, automatic rollback on failure
- **Logging**: structured logs in `logs/zapret_YYYY-MM-DD_HH-mm-ss.log`
- **Hash Verification**: SHA256 for downloaded archives
- **JSON Strategies**: `strategies.json` + generator instead of 25 bat copies
- **Parallel Downloads**: lists download in parallel (PowerShell 7+)
- **Diagnostic Reports**: export service state, versions, conflicts to text file
- **PowerShell 7 Support**: auto-detect `pwsh.exe` with fallback to `powershell.exe`

## Features

- **Smart Self-Update from GitHub**: Checks commits in `WildeSR98/12345` and releases in `Flowseal/zapret-discord-youtube`. On update creates backup; on failure performs automatic rollback.
- **Integrated Testing**: Test all strategies with parallel threads (up to 8), DPI check for 16-20 KB freeze, pick best config in 1 click.
- **List Management (Auto-Cleanup)**: Merge, deduplicate, CIDR validation, remove overlapping subnets, clean invalid IPs (`0.x.x.x`). Your `*-user.txt` files are never overwritten.
- **Service Management**: Proper WinDivert shutdown, conflict removal (GoodbyeDPI etc.), install as Windows service.

## How to Install and Run?

### Interactive Mode (default)

1. Extract the archive to a convenient folder.
2. Right-click `autosetup.bat` -> "Run as administrator".
3. Follow the instructions in the blue PowerShell window.

**New in menu — option [5] "Test strategies & install best":**
- Shows previous test results (if any)
- Tests all `general*.bat` strategies against Discord/YouTube
- Saves history to `logs/strategy_test_history.json`
- Offers to install the best strategy as a service

### Silent Mode (for advanced users / automation)

```batch
:: Auto-install with recommended strategy
autosetup.bat -silent

:: Specify exact strategy
autosetup.bat -silent -strategy "FAKE TLS AUTO"

:: Full reinstall without prompts
autosetup.bat -silent -mode reinstall -strategy "ALT"

:: Verbose logging
autosetup.bat -verbose
```

> ⚠️ **Important**: Never run `utils/autosetup.ps1` directly. Always use `autosetup.bat`. It guarantees `ExecutionPolicy Bypass` and administrator rights.

## Project Structure

| File/Folder | Purpose |
|-------------|---------|
| `autosetup.bat` | Entry point. Detects `pwsh`/`powershell`, passes arguments, requests admin rights |
| `utils/autosetup.ps1` | Main script. Loads config, imports modules, orchestrates the process |
| `utils/modules/` | PowerShell modules with business logic |
| `utils/modules/Utils.psm1` | Logging, backup/rollback, progress, rights check, hashes |
| `utils/modules/Update.psm1` | GitHub API, SHA256 verified download, transactional updates |
| `utils/modules/Service.psm1` | Install/remove zapret service, Game Filter, winws arg parsing |
| `utils/modules/Lists.psm1` | List merge, cleanup, CIDR validation, parallel download, hosts |
| `utils/modules/Diagnostics.psm1` | Accessibility tests, DPI check, conflict detection, report export |
| `utils/modules/Strategy.psm1` | Load strategies from JSON, generate `.bat` files |
| `config.json` | Centralized config: repository URLs, settings, lists |
| `strategies.json` | DPI bypass strategies in machine-readable format |
| `service.bat` | Service manager (menu). Option 10 - diagnostics, option 12 - export report |
| `logs/` | Log files and diagnostic reports |
| `backups/` | Automatic backups before updates (last 5 kept) |

## Updating Scripts

No more manual downloads from GitHub.
Every time you run `autosetup.bat` the script:

1. Requests latest commit hash from repository.
2. Compares with local version (`utils/12345_version.txt`).
3. Offers to press `1` to apply updates. After downloading, script closes and asks to restart `autosetup.bat`.
