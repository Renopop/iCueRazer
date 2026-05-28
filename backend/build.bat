@echo off
setlocal
echo ========================================
echo  Razer Battery Monitor - Build
echo ========================================
echo.

:: Verifier Python
python --version >nul 2>&1
if errorlevel 1 (
    echo [ERREUR] Python n'est pas installe ou pas dans le PATH.
    pause & exit /b 1
)

echo [1/3] Installation des dependances...
pip install -r requirements.txt --quiet
if errorlevel 1 ( echo [ERREUR] pip install a echoue. & pause & exit /b 1 )

echo [2/3] Installation de PyInstaller...
pip install pyinstaller --quiet

echo [3/3] Compilation en .exe (sans fenetre)...
pyinstaller --onefile --noconsole --name RazerBattery --hidden-import razer_hid server.py
if errorlevel 1 ( echo [ERREUR] Compilation echouee. & pause & exit /b 1 )

echo.
echo ========================================
echo  Succes ! Binaire : dist\RazerBattery.exe
echo  Lance install.bat pour le demarrage auto.
echo ========================================
pause
