@echo off
REM =====================================================================
REM  Asks the CLOUD gateway what it can see, and prints the answer.
REM
REM  This is the honest check that you are really on hosted. It does not
REM  ask the program on this PC - it asks the cloud directly, over the
REM  internet, and prints whatever the cloud says.
REM
REM  If your sessions appear here, they genuinely reached the cloud.
REM =====================================================================

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$key = (Get-Content 'D:\ReposFred\_wt\hosted-clientleg\testroot\config\director\gateway-token.txt' -Raw).Trim();" ^
  "$gw  = 'https://devthrottle-gw.azurewebsites.net';" ^
  "Write-Host '';" ^
  "Write-Host ('Asking the cloud gateway: ' + $gw);" ^
  "Write-Host '';" ^
  "try { $d = (Invoke-WebRequest \"$gw/directors\" -Headers @{Authorization=\"Bearer $key\"} -UseBasicParsing -TimeoutSec 45).Content | ConvertFrom-Json } catch { Write-Host 'Could not reach the cloud gateway.'; Write-Host $_.Exception.Message; exit 1 }" ^
  "if ($d.Count -eq 0) { Write-Host 'DevThrottles the cloud can see: none.'; Write-Host '(If you just started it, wait ten seconds and run this again.)' } else { Write-Host ('DevThrottles the cloud can see: ' + $d.Count); foreach ($x in $d) { Write-Host ('   on ' + $x.machineName + ', version ' + $x.version) } }" ^
  "Write-Host '';" ^
  "$s = (Invoke-WebRequest \"$gw/sessions\" -Headers @{Authorization=\"Bearer $key\"} -UseBasicParsing -TimeoutSec 45).Content | ConvertFrom-Json;" ^
  "if ($s.Count -eq 0) { Write-Host 'Sessions the cloud can see: none yet.' } else { Write-Host ('Sessions the cloud can see: ' + $s.Count); foreach ($x in $s) { Write-Host ('   #' + $x.number + '  ' + $x.name + '   [' + $x.stateLabel + ']') } }" ^
  "Write-Host '';" ^
  "Write-Host 'These came from the cloud, not from this PC. If a session is listed here, it made it.'"

echo.
pause
