@echo off
echo === Building CC Director ===
dotnet build src\CcDirector.Wpf -c Debug
if %ERRORLEVEL% NEQ 0 (
    echo BUILD FAILED
    pause
    exit /b 1
)
echo.
echo === Running CC Director ===
start "" "src\CcDirector.Wpf\bin\Debug\net10.0-windows\cc-director.exe"
if %ERRORLEVEL% NEQ 0 (
    echo LAUNCH FAILED
    pause
    exit /b 1
)
