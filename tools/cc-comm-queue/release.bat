@echo off
REM Release script for cc-comm-queue
REM Builds and deploys to %LOCALAPPDATA%\cc-director\bin

setlocal

echo ============================================
echo Releasing cc-comm-queue
echo ============================================
echo.

set "SCRIPT_DIR=%~dp0"
set "INSTALL_DIR=%LOCALAPPDATA%\cc-director\bin"

REM Check for Python
where python >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Python not found in PATH
    exit /b 1
)

REM Create venv if it doesn't exist
if not exist "%SCRIPT_DIR%venv" (
    echo Creating virtual environment...
    python -m venv "%SCRIPT_DIR%venv"
)

REM Activate venv and install dependencies
echo Installing dependencies...
call "%SCRIPT_DIR%venv\Scripts\activate.bat"
pip install typer rich pydantic pyinstaller >nul 2>&1

REM Build with PyInstaller
echo Building executable...
pyinstaller "%SCRIPT_DIR%cc_comm_queue.spec" --clean --noconfirm --distpath "%SCRIPT_DIR%dist" --workpath "%SCRIPT_DIR%build"

if %errorlevel% neq 0 (
    echo ERROR: Build failed
    exit /b 1
)

REM Check if exe was created
if not exist "%SCRIPT_DIR%dist\cc-comm-queue.exe" (
    echo ERROR: Executable not found after build
    exit /b 1
)

REM Create install directory if needed
if not exist "%INSTALL_DIR%" (
    echo Creating %INSTALL_DIR%...
    mkdir "%INSTALL_DIR%"
)

REM Copy executable
echo Deploying to %INSTALL_DIR%...
copy /Y "%SCRIPT_DIR%dist\cc-comm-queue.exe" "%INSTALL_DIR%\" >nul

echo.
echo ============================================
echo Release complete!
echo ============================================
echo.
echo Executable: %INSTALL_DIR%\cc-comm-queue.exe
echo.
echo Test with: cc-comm-queue --version
echo.
