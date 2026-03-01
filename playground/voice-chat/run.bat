@echo off
echo Starting Voice Chat Playground...
cd /d "%~dp0"
dotnet run --project src\VoiceChat.Wpf
