@echo off
setlocal enabledelayedexpansion

echo.
echo  cc-gmail OAuth Authentication
echo  =============================
echo.

:: Data directory: %LOCALAPPDATA%\cc-director\data\gmail\accounts
set "DATA_DIR=%LOCALAPPDATA%\cc-director\data\gmail\accounts"

:: Find the source directory
set "SRC_DIR=%~dp0"
if not exist "!SRC_DIR!src\cli.py" (
    set "SRC_DIR=%~dp0"
)

:: List existing accounts
echo  Your accounts:
echo.

set "ACCT_COUNT=0"
set "LAST_ACCT="

if not exist "!DATA_DIR!" (
    echo  No accounts found.
    echo  Run: cc-gmail accounts add myaccount
    goto :end
)

for /d %%A in ("!DATA_DIR!\*") do (
    set /a ACCT_COUNT+=1
    set "LAST_ACCT=%%~nxA"
    echo    %%~nxA
)

if "!ACCT_COUNT!"=="0" (
    echo  No accounts found.
    echo  Run: cc-gmail accounts add myaccount
    goto :end
)

echo.

:: Auto-select if only one account
if "!ACCT_COUNT!"=="1" (
    set "ACCOUNT=!LAST_ACCT!"
    echo  Using account: !LAST_ACCT!
) else (
    set /p "ACCOUNT=  Enter account name: "
)

if "!ACCOUNT!"=="" (
    echo  No account name entered.
    goto :end
)

if not exist "!DATA_DIR!\!ACCOUNT!" (
    echo  Account '!ACCOUNT!' not found. Use the account name, not email.
    goto :end
)

if not exist "!DATA_DIR!\!ACCOUNT!\credentials.json" (
    echo  No credentials.json for '!ACCOUNT!'.
    echo.
    echo  Get it from Google Cloud Console:
    echo    1. https://console.cloud.google.com/auth/clients
    echo    2. Click your OAuth client, download JSON
    echo    3. Save to: !DATA_DIR!\!ACCOUNT!\credentials.json
    goto :end
)

echo.
echo  Your default browser will open for Google sign-in.
echo  Sign in with the correct Google account and click Allow.
echo.
echo  DO NOT close this window until authentication completes.
echo.
echo  -----------------------------------------------
echo.

if exist "!SRC_DIR!src\cli.py" (
    pushd "!SRC_DIR!"
    python -m src.cli -a !ACCOUNT! auth --force
    set "AUTH_RESULT=!ERRORLEVEL!"
    popd
) else (
    cc-gmail -a !ACCOUNT! auth --force
    set "AUTH_RESULT=!ERRORLEVEL!"
)

echo.
if "!AUTH_RESULT!"=="0" (
    echo  Done! Verify with: cc-gmail -a !ACCOUNT! profile
) else (
    echo  Authentication failed. Check the error above.
)

:end
echo.
echo  Press any key to close...
pause >nul
