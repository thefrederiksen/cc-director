@echo off
REM =====================================================================
REM  Stops the HOSTED DevThrottle cleanly.
REM
REM  It asks the program to shut itself down, which lets it close its own
REM  sessions tidily. That is why this exists rather than just closing the
REM  window or ending the task: a hard kill leaves a stray "interrupted"
REM  entry behind.
REM
REM  Your normal DevThrottle is never touched by this - it only ever stops
REM  the one named cc-director16.
REM =====================================================================

tasklist /FI "IMAGENAME eq cc-director16.exe" 2>nul | find /I "cc-director16.exe" >nul
if errorlevel 1 (
    echo The hosted DevThrottle is not running. Nothing to stop.
    pause
    exit /b 0
)

echo Asking the hosted DevThrottle to shut down...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$log = Get-ChildItem 'D:\ReposFred\_wt\hosted-clientleg\testroot\logs\director\*.log' -EA SilentlyContinue | Sort-Object LastWriteTime -Desc | Select-Object -First 1;" ^
  "if (-not $log) { Write-Host 'Could not find its log, so could not find its port. Close the window by hand.'; exit 1 }" ^
  "$m = Select-String -Path $log.FullName -Pattern 'Kestrel listening on http://127.0.0.1:(\d+)' | Select-Object -Last 1;" ^
  "if (-not $m) { Write-Host 'Could not read its port from the log. Close the window by hand.'; exit 1 }" ^
  "$port = $m.Matches[0].Groups[1].Value;" ^
  "try { Invoke-WebRequest \"http://127.0.0.1:$port/shutdown\" -Method POST -UseBasicParsing -TimeoutSec 30 | Out-Null; Write-Host \"Shutdown sent (port $port).\" } catch { Write-Host 'It did not answer. Close the window by hand.' }"

echo.
echo Waiting for it to close...
for /L %%i in (1,1,20) do (
    tasklist /FI "IMAGENAME eq cc-director16.exe" 2>nul | find /I "cc-director16.exe" >nul
    if errorlevel 1 (
        echo Stopped.
        timeout /t 2 >nul
        exit /b 0
    )
    timeout /t 2 >nul
)

echo.
echo It is still running. Close its window by hand if you need it gone.
pause
