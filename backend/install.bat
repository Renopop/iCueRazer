@echo off
setlocal

SET EXE=%~dp0dist\RazerBattery.exe
SET CERT=%APPDATA%\RazerBattery\cert.pem

if not exist "%EXE%" (
    echo [ERREUR] RazerBattery.exe introuvable.
    echo Lancez d'abord build.bat pour le compiler.
    pause & exit /b 1
)

:: Demarrer le serveur (il genere le certificat SSL au premier lancement)
echo [1/3] Demarrage du serveur (generation du certificat SSL)...
start "" "%EXE%"

:: Attendre que cert.pem soit cree (max 15 secondes)
echo [2/3] Attente de la generation du certificat...
SET /A COUNT=0
:WAIT_CERT
if exist "%CERT%" goto CERT_READY
SET /A COUNT+=1
if %COUNT% GEQ 15 (
    echo [AVERTISSEMENT] Certificat non trouve apres 15s.
    echo Verifiez que RazerBattery.exe tourne (Gestionnaire des taches).
    goto SKIP_CERT
)
timeout /t 1 /nobreak >nul
goto WAIT_CERT

:CERT_READY
echo [3/3] Ajout du certificat aux autorites de confiance Windows...
certutil -addstore -user Root "%CERT%" >nul 2>&1
if errorlevel 1 (
    echo [INFO] certutil a echoue - vous devrez accepter le certificat manuellement dans iCUE.
) else (
    echo       Certificat accepte automatiquement par Windows.
)

:SKIP_CERT
:: Ajouter au demarrage automatique
REG ADD "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" ^
    /v "RazerBattery" /t REG_SZ /d "\"%EXE%\"" /f >nul

echo.
echo ============================================
echo  OK ! RazerBattery tourne en fond.
echo  Il redemarrera avec Windows automatiquement.
echo.
echo  Widget iCUE Dashboard :
echo    URL   : https://localhost:8765/
echo    Taille: 320 x 200 px
echo ============================================
pause
