@echo off
echo Building CC Director release...

dotnet publish src\CcDirector.Wpf\CcDirector.Wpf.csproj -c Release -o publish

if errorlevel 1 (
    echo [ERROR] Build failed!
    exit /b 1
)

echo Copying to releases folder...
if not exist releases mkdir releases
copy /y publish\cc_director_v2.exe releases\cc_director.exe

echo.
echo [OK] Release built successfully!
echo Output: releases\cc_director.exe
