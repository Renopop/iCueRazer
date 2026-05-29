using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace VirpilCleanup
{
    public partial class MainWindow : Window
    {
        private List<USBDevice> virpilDevices = new List<USBDevice>();
        private StringBuilder logBuffer = new StringBuilder();

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "VIRPIL USB Device Cleaner";
            LoadDevicesInternal();
        }

        private void LoadDevices_Click(object sender, RoutedEventArgs e) => LoadDevicesInternal();

        private void RemoveDevices_Click(object sender, RoutedEventArgs e) => RemoveDevicesInternal();

        private void LoadDevicesInternal()
        {
            ClearLog();
            Log("Détection des périphériques USB VIRPIL...");
            virpilDevices.Clear();

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_USBHub"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string description = obj["Description"]?.ToString() ?? "";
                        string deviceId = obj["DeviceID"]?.ToString() ?? "";

                        if (IsVirpilDevice(description))
                        {
                            virpilDevices.Add(new USBDevice { Description = description, DeviceID = deviceId });
                            Log($"✓ Trouvé: {description}");
                        }
                    }
                }

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%VIRPIL%' OR Name LIKE '%VPC%'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string description = obj["Name"]?.ToString() ?? "";
                        string deviceId = obj["DeviceID"]?.ToString() ?? "";

                        if (!virpilDevices.Any(d => d.DeviceID == deviceId))
                        {
                            virpilDevices.Add(new USBDevice { Description = description, DeviceID = deviceId });
                            Log($"✓ Trouvé: {description}");
                        }
                    }
                }

                if (virpilDevices.Count == 0)
                {
                    Log("⚠ Aucun périphérique VIRPIL détecté.");
                }
                else
                {
                    Log($"\n✓ {virpilDevices.Count} périphérique(s) VIRPIL trouvé(s).");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Erreur lors de la détection: {ex.Message}");
            }
        }

        private bool IsVirpilDevice(string description)
        {
            if (string.IsNullOrEmpty(description))
                return false;

            string upper = description.ToUpper();
            return upper.Contains("VIRPIL") || upper.Contains("VPC");
        }

        private void RemoveDevicesInternal()
        {
            if (virpilDevices.Count == 0)
            {
                MessageBox.Show("Aucun périphérique VIRPIL à supprimer.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                $"Êtes-vous sûr de vouloir supprimer {virpilDevices.Count} périphérique(s) VIRPIL ?\n\n" +
                "Cette opération supprimera aussi les entrées du registre Windows.\n" +
                "Vous devrez reconnecter les périphériques après.",
                "Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            ClearLog();
            Log("Suppression profonde des périphériques VIRPIL...\n");

            int removed = 0;
            foreach (var device in virpilDevices)
            {
                if (RemoveDevice(device))
                    removed++;
            }

            Log("\n====== Nettoyage du registre Windows... ======\n");
            CleanRegistry();

            Log($"\n✓ {removed}/{virpilDevices.Count} périphérique(s) supprimé(s).");
            Log("\nDéconnectez et reconnectez vos périphériques VIRPIL.");
            Log("Les drivers vont se réinstaller automatiquement.");

            System.Threading.Thread.Sleep(1000);
            LoadDevicesInternal();
        }

        private bool RemoveDevice(USBDevice device)
        {
            try
            {
                Log($"Suppression: {device.Description}...");

                System.Threading.Thread.Sleep(200);

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-Command \"pnputil /remove-device \\\"{device.DeviceID}\\\" /uninstall\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(psi))
                {
                    process.WaitForExit();
                    Log($"  ✓ Supprimé du système");
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"  ❌ Erreur: {ex.Message}");
                return false;
            }
        }

        private void CleanRegistry()
        {
            try
            {
                string[] registryPaths = new[]
                {
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\USB",
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\USBSTOR",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services"
                };

                int cleaned = 0;

                foreach (string basePath in registryPaths)
                {
                    try
                    {
                        cleaned += RemoveVirpilRegistryEntries(basePath);
                    }
                    catch (Exception ex)
                    {
                        Log($"  ⚠ Erreur lors du nettoyage de {basePath}: {ex.Message}");
                    }
                }

                Log($"✓ {cleaned} entrée(s) de registre supprimée(s)");
            }
            catch (Exception ex)
            {
                Log($"❌ Erreur lors du nettoyage du registre: {ex.Message}");
            }
        }

        private int RemoveVirpilRegistryEntries(string basePath)
        {
            int count = 0;
            string[] parts = basePath.Split('\\');
            string hive = parts[0];
            string subPath = string.Join("\\", parts.Skip(1));

            RegistryHive registryHive = hive.Contains("LOCAL_MACHINE") ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

            try
            {
                using (RegistryKey key = RegistryKey.OpenBaseKey(registryHive, RegistryView.Default))
                using (RegistryKey subKey = key.OpenSubKey(subPath, true))
                {
                    if (subKey != null)
                    {
                        foreach (string subKeyName in subKey.GetSubKeyNames().ToList())
                        {
                            if (IsVirpilRegistryEntry(subKeyName))
                            {
                                try
                                {
                                    subKey.DeleteSubKeyTree(subKeyName);
                                    Log($"  ✓ Supprimé: {basePath}\\{subKeyName}");
                                    count++;
                                }
                                catch (Exception ex)
                                {
                                    Log($"  ⚠ Impossible de supprimer {subKeyName}: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silencieusement échouer si le chemin n'existe pas
            }

            return count;
        }

        private bool IsVirpilRegistryEntry(string entryName)
        {
            string upper = entryName.ToUpper();
            return upper.Contains("VIRPIL") ||
                   upper.Contains("VPC") ||
                   upper.Contains("THRUSTMASTER") ||
                   upper.Contains("WARTHOG");
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
        public string Description { get; set; }
        public string DeviceID { get; set; }
    }
}
