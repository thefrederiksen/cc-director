@echo off
REM Uninstall cc_director service
REM Run as Administrator

setlocal enabledelayedexpansion

set SERVICE_NAME=cc_director
set DEPLOY_DIR=C:\cc-tools\cc_director_service

echo cc_director Service Uninstaller
echo ================================
echo.

REM Check admin
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: Run as Administrator
    exit /b 1
)

REM Check NSSM
where nssm >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: NSSM not found
    exit /b 1
)

REM Stop and remove service
sc query %SERVICE_NAME% >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo Stopping service...
    nssm stop %SERVICE_NAME% >nul 2>&1
    timeout /t 2 /nobreak >nul
    echo Removing service...
    nssm remove %SERVICE_NAME% confirm
    echo Service removed.
) else (
    echo Service not installed.
)

echo.
echo Done. Deployment folder preserved at: %DEPLOY_DIR%
echo To delete it: rmdir /s /q "%DEPLOY_DIR%"

endlocal
