@echo off
REM VIRPIL USB Device Cleaner - Launcher with Admin Rights

cd /d "%~dp0"

REM Check if running as admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Demande des droits administrateur...
    powershell -Command "Start-Process cmd -ArgumentList '/c %~f0' -Verb RunAs"
    exit /b
)

REM Check if .NET 6+ is installed
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    color 0C
    echo.
    echo [ERREUR] .NET 6 SDK n'est pas installe sur ce systeme.
    echo.
    echo Installer .NET 6 depuis: https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

REM Compile and run
echo Building VIRPIL Cleaner...
dotnet build -c Release --nologo
if %errorlevel% neq 0 (
    color 0C
    echo [ERREUR] Erreur de compilation.
    pause
    exit /b 1
)

echo.
echo Lancement de l'application...
dotnet run -c Release --no-build

pause
