@echo off
REM deploy.bat - One command to build and deploy cc_director_service
REM Run as Administrator from: D:\ReposFred\cc_director\scheduler\
REM
REM This script:
REM   1. Stops the service (if running)
REM   2. Builds the executable
REM   3. Deploys to C:\cc-tools\cc_director_service\
REM   4. Installs service (if not installed)
REM   5. Starts the service

setlocal

set SERVICE_NAME=cc_director
set DEPLOY_DIR=C:\cc-tools\cc_director_service
set SOURCE_DIR=%~dp0

echo.
echo ============================================
echo cc_director_service - Build and Deploy
echo ============================================
echo.

REM Check for admin rights
net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: Run this script as Administrator
    echo Right-click and select "Run as administrator"
    exit /b 1
)

REM Check NSSM
where nssm >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: NSSM not found. Install with: winget install nssm
    exit /b 1
)

REM Stop service if running
echo [1/5] Stopping service...
sc query %SERVICE_NAME% >nul 2>&1
if %ERRORLEVEL% equ 0 (
    nssm stop %SERVICE_NAME% >nul 2>&1
    timeout /t 3 /nobreak >nul
    echo      Service stopped.
) else (
    echo      Service not installed yet.
)

REM Build
echo [2/5] Building executable...
cd /d "%SOURCE_DIR%"
powershell -ExecutionPolicy Bypass -File build.ps1
if %ERRORLEVEL% neq 0 (
    echo ERROR: Build failed
    exit /b 1
)

REM Create deployment directory and data directories
echo [3/5] Deploying to %DEPLOY_DIR%...
if not exist "%DEPLOY_DIR%" mkdir "%DEPLOY_DIR%"
if not exist "%DEPLOY_DIR%\logs" mkdir "%DEPLOY_DIR%\logs"
if not exist "%DEPLOY_DIR%\data" mkdir "%DEPLOY_DIR%\data"
REM Ensure shared data directory exists for cc_tools tokens
if not exist "C:\cc-tools\data" mkdir "C:\cc-tools\data"
if not exist "C:\cc-tools\data\gmail" mkdir "C:\cc-tools\data\gmail"
if not exist "C:\cc-tools\data\gmail\accounts" mkdir "C:\cc-tools\data\gmail\accounts"
if not exist "C:\cc-tools\data\outlook" mkdir "C:\cc-tools\data\outlook"
if not exist "C:\cc-tools\data\outlook\tokens" mkdir "C:\cc-tools\data\outlook\tokens"
REM Set permissions for SYSTEM account
icacls "C:\cc-tools\data" /grant "SYSTEM:(OI)(CI)F" /Q >nul 2>&1

REM Copy executable
copy /Y "%SOURCE_DIR%dist\cc_director_service.exe" "%DEPLOY_DIR%\" >nul
if %ERRORLEVEL% neq 0 (
    echo ERROR: Failed to copy executable
    exit /b 1
)
echo      Deployed.

REM Install service if not exists
echo [4/5] Configuring service...
sc query %SERVICE_NAME% >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo      Installing service...
    nssm install %SERVICE_NAME% "%DEPLOY_DIR%\cc_director_service.exe" run --with-gateway
    nssm set %SERVICE_NAME% AppDirectory "%DEPLOY_DIR%"
    nssm set %SERVICE_NAME% AppStdout "%DEPLOY_DIR%\logs\service_stdout.log"
    nssm set %SERVICE_NAME% AppStderr "%DEPLOY_DIR%\logs\service_stderr.log"
    nssm set %SERVICE_NAME% AppRotateFiles 1
    nssm set %SERVICE_NAME% AppRotateBytes 10485760
    nssm set %SERVICE_NAME% Description "cc_director - Job scheduler and communication dispatch"
    nssm set %SERVICE_NAME% AppEnvironmentExtra CC_DIRECTOR_DB=%DEPLOY_DIR%\data\cc_director.db
    nssm set %SERVICE_NAME% AppEnvironmentExtra+ CC_DIRECTOR_LOG_DIR=%DEPLOY_DIR%\logs
    nssm set %SERVICE_NAME% AppEnvironmentExtra+ CC_TOOLS_DATA=C:\cc-tools\data
) else (
    echo      Service already configured.
    REM Ensure CC_TOOLS_DATA is set even for existing service
    nssm set %SERVICE_NAME% AppEnvironmentExtra+ CC_TOOLS_DATA=C:\cc-tools\data >nul 2>&1
)

REM Start service
echo [5/5] Starting service...
nssm start %SERVICE_NAME%
timeout /t 2 /nobreak >nul

REM Check status
sc query %SERVICE_NAME% | findstr "RUNNING" >nul
if %ERRORLEVEL% equ 0 (
    echo.
    echo ============================================
    echo SUCCESS - Service is running
    echo ============================================
    echo.
    echo Logs: %DEPLOY_DIR%\logs\
    echo Data: %DEPLOY_DIR%\data\
    echo.
) else (
    echo.
    echo WARNING: Service may not have started correctly
    echo Check logs: %DEPLOY_DIR%\logs\service_stderr.log
    echo.
    type "%DEPLOY_DIR%\logs\service_stderr.log"
)

endlocal
