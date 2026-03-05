@echo off
setlocal
chcp 65001 >nul

echo [GIT SYNC] Подготовка к синхронизации...

:: Проверка наличия git
where git >nul 2>nul
if %errorlevel% neq 0 (
    echo [ОШИБКА] Git не найден в системе. Установите Git для Windows.
    pause
    exit /b 1
)

:: Стадия
echo [1/3] Добавление изменений...
git add .

:: Коммит
set /p commit_msg="Введите описание изменений (или нажмите Enter для 'minor fixes'): "
if "%commit_msg%"=="" set "commit_msg=minor fixes"

echo [2/3] Создание коммита: "%commit_msg%"...
git commit -m "%commit_msg%"

:: Пуш
echo [3/3] Отправка в репозиторий...
git push

if %errorlevel% equ 0 (
    echo.
    echo [УСПЕХ] Все файлы успешно отправлены на GitHub!
) else (
    echo.
    echo [ОШИБКА] Не удалось отправить файлы. Проверьте соединение и права доступа.
)

echo.
pause
