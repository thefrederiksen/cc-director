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
REM The Control API requires a credential; a bare POST is refused with 401 and the refusal used
REM to be reported here as "It did not answer". The secret is resolved from the test Director's
REM OWN root, the way that Director resolves it: the shared gateway token from its config.json
REM when it is enrolled, else its persisted token file (instance home first, then the flat root).
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$testroot = 'D:\ReposFred\_wt\hosted-clientleg\testroot';" ^
  "$log = Get-ChildItem \"$testroot\logs\director\*.log\" -EA SilentlyContinue | Sort-Object LastWriteTime -Desc | Select-Object -First 1;" ^
  "if (-not $log) { Write-Host 'Could not find its log, so could not find its port. Close the window by hand.'; exit 1 }" ^
  "$m = Select-String -Path $log.FullName -Pattern 'Kestrel listening on http://127.0.0.1:(\d+)' | Select-Object -Last 1;" ^
  "if (-not $m) { Write-Host 'Could not read its port from the log. Close the window by hand.'; exit 1 }" ^
  "$port = $m.Matches[0].Groups[1].Value;" ^
  "$ih = Join-Path $testroot 'instances\default'; if (-not (Test-Path $ih)) { $ih = $testroot };" ^
  "$tok = ''; $cfg = Join-Path $ih 'config\config.json';" ^
  "if (Test-Path $cfg) { try { $j = Get-Content $cfg -Raw | ConvertFrom-Json; if ($j.gateway -and $j.gateway.token) { $tok = [string]$j.gateway.token } } catch {} };" ^
  "if (-not $tok) { $tf = Join-Path $ih 'config\director\gateway-token.txt'; if (Test-Path $tf) { try { $tok = (Get-Content $tf -Raw).Trim() } catch {} } };" ^
  "$headers = @{}; if ($tok) { $headers['Authorization'] = \"Bearer $tok\" };" ^
  "try { Invoke-WebRequest \"http://127.0.0.1:$port/shutdown\" -Method POST -Headers $headers -UseBasicParsing -TimeoutSec 30 | Out-Null; Write-Host \"Shutdown sent (port $port).\" } catch { Write-Host \"It refused or did not answer: $($_.Exception.Message). Close the window by hand.\" }"

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
