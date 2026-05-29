using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace VirpilCleanup
{
    // VIRPIL Controls USB Vendor ID = 0x3344 (gravé dans le hardware)
    // Présent dans le registre sous la forme VID_3344
    // Cette approche fonctionne même si Windows affiche "Périphérique inconnu"

    public partial class MainWindow : Window
    {
        private const string VIRPIL_VID = "VID_3344";

        private List<USBDevice> virpilDevices = new List<USBDevice>();
        private StringBuilder logBuffer = new StringBuilder();

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "VPC (VIRPIL) USB Device Cleaner";
            LoadDevicesInternal();
        }

        private void LoadDevices_Click(object sender, RoutedEventArgs e) => LoadDevicesInternal();
        private void RemoveDevices_Click(object sender, RoutedEventArgs e) => RemoveDevicesInternal();

        private void LoadDevicesInternal()
        {
            ClearLog();
            Log($"Recherche par Vendor ID USB {VIRPIL_VID} (VIRPIL Controls)...");
            Log("Cette méthode trouve les périphériques même si Windows ne les reconnaît pas.\n");
            virpilDevices.Clear();

            // Chemin USB principal + HID (joystick/gamepad passent par HID)
            string[] registryRoots = new[]
            {
                @"SYSTEM\CurrentControlSet\Enum\USB",
                @"SYSTEM\CurrentControlSet\Enum\HID",
            };

            foreach (string root in registryRoots)
            {
                ScanRegistryForVID(root);
            }

            if (virpilDevices.Count == 0)
            {
                Log("⚠  Aucun périphérique VIRPIL trouvé dans le registre.");
                Log("   → Si les contrôleurs n'ont jamais été branchés sur ce PC, c'est normal.");
                Log("   → Sinon, le nettoyage du registre est déjà effectué.");
            }
            else
            {
                Log($"\n✓ {virpilDevices.Count} instance(s) trouvée(s).");
                Log("  Cliquez sur 'Supprimer' pour les effacer complètement.");
            }
        }

        private void ScanRegistryForVID(string registryPath)
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                using RegistryKey baseKey = root.OpenSubKey(registryPath);
                if (baseKey == null) return;

                foreach (string deviceKeyName in baseKey.GetSubKeyNames())
                {
                    if (!deviceKeyName.ToUpper().Contains(VIRPIL_VID))
                        continue;

                    using RegistryKey deviceKey = baseKey.OpenSubKey(deviceKeyName);
                    if (deviceKey == null) continue;

                    // Chaque sous-clé est une instance du périphérique
                    foreach (string instanceId in deviceKey.GetSubKeyNames())
                    {
                        using RegistryKey instanceKey = deviceKey.OpenSubKey(instanceId);
                        string friendlyName = instanceKey?.GetValue("FriendlyName")?.ToString()
                                          ?? instanceKey?.GetValue("DeviceDesc")?.ToString()
                                          ?? "(Périphérique inconnu)";

                        string fullInstancePath = $@"{registryPath}\{deviceKeyName}\{instanceId}";
                        string pnpInstanceId = $@"{deviceKeyName}\{instanceId}";

                        virpilDevices.Add(new USBDevice
                        {
                            Description = $"{friendlyName}  [{deviceKeyName}]",
                            DeviceID = pnpInstanceId,
                            RegistryPath = fullInstancePath
                        });

                        Log($"  ✓ {friendlyName}");
                        Log($"      ID: {pnpInstanceId}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"  ⚠  Erreur lecture registre ({registryPath}): {ex.Message}");
            }
        }

        private void RemoveDevicesInternal()
        {
            if (virpilDevices.Count == 0)
            {
                MessageBox.Show(
                    "Aucun périphérique VIRPIL trouvé dans le registre.\n\n" +
                    "Le nettoyage est peut-être déjà effectué, ou les contrôleurs\n" +
                    "n'ont pas encore été branchés sur ce PC.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                $"Supprimer {virpilDevices.Count} instance(s) VIRPIL (VID {VIRPIL_VID}) ?\n\n" +
                "• Drivers désinstallés\n" +
                "• Entrées de registre supprimées\n\n" +
                "Redémarrez le PC ensuite, puis rebranchez les contrôleurs.",
                "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            ClearLog();
            Log("=== Suppression des périphériques VIRPIL ===\n");

            // Étape 1 : pnputil pour désinstaller les drivers
            Log("── Étape 1 : désinstallation via pnputil...\n");
            foreach (var device in virpilDevices)
            {
                RemoveViaPnpUtil(device);
            }

            // Étape 2 : nettoyage registre par VID
            Log("\n── Étape 2 : nettoyage du registre (VID_3344)...\n");
            int cleaned = CleanRegistryByVID();

            Log($"\n✓ Terminé — {cleaned} entrée(s) de registre supprimée(s).");
            Log("\nÉtapes suivantes :");
            Log("  1. Redémarrez le PC");
            Log("  2. Rebranchez les contrôleurs VIRPIL");
            Log("  3. Windows les réinstallera automatiquement");

            System.Threading.Thread.Sleep(800);
            LoadDevicesInternal();
        }

        private void RemoveViaPnpUtil(USBDevice device)
        {
            try
            {
                Log($"  Suppression: {device.Description}");

                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/remove-device \"{device.DeviceID}\" /uninstall",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Verb = "runas"
                };

                using Process proc = Process.Start(psi);
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                Log(proc.ExitCode == 0 ? "    ✓ Supprimé" : $"    ⚠  Code retour {proc.ExitCode} (peut être ignoré)");
            }
            catch (Exception ex)
            {
                Log($"    ⚠  pnputil: {ex.Message}");
            }
        }

        private int CleanRegistryByVID()
        {
            int count = 0;
            string[] registryRoots = new[]
            {
                @"SYSTEM\CurrentControlSet\Enum\USB",
                @"SYSTEM\CurrentControlSet\Enum\HID",
                @"SYSTEM\CurrentControlSet\Control\DeviceClasses",
            };

            foreach (string root in registryRoots)
            {
                count += DeleteVIDKeysUnder(root);
            }

            return count;
        }

        private int DeleteVIDKeysUnder(string registryPath)
        {
            int count = 0;
            try
            {
                using RegistryKey rootKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                using RegistryKey baseKey = rootKey.OpenSubKey(registryPath, writable: true);
                if (baseKey == null) return 0;

                var toDelete = baseKey.GetSubKeyNames()
                    .Where(k => k.ToUpper().Contains(VIRPIL_VID))
                    .ToList();

                foreach (string keyName in toDelete)
                {
                    try
                    {
                        baseKey.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                        Log($"    ✓ Supprimé: HKLM\\{registryPath}\\{keyName}");
                        count++;
                    }
                    catch (Exception ex)
                    {
                        Log($"    ⚠  Impossible de supprimer {keyName}: {ex.Message}");
                    }
                }

                // Pour DeviceClasses, VID peut être dans les sous-clés
                if (registryPath.Contains("DeviceClasses"))
                {
                    count += CleanDeviceClasses(baseKey);
                }
            }
            catch (Exception ex)
            {
                Log($"  ⚠  Erreur ({registryPath}): {ex.Message}");
            }

            return count;
        }

        private int CleanDeviceClasses(RegistryKey baseKey)
        {
            int count = 0;
            foreach (string classGuid in baseKey.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey classKey = baseKey.OpenSubKey(classGuid, writable: true);
                    if (classKey == null) continue;

                    var toDelete = classKey.GetSubKeyNames()
                        .Where(k => k.ToUpper().Contains(VIRPIL_VID))
                        .ToList();

                    foreach (string entry in toDelete)
                    {
                        try
                        {
                            classKey.DeleteSubKeyTree(entry, throwOnMissingSubKey: false);
                            Log($"    ✓ Supprimé: DeviceClasses\\{classGuid}\\{entry}");
                            count++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return count;
        }

        private void Log(string message)
        {
            logBuffer.AppendLine(message);
            Dispatcher.Invoke(() =>
            {
                if (LogTextBox != null)
                    LogTextBox.Text = logBuffer.ToString();
            });
        }

        private void ClearLog()
        {
            logBuffer.Clear();
            if (LogTextBox != null)
                LogTextBox.Text = "";
        }
    }

    public class USBDevice
    {
        public string Description { get; set; } = "";
        public string DeviceID { get; set; } = "";
        public string RegistryPath { get; set; } = "";
    }
}
