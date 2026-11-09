@echo off
setlocal
title Solforge Build

where pwsh >nul 2>nul
if %ERRORLEVEL% EQU 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
)

set BUILD_EXIT=%ERRORLEVEL%
echo.
if %BUILD_EXIT% EQU 0 (
    echo [PASS] Build complete.
) else (
    echo [FAIL] Build failed with exit code %BUILD_EXIT%.
)
echo.
pause
exit /b %BUILD_EXIT%
