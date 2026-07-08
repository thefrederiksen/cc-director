@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0..\local-build-avalonia.ps1" -Slot 4 -OutputDir "%~dp0." %*
if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED - see errors above
    pause
    exit /b %ERRORLEVEL%
)
echo.
echo Exe location: %~dp0cc-director4.exe
pause
