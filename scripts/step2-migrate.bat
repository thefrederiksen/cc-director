@echo off
echo ============================================================
echo  Step 2: Migrate storage to unified cc-director location
echo ============================================================
echo.
echo Make sure cc-director and all Claude Code terminals are closed!
echo.
python "%~dp0migrate-storage.py" --run
echo.
if %ERRORLEVEL% NEQ 0 (
    echo [FAILED] Migration had errors. Check output above.
    pause
    exit /b 1
)
echo [OK] Migration complete. Now rebuild and launch cc-director.
pause
