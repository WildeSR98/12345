@echo off
chcp 65001 > nul

:: РџРµСЂРµРґР°С‘Рј РїСѓС‚СЊ Рє СЃРєСЂРёРїС‚Сѓ С‡РµСЂРµР· РїРµСЂРµРјРµРЅРЅСѓСЋ СЃСЂРµРґС‹, С‡С‚РѕР±С‹ РёР·Р±РµР¶Р°С‚СЊ РїСЂРѕР±Р»РµРј СЃ РєР°РІС‹С‡РєР°РјРё
set "PS_SCRIPT=%~dp0utils\autosetup.ps1"

:: РћС‚РєСЂС‹РІР°РµРј PowerShell-РѕРєРЅРѕ СЃ РїСЂР°РІР°РјРё Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° РЅР°РїСЂСЏРјСѓСЋ
powershell -NoProfile -Command "Start-Process powershell.exe -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File \"{0}\"' -f $env:PS_SCRIPT) -Verb RunAs -Wait"

:: Р•СЃР»Рё РїРѕР»СЊР·РѕРІР°С‚РµР»СЊ РѕС‚РєР»РѕРЅРёР» UAC вЂ” РїРѕРєР°Р·С‹РІР°РµРј СЃРѕРѕР±С‰РµРЅРёРµ РІ СЌС‚РѕРј РѕРєРЅРµ
if %errorlevel% neq 0 (
    echo.
    echo  [!] РџСЂР°РІР° Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° РЅРµ РїСЂРµРґРѕСЃС‚Р°РІР»РµРЅС‹.
    echo      Р—Р°РїСѓСЃС‚РёС‚Рµ autosetup.bat РѕС‚ РёРјРµРЅРё Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР°.
    echo.
    pause
)