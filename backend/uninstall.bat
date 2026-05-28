@echo off
echo Suppression du demarrage automatique...
REG DELETE "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "RazerBattery" /f >nul 2>&1

echo Arret du processus...
taskkill /IM RazerBattery.exe /F >nul 2>&1

echo Desinstalle.
pause
