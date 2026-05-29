using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace VirpilCleanup
{
    // VIRPIL Controls USB Vendor ID = 3344 (hex), gravé dans le hardware
    // Présent dans le registre sous VID_3344 dans Enum\USB et Enum\HID
    // pnputil nécessite le chemin COMPLET : "USB\VID_3344&PID_xxxx\{instance}"

    public partial class MainWindow : Window
    {
        private const string VIRPIL_VID = "VID_3344";

        private List<USBDevice> virpilDevices = new List<USBDevice>();
        private readonly StringBuilder logBuffer = new StringBuilder();

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "VPC (VIRPIL) USB Device Cleaner";
            _ = LoadDevicesAsync();
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

        // ──────────────────────────────────────────────────────────────
        // SCAN
        // ──────────────────────────────────────────────────────────────

        private async Task LoadDevicesAsync()
        {
            ClearLog();
            Log($"Recherche des périphériques VIRPIL par Vendor ID {VIRPIL_VID}...");
            Log("(fonctionne même si Windows affiche 'Périphérique inconnu')\n");

            var found = await Task.Run(ScanRegistry);
            virpilDevices = found;

            if (virpilDevices.Count == 0)
            {
                Log("⚠  Aucun périphérique VIRPIL trouvé.");
                Log("   → Soit les contrôleurs n'ont jamais été branchés sur ce PC");
                Log("   → Soit le registre est déjà propre.");
            }
            else
            {
                Log($"\n✓ {virpilDevices.Count} instance(s) VIRPIL trouvée(s).");
                Log("  Cliquez sur 'Supprimer' pour tout effacer.");
            }
        }

        private List<USBDevice> ScanRegistry()
        {
            var result = new List<USBDevice>();

            // Tuple : (chemin registre, préfixe bus pour pnputil)
            // IMPORTANT : pnputil attend "USB\VID_3344...\instance" — le préfixe bus est obligatoire
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
                    using RegistryKey enumKey = hklm.OpenSubKey(regPath);
                    if (enumKey == null) continue;

                    foreach (string vidPidKey in enumKey.GetSubKeyNames())
                    {
                        if (!vidPidKey.ToUpperInvariant().Contains(VIRPIL_VID))
                            continue;

                        using RegistryKey deviceKey = enumKey.OpenSubKey(vidPidKey);
                        if (deviceKey == null) continue;

                        foreach (string instanceId in deviceKey.GetSubKeyNames())
                        {
                            using RegistryKey instanceKey = deviceKey.OpenSubKey(instanceId);

                            string name = instanceKey?.GetValue("FriendlyName")?.ToString()
                                       ?? instanceKey?.GetValue("DeviceDesc")?.ToString()
                                       ?? "(Périphérique inconnu)";

                            // Chemin complet pnputil = bus + VID_PID + instanceId
                            // ex: "USB\VID_3344&PID_0101\6&1a2b3c4d&0&1"
                            string pnpInstanceId = $@"{bus}\{vidPidKey}\{instanceId}";

                            result.Add(new USBDevice
                            {
                                Description = $"{name}  [{vidPidKey}]",
                                PnpInstanceId = pnpInstanceId,
                                VidPidKey = vidPidKey,
                                Bus = bus
                            });

                            Log($"  ✓ [{bus}] {name}");
                            Log($"        {pnpInstanceId}");
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

        // ──────────────────────────────────────────────────────────────
        // SUPPRESSION
        // ──────────────────────────────────────────────────────────────

        private async Task RemoveDevicesAsync()
        {
            if (virpilDevices.Count == 0)
            {
                MessageBox.Show(
                    "Aucun périphérique VIRPIL trouvé dans le registre.\n\n" +
                    "Le nettoyage est peut-être déjà effectué.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Supprimer {virpilDevices.Count} instance(s) VIRPIL ?\n\n" +
                "• Drivers désinstallés via pnputil\n" +
                "• Entrées DeviceClasses nettoyées\n\n" +
                "⚠  Redémarrez le PC ensuite, puis rebranchez les contrôleurs.",
                "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            ClearLog();
            Log("=== Suppression des périphériques VIRPIL ===\n");

            var snapshot = virpilDevices.ToList();

            // ── Étape 1 : pnputil /remove-device (désinstalle driver + retire de l'arbre PnP)
            Log("── Étape 1 : pnputil /remove-device /uninstall\n");
            await Task.Run(() =>
            {
                foreach (var device in snapshot)
                    RunPnpUtil(device);
            });

            // ── Étape 2 : nettoyage DeviceClasses (interfaces de classe HID/USB)
            // Note : Enum\USB et Enum\HID sont protégés par Windows → pnputil les gère
            Log("\n── Étape 2 : nettoyage HKLM\\...\\Control\\DeviceClasses\n");
            int cleaned = await Task.Run(CleanDeviceClasses);

            Log($"\n✓ {cleaned} entrée(s) DeviceClasses supprimée(s).");
            Log("\n══════════════════════════════════");
            Log("Étapes suivantes :");
            Log("  1. Redémarrez le PC");
            Log("  2. Rebranchez les contrôleurs VIRPIL");
            Log("  3. Windows réinstalle les drivers automatiquement");

            await Task.Delay(600);
            await LoadDevicesAsync();
        }

        private void RunPnpUtil(USBDevice device)
        {
            Log($"  → {device.Description}");
            Log($"    Instance: {device.PnpInstanceId}");

            try
            {
                // pnputil attend exactement "USB\VID_3344&PID_xxxx\{instance}"
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/remove-device \"{device.PnpInstanceId}\" /uninstall",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using Process proc = Process.Start(psi)!;
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (!string.IsNullOrWhiteSpace(stdout))
                    Log($"    {stdout.Trim()}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    Log($"    ⚠  {stderr.Trim()}");

                Log(proc.ExitCode == 0
                    ? "    ✓ OK"
                    : $"    ⚠  Code retour: {proc.ExitCode}");
            }
            catch (Exception ex)
            {
                Log($"    ✗ Erreur pnputil: {ex.Message}");
            }
        }

        // DeviceClasses contient les interfaces de classe (HID, joystick…)
        // Structure : HKLM\...\DeviceClasses\{GUID}\##?#USB#VID_3344&PID_xxxx#...
        // Ces clés ne sont PAS protégées comme Enum\USB → suppression directe possible
        private int CleanDeviceClasses()
        {
            int count = 0;
            const string dcPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses";

            try
            {
                using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                using RegistryKey dcKey = hklm.OpenSubKey(dcPath, writable: false);
                if (dcKey == null) return 0;

                foreach (string classGuid in dcKey.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey classKey = hklm.OpenSubKey($@"{dcPath}\{classGuid}", writable: true);
                        if (classKey == null) continue;

                        var toDelete = classKey.GetSubKeyNames()
                            .Where(k => k.ToUpperInvariant().Contains(VIRPIL_VID))
                            .ToList();

                        foreach (string entry in toDelete)
                        {
                            try
                            {
                                classKey.DeleteSubKeyTree(entry, throwOnMissingSubKey: false);
                                Log($"    ✓ DeviceClasses\\{classGuid}\\{entry}");
                                count++;
                            }
                            catch (Exception ex)
                            {
                                Log($"    ⚠  Impossible: {entry} — {ex.Message}");
                            }
                        }
                    }
                    catch { /* classe inaccessible, on passe */ }
                }
            }
            catch (Exception ex)
            {
                Log($"  ⚠  Erreur DeviceClasses: {ex.Message}");
            }

            return count;
        }

        // ──────────────────────────────────────────────────────────────
        // LOG / UI
        // ──────────────────────────────────────────────────────────────

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
        public string Description { get; set; } = "";
        // Format pnputil : "USB\VID_3344&PID_xxxx\{instance}"
        public string PnpInstanceId { get; set; } = "";
        public string VidPidKey { get; set; } = "";
        public string Bus { get; set; } = "";
    }
}
