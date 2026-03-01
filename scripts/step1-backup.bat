@echo off
echo ============================================================
echo  Step 1: Backup all storage locations before migration
echo ============================================================
echo.
python "%~dp0backup-before-migration.py"
echo.
if %ERRORLEVEL% NEQ 0 (
    echo [FAILED] Backup failed. Do NOT proceed to step 2.
    pause
    exit /b 1
)
echo [OK] Backup complete. You can now run step2-migrate.bat
pause
