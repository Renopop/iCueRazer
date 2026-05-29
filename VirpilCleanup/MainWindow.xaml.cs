using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace VirpilCleanup
{
    public partial class MainWindow : Window
    {
        private const string VIRPIL_VID = "VID_3344";

        private List<USBDevice> virpilDevices = new List<USBDevice>();
        private readonly StringBuilder logBuffer = new StringBuilder();

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "VPC (VIRPIL) USB Device Cleaner";

            if (!IsRunningAsAdmin())
            {
                Log("✗ ERREUR : L'application doit être lancée en tant qu'Administrateur.");
                Log("  → Fermez et relancez via LaunchAsAdmin.bat ou clic droit → Exécuter en tant qu'administrateur.");
                SetButtonsEnabled(false);
                return;
            }

            Log("✓ Droits administrateur confirmés.\n");
            _ = LoadDevicesAsync();
        }

        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private async void LoadDevices_Click(object sender, RoutedEventArgs e)
        {
            SetButtonsEnabled(false);
            await LoadDevicesAsync();
            SetButtonsEnabled(true);
        }

        private async void RemoveDevices_Click(object sender, RoutedEventArgs e)
        {
            SetButtonsEnabled(false);
            await RemoveDevicesAsync();
            SetButtonsEnabled(true);
        }

        private void SetButtonsEnabled(bool enabled)
        {
            RefreshButton.IsEnabled = enabled;
            RemoveButton.IsEnabled = enabled;
        }

        // ── SCAN ─────────────────────────────────────────────────────────

        private async Task LoadDevicesAsync()
        {
            ClearLog();
            Log($"Recherche des périphériques VIRPIL (Vendor ID {VIRPIL_VID})...");
            Log("(détecte aussi les appareils déconnectés / non reconnus)\n");

            var found = await Task.Run(ScanRegistry);
            virpilDevices = found;

            if (virpilDevices.Count == 0)
            {
                Log("⚠  Aucun périphérique VIRPIL trouvé dans le registre.");
                Log("   → Registre déjà propre, ou appareils jamais branchés sur ce PC.");
            }
            else
            {
                Log($"\n✓ {virpilDevices.Count} instance(s) trouvée(s).");
                Log("  Cliquez 'Supprimer' pour effacer drivers et entrées de registre.");
            }
        }

        private List<USBDevice> ScanRegistry()
        {
            var result = new List<USBDevice>();
            var roots = new (string RegPath, string Bus)[]
            {
                (@"SYSTEM\CurrentControlSet\Enum\USB", "USB"),
                (@"SYSTEM\CurrentControlSet\Enum\HID", "HID"),
            };

            foreach (var (regPath, bus) in roots)
            {
                try
                {
                    using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                    using RegistryKey? enumKey = hklm.OpenSubKey(regPath);
                    if (enumKey == null) continue;

                    foreach (string vidPidKey in enumKey.GetSubKeyNames())
                    {
                        if (!vidPidKey.ToUpperInvariant().Contains(VIRPIL_VID))
                            continue;

                        using RegistryKey? deviceKey = enumKey.OpenSubKey(vidPidKey);
                        if (deviceKey == null) continue;

                        foreach (string instanceId in deviceKey.GetSubKeyNames())
                        {
                            using RegistryKey? instanceKey = deviceKey.OpenSubKey(instanceId);

                            string name = instanceKey?.GetValue("FriendlyName")?.ToString()
                                       ?? instanceKey?.GetValue("DeviceDesc")?.ToString()
                                       ?? "(Périphérique inconnu / non reconnu)";

                            // Format PnP complet attendu par Remove-PnpDevice et pnputil
                            // ex: "USB\VID_3344&PID_0101\6&1a2b3c4d&0&1"
                            string pnpId = $@"{bus}\{vidPidKey}\{instanceId}";

                            result.Add(new USBDevice
                            {
                                Name = name,
                                PnpInstanceId = pnpId,
                                Bus = bus
                            });

                            Log($"  [{bus}] {name}");
                            Log($"         {pnpId}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"  ⚠  Erreur scan {regPath}: {ex.Message}");
                }
            }

            return result;
        }

        // ── SUPPRESSION ──────────────────────────────────────────────────

        private async Task RemoveDevicesAsync()
        {
            if (virpilDevices.Count == 0)
            {
                MessageBox.Show(
                    "Aucun périphérique VIRPIL dans le registre.\n\nRien à supprimer.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Supprimer {virpilDevices.Count} instance(s) VIRPIL ?\n\n" +
                "• Drivers désinstallés\n" +
                "• Entrées registre supprimées\n\n" +
                "Redémarrez le PC ensuite, puis rebranchez les contrôleurs.",
                "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            ClearLog();
            Log("=== Suppression VIRPIL (VID_3344) ===\n");

            var snapshot = virpilDevices.ToList();

            // Étape 1 — Remove-PnpDevice (PowerShell, plus fiable que pnputil)
            Log("── Étape 1 : Remove-PnpDevice (PowerShell)...\n");
            await Task.Run(() => RemoveViaPowerShell(snapshot));

            // Étape 2 — Nettoyage DeviceClasses (interfaces de classe)
            Log("\n── Étape 2 : Nettoyage DeviceClasses...\n");
            int cleaned = await Task.Run(CleanDeviceClasses);

            Log($"\n✓ {cleaned} entrée(s) DeviceClasses supprimée(s).");
            Log("\n══════════════════════════════");
            Log("Étapes suivantes :");
            Log("  1. Redémarrez le PC");
            Log("  2. Rebranchez les contrôleurs VIRPIL");
            Log("  3. Windows réinstalle les drivers");

            await Task.Delay(600);
            await LoadDevicesAsync();
        }

        private void RemoveViaPowerShell(List<USBDevice> devices)
        {
            Log($"  {devices.Count} instance(s) à supprimer...\n");

            // Script écrit dans un fichier temp pour éviter tout problème d'échappement
            // Get-PnpDevice cherche par HardwareID directement — plus fiable que les InstanceId
            string script = @"
$ErrorActionPreference = 'Continue'

# Chercher tous les périphériques VIRPIL (connectés + ghosts déconnectés)
$all = Get-PnpDevice | Where-Object { $_.HardwareID -match 'VID_3344' }

if ($all.Count -eq 0) {
    Write-Output 'AUCUN peripherique VID_3344 trouve via Get-PnpDevice'
    exit 0
}

Write-Output ""Trouvé $($all.Count) périphérique(s) VID_3344""

foreach ($dev in $all) {
    $label = if ($dev.FriendlyName) { $dev.FriendlyName } else { $dev.InstanceId }
    Write-Output ""  Suppression: $label""
    try {
        Remove-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop
        Write-Output ""    OK""
    } catch {
        Write-Output ""    ERREUR: $($_.Exception.Message)""
    }
}

Write-Output 'Terminé.'
";

            string tempFile = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "virpil_remove.ps1");

            try
            {
                System.IO.File.WriteAllText(tempFile, script, System.Text.Encoding.UTF8);
                RunPowerShellFile(tempFile);
            }
            finally
            {
                try { System.IO.File.Delete(tempFile); } catch { }
            }
        }

        private void RunPowerShellFile(string scriptPath)
        {
            try
            {
                string psExe = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");

                if (!System.IO.File.Exists(psExe))
                    psExe = "powershell.exe";

                var psi = new ProcessStartInfo
                {
                    FileName = psExe,
                    // -File (pas -Command) : pas d'échappement, supporte les scripts longs
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using Process proc = Process.Start(psi)!;
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Log($"  {line.TrimEnd()}");

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Log("  ── Erreurs PowerShell ──");
                    foreach (string line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        Log($"  ⚠  {line.TrimEnd()}");
                }

                Log($"\n  [Code retour PowerShell: {proc.ExitCode}]");
            }
            catch (Exception ex)
            {
                Log($"  ✗ Impossible de lancer PowerShell: {ex.Message}");
            }
        }

        // DeviceClasses : interfaces de classe HID/USB
        // Structure : HKLM\...\DeviceClasses\{GUID}\##?#USB#VID_3344&PID_xxxx#...
        // Ces clés sont accessibles en écriture avec droits admin
        private int CleanDeviceClasses()
        {
            int count = 0;
            const string dcPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses";

            try
            {
                using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                using RegistryKey? dcKey = hklm.OpenSubKey(dcPath);
                if (dcKey == null) return 0;

                foreach (string classGuid in dcKey.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? classKey = hklm.OpenSubKey($@"{dcPath}\{classGuid}", writable: true);
                        if (classKey == null) continue;

                        var toDelete = classKey.GetSubKeyNames()
                            .Where(k => k.ToUpperInvariant().Contains(VIRPIL_VID))
                            .ToList();

                        foreach (string entry in toDelete)
                        {
                            try
                            {
                                classKey.DeleteSubKeyTree(entry, throwOnMissingSubKey: false);
                                Log($"  ✓ Supprimé: DeviceClasses\\{classGuid}\\{entry[..Math.Min(entry.Length, 60)]}...");
                                count++;
                            }
                            catch (Exception ex)
                            {
                                Log($"  ⚠  {entry[..Math.Min(entry.Length, 40)]}... : {ex.Message}");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"  ⚠  Erreur DeviceClasses: {ex.Message}");
            }

            return count;
        }

        // ── LOG / UI ──────────────────────────────────────────────────────

        private void Log(string message)
        {
            logBuffer.AppendLine(message);
            Dispatcher.BeginInvoke(() =>
            {
                LogTextBox.Text = logBuffer.ToString();
                LogTextBox.ScrollToEnd();
            });
        }

        private void ClearLog()
        {
            logBuffer.Clear();
            Dispatcher.Invoke(() => LogTextBox.Text = "");
        }
    }

    public class USBDevice
    {
        public string Name { get; set; } = "";
        public string PnpInstanceId { get; set; } = "";
        public string Bus { get; set; } = "";
    }
}
