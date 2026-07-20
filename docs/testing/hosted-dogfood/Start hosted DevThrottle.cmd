@echo off
REM =====================================================================
REM  Starts the HOSTED DevThrottle - a SEPARATE DevThrottle that talks to
REM  the cloud gateway instead of the one on this PC.
REM
REM  Your normal DevThrottle is NOT touched. This one has its own folder,
REM  its own settings and its own sessions. Both can run at the same time.
REM
REM  CC_DIRECTOR_ROOT is set here, inside this window only. It is never
REM  set for the whole machine, so it cannot affect your normal DevThrottle.
REM =====================================================================

set CC_DIRECTOR_ROOT=D:\ReposFred\_wt\hosted-clientleg\testroot

tasklist /FI "IMAGENAME eq cc-director16.exe" 2>nul | find /I "cc-director16.exe" >nul
if not errorlevel 1 (
    echo The hosted DevThrottle is already running.
    echo Look for its window, or run "Stop hosted DevThrottle.cmd" first.
    pause
    exit /b 0
)

echo Starting the hosted DevThrottle...
start "" "D:\ReposFred\_wt\hosted-clientleg\local_builds\cc-director16.exe"
echo.
echo Started. A DevThrottle window will open in a few seconds.
echo It is already signed in to the cloud gateway - there is nothing to log into.
echo.
echo To check it really is talking to the cloud, run:
echo     "Show what the cloud sees.cmd"
echo.
timeout /t 5 >nul
