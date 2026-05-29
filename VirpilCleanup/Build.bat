@echo off
REM Script de compilation du VIRPIL USB Device Cleaner

cd /d "%~dp0"

echo Building VIRPIL USB Device Cleaner...
echo.

dotnet build -c Release

if %errorlevel% neq 0 (
    echo.
    color 0C
    echo Build FAILED
    pause
    exit /b 1
)

echo.
color 0A
echo Build SUCCESS
echo.
echo Executable: bin\Release\net9.0-windows\VirpilCleanup.exe
echo.
pause
