@echo off
rem Starts Wilson's service. Used by the "Wilson" scheduled task at logon, and fine by hand.
rem
rem The Groq key is read from the credentials file below unless GROQ_API_KEY is already set.
rem Output goes to %LOCALAPPDATA%\wilson\service.log next to Wilson's data, so a silent failure
rem at logon can be read about afterwards.

setlocal
cd /d "%~dp0.."
if "%WILSON_CREDENTIALS_FILE%"=="" set "WILSON_CREDENTIALS_FILE=%LOCALAPPDATA%\cc-director\config\credentials.env"
if not exist "%LOCALAPPDATA%\wilson" mkdir "%LOCALAPPDATA%\wilson"
echo %date% %time% starting Wilson from %cd% >> "%LOCALAPPDATA%\wilson\service.log"
node server\wilson.mjs >> "%LOCALAPPDATA%\wilson\service.log" 2>&1
echo %date% %time% Wilson exited with %errorlevel% >> "%LOCALAPPDATA%\wilson\service.log"
endlocal
