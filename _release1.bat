@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0scripts\release.ps1" -Slot 1 %*
if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED - see errors above
    pause
    exit /b %ERRORLEVEL%
)
echo.
echo Exe location: %~dp0releases\cc-director1.exe
pause
