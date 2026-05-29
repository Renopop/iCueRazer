# Crée un raccourci bureau pour le VIRPIL Cleaner

param(
    [string]$ShortcutName = "VIRPIL USB Cleaner"
)

$ScriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$DesktopPath = [Environment]::GetFolderPath('Desktop')
$ShortcutPath = Join-Path $DesktopPath "$ShortcutName.lnk"

$PowerShellPath = (Get-Command powershell.exe).Source
$ScriptFile = Join-Path $ScriptPath "Clean-VirpilDevices.ps1"

# Create COM object for shell link
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)

$Shortcut.TargetPath = $PowerShellPath
$Shortcut.Arguments = "-ExecutionPolicy Bypass -File `"$ScriptFile`""
$Shortcut.WorkingDirectory = $ScriptPath
$Shortcut.WindowStyle = 1  # Normal window
$Shortcut.Description = "VIRPIL USB Device Cleaner - Supprime les périphériques VIRPIL"

# Icon (using PowerShell icon)
$Shortcut.IconLocation = "C:\Windows\System32\shell32.dll,49"

$Shortcut.Save()

Write-Host "✓ Raccourci créé sur le Bureau: $ShortcutName.lnk" -ForegroundColor Green
Write-Host "  Cliquez sur le raccourci pour exécuter le nettoyeur" -ForegroundColor Green
