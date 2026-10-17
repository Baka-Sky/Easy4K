@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-all.ps1" %*
set "rc=%errorlevel%"
echo.
if "%rc%"=="0" (
    echo Build OK.
) else (
    echo Build FAILED.
)
echo.
pause
exit /b %rc%
