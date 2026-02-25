@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0scripts\release.ps1" %*
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
echo.
echo Exe location: %~dp0releases\cc-director.exe
pause
