@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul

echo [GIT SYNC] Preparing to synchronize...

:: Check for git
where git >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] Git not found. Please install Git for Windows.
    pause
    exit /b 1
)

:: Check if this is a git repository
if not exist ".git\" (
    echo [INFO] Not a git repository yet.
    set /p init_repo="Initialize git repository? (Y/N): "
    if /i "!init_repo!"=="Y" (
        git init
        echo [INFO] Repository initialized.
        set /p remote_url="Enter GitHub repository URL (e.g. https://github.com/WildeSR98/12345.git): "
        if not "!remote_url!"=="" (
            git remote add origin !remote_url!
            echo [INFO] Remote 'origin' added.
        )
    ) else (
        echo [CANCEL] Git initialization skipped.
        pause
        exit /b 0
    )
)

:: Stage 1: Add
echo [1/4] Adding changes...
git add .

:: Stage 2: Check status
git status --short

:: Stage 3: Commit
set /p commit_msg="Enter commit description (or press Enter for 'Update to v2.0'): "
if "%commit_msg%"=="" set "commit_msg=Update to v2.0"

echo [2/4] Creating commit: "%commit_msg%"...
git commit -m "%commit_msg%"

:: Stage 4: Pull before push (avoid conflicts)
echo [3/4] Pulling latest changes from remote...
git pull origin main --rebase 2>nul || git pull origin master --rebase 2>nul

:: Stage 5: Push
echo [4/4] Pushing to repository...
git push

if %errorlevel% equ 0 (
    echo.
    echo [SUCCESS] All files successfully sent to GitHub!
) else (
    echo.
    echo [ERROR] Failed to push. Check connection and access rights.
)

echo.
pause
