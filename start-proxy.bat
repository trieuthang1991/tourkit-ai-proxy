@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo === Tourkit AI Proxy === http://localhost:5080 ===
echo (Ctrl+C de dung)
echo.
dotnet run --no-launch-profile --urls http://localhost:5080
