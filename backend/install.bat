@echo off
setlocal

:: Chemin absolu vers le .exe compile
SET EXE=%~dp0dist\RazerBattery.exe

if not exist "%EXE%" (
    echo [ERREUR] RazerBattery.exe introuvable.
    echo Lancez d'abord build.bat pour le compiler.
    pause & exit /b 1
)

echo Ajout au demarrage automatique Windows...
REG ADD "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" ^
    /v "RazerBattery" /t REG_SZ /d "\"%EXE%\"" /f >nul

echo Demarrage immediat...
start "" "%EXE%"

echo.
echo ============================================
echo  OK ! RazerBattery tourne en fond.
echo  Il redemarrera automatiquement avec Windows.
echo.
echo  Widget iCUE Dashboard :
echo    URL  : http://localhost:8765/
echo    Taille recommandee : 320 x 200 px
echo ============================================
pause
