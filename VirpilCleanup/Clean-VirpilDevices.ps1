# VIRPIL USB Device Cleaner - PowerShell Script
# Supprime complètement les périphériques VIRPIL du système et du registre

param(
    [switch]$Force = $false
)

function Test-Administrator {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Title {
    param([string]$Message)
    Write-Host "`n$('='*60)" -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan
    Write-Host "$('='*60)`n" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Error-Custom {
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

function Write-Warning-Custom {
    param([string]$Message)
    Write-Host "⚠ $Message" -ForegroundColor Yellow
}

# Check admin rights
if (-not (Test-Administrator)) {
    Write-Title "Erreur: Droits administrateur requis"
    Write-Host "Ce script doit être exécuté en tant qu'administrateur.`n"
    Write-Host "Redémarrage avec droits admin...`n" -ForegroundColor Yellow

    $ScriptPath = $MyInvocation.MyCommand.Path
    $Process = New-Object System.Diagnostics.ProcessStartInfo
    $Process.FileName = 'powershell.exe'
    $Process.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`""
    $Process.UseShellExecute = $true
    $Process.Verb = 'runas'

    [System.Diagnostics.Process]::Start($Process) | Out-Null
    exit
}

Write-Title "Nettoyeur de périphériques USB VIRPIL"

# Find VIRPIL devices
Write-Host "Recherche des périphériques VIRPIL connectés...`n"

$virpilDevices = @()
$usbDevices = Get-PnpDevice -Class Net, USB, HIDClass, Ports 2>$null | Where-Object {
    $_.Name -match 'VIRPIL|VPC|Warthog'
}

if ($usbDevices) {
    Write-Host "Périphériques trouvés:`n" -ForegroundColor Cyan
    foreach ($device in $usbDevices) {
        Write-Host "  • $($device.Name)" -ForegroundColor Green
        Write-Host "    ID: $($device.InstanceId)" -ForegroundColor Gray
        Write-Host "    État: $($device.Status)`n" -ForegroundColor Gray
        $virpilDevices += $device
    }
} else {
    Write-Warning-Custom "Aucun périphérique VIRPIL détecté"
}

# Confirmation
Write-Host ""
if (-not $Force) {
    $confirmation = Read-Host "Êtes-vous sûr de vouloir supprimer tous les périphériques VIRPIL ? (O/N)"
    if ($confirmation -ne 'O' -and $confirmation -ne 'o') {
        Write-Host "`nOpération annulée." -ForegroundColor Yellow
        exit
    }
}

Write-Title "Suppression des périphériques"

# Remove devices
$removed = 0
foreach ($device in $virpilDevices) {
    try {
        Write-Host "Suppression: $($device.Name)..." -ForegroundColor Yellow

        $device | Disable-PnpDevice -Confirm:$false -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500

        $device | Remove-PnpDevice -Confirm:$false -ErrorAction SilentlyContinue
        Write-Success "Supprimé"

        $removed++
    } catch {
        Write-Error-Custom "Erreur: $_"
    }
}

Write-Title "Nettoyage du registre Windows"

$regPaths = @(
    @{ Path = "HKLM:\SYSTEM\CurrentControlSet\Enum\USB"; Name = "USB Devices" },
    @{ Path = "HKLM:\SYSTEM\CurrentControlSet\Enum\USBSTOR"; Name = "USB Storage" },
    @{ Path = "HKLM:\SYSTEM\CurrentControlSet\Enum\HID"; Name = "HID Devices" },
    @{ Path = "HKLM:\SYSTEM\CurrentControlSet\Services"; Name = "Services" },
    @{ Path = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"; Name = "Programs" },
    @{ Path = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"; Name = "User Programs" }
)

$registryCleaned = 0

foreach ($regPath in $regPaths) {
    try {
        if (Test-Path $regPath.Path) {
            $subKeys = Get-ChildItem -Path $regPath.Path -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match 'VIRPIL|VPC|Warthog|Thrustmaster' }

            foreach ($subKey in $subKeys) {
                try {
                    Remove-Item -Path $subKey.PSPath -Recurse -Force -ErrorAction SilentlyContinue
                    Write-Success "Supprimé: $($subKey.PSChildName)"
                    $registryCleaned++
                } catch {
                    Write-Warning-Custom "Impossible de supprimer $($subKey.PSChildName): $_"
                }
            }
        }
    } catch {
        Write-Warning-Custom "Erreur lors du nettoyage de $($regPath.Name): $_"
    }
}

Write-Title "Résumé de l'opération"

Write-Host "Périphériques supprimés: " -NoNewline
Write-Host "$removed" -ForegroundColor Green
Write-Host "Entrées registre supprimées: " -NoNewline
Write-Host "$registryCleaned" -ForegroundColor Green

Write-Host "`nÉtapes suivantes:" -ForegroundColor Cyan
Write-Host "1. Redémarrez votre ordinateur (recommandé)"
Write-Host "2. Reconnectez vos périphériques VIRPIL"
Write-Host "3. Les drivers se réinstalleront automatiquement"
Write-Host "4. Configurez vos périphériques dans le logiciel VIRPIL`n"

Write-Success "Nettoyage terminé !"
Write-Host "Appuyez sur une touche pour quitter...`n"
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
