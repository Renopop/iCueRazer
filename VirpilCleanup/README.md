# VIRPIL USB Device Cleaner

Utilitaire Windows pour supprimer complètement les périphériques USB VIRPIL du système et du registre Windows, permettant une réinstallation propre des drivers.

## 🎯 Fonctionnalités

- ✓ Détection automatique des périphériques VIRPIL (VPC, Warthog, etc.)
- ✓ Suppression complète du système
- ✓ Nettoyage en profondeur du registre Windows
- ✓ Interface graphique conviviale
- ✓ Confirmations de sécurité
- ✓ Logs détaillés des opérations

## 📋 Prérequis

- **Système d'exploitation** : Windows 10 / Windows 11
- **.NET Framework** : .NET 6 SDK ou Runtime
- **Permissions** : Droits administrateur requis
- **Connexion USB** : Périphériques VIRPIL connectés (optionnel, peuvent être non-détectés)

### Installation de .NET 6

Si vous n'avez pas .NET 6 d'installé, téléchargez et installez :
https://dotnet.microsoft.com/download/dotnet/6.0

Choisissez l'option ".NET Runtime" ou ".NET Desktop Runtime" pour votre architecture (x64 ou x86).

## 🚀 Utilisation

### Option 1 : Application WPF (Interface graphique)

```batch
LaunchAsAdmin.bat
```

L'application demande automatiquement les droits administrateur.

**Interface** :
1. Cliquez sur **"Actualiser"** pour détecter les périphériques
2. Cliquez sur **"Supprimer les périphériques"** pour commencer
3. Confirmez l'opération
4. Attendez que le nettoyage se termine

### Option 2 : Script PowerShell (Terminal)

```powershell
# Avec confirmation
powershell -ExecutionPolicy Bypass -File Clean-VirpilDevices.ps1

# Sans confirmation (mode automatique)
powershell -ExecutionPolicy Bypass -File Clean-VirpilDevices.ps1 -Force
```

⚠️ Le script demande automatiquement les droits administrateur.

## 🔍 Que fait cet outil ?

### Suppression du système
- Désactive les périphériques
- Utilise `pnputil` pour supprimer les drivers
- Nettoie les entrées d'appareils

### Nettoyage du registre
Supprime les clés de registre dans :
- `HKLM\SYSTEM\CurrentControlSet\Enum\USB`
- `HKLM\SYSTEM\CurrentControlSet\Enum\USBSTOR`
- `HKLM\SYSTEM\CurrentControlSet\Enum\HID`
- `HKLM\SYSTEM\CurrentControlSet\Services`
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`
- `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`

## ✅ Étapes après utilisation

1. **Redémarrez votre ordinateur** (fortement recommandé)
2. **Reconnectez vos périphériques VIRPIL** via USB
3. **Attendez la réinstallation automatique** des drivers
4. **Configurez les périphériques** dans le logiciel VIRPIL

## ⚠️ Important

- ⚠️ **L'opération est irréversible** : sauvegardez votre configuration avant de procéder
- ⚠️ **Droits administrateur obligatoires**
- ⚠️ **Redémarrage recommandé** après le nettoyage
- ⚠️ Ne fermez pas les applications à mi-chemin

## 🐛 Dépannage

### "Accès refusé" sur le registre
- Relancez l'application avec droits administrateur
- Sur certaines clés très protégées, l'accès peut être refusé (normal)

### Les périphériques ne sont pas détectés
- Reconnectez vos périphériques USB
- Cliquez sur "Actualiser"
- Les appareils non-connectés peuvent être listés quand même

### L'application plante au lancement
- Vérifiez que .NET 6 est installé : `dotnet --version`
- Installez .NET 6 si absent
- Réessayez avec les droits administrateur

## 🔄 Compilation manuelle

```bash
# Build
dotnet build -c Release

# Run
dotnet run -c Release
```

## 📝 Notes

- Le script PowerShell est autonome et ne nécessite pas de compilation
- L'interface WPF offre une meilleure visualisation du processus
- Les deux outils font le même travail, choisissez selon votre préférence

## 📄 Licence

Libre d'utilisation pour tous les utilisateurs de périphériques VIRPIL.

---

**Besoin d'aide ?** Consultez la documentation VIRPIL officielle.
