using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private static readonly string PnpUtilPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

        private List<USBDevice> virpilDevices = new();
        private readonly StringBuilder logBuffer = new();

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "VPC (VIRPIL) USB Device Cleaner";

            if (!IsRunningAsAdmin())
            {
                Log("✗ ERREUR : Droits administrateur requis.");
                Log("  → Lancez via LaunchAsAdmin.bat ou clic droit → Exécuter en tant qu'administrateur.");
                SetButtonsEnabled(false);
                return;
            }

            Log("✓ Administrateur confirmé.\n");
            _ = LoadDevicesAsync();
        }

        private static bool IsRunningAsAdmin()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
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
            Log($"Recherche {VIRPIL_VID} dans le registre...\n");

            var found = await Task.Run(ScanRegistry);
            virpilDevices = found;

            if (virpilDevices.Count == 0)
            {
                Log("⚠  Aucun périphérique VIRPIL trouvé.");
                Log("   → Registre déjà propre, ou appareils jamais branchés sur ce PC.");
            }
            else
            {
                Log($"\n✓ {virpilDevices.Count} instance(s) trouvée(s). Cliquez 'Supprimer'.");
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
                    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                    using var enumKey = hklm.OpenSubKey(regPath);
                    if (enumKey == null) continue;

                    foreach (string vidPidKey in enumKey.GetSubKeyNames())
                    {
                        if (!vidPidKey.ToUpperInvariant().Contains(VIRPIL_VID)) continue;

                        using var deviceKey = enumKey.OpenSubKey(vidPidKey);
                        if (deviceKey == null) continue;

                        foreach (string instanceId in deviceKey.GetSubKeyNames())
                        {
                            using var instanceKey = deviceKey.OpenSubKey(instanceId);
                            string name = instanceKey?.GetValue("FriendlyName")?.ToString()
                                       ?? instanceKey?.GetValue("DeviceDesc")?.ToString()
                                       ?? "(non reconnu)";

                            string pnpId = $@"{bus}\{vidPidKey}\{instanceId}";
                            result.Add(new USBDevice { Name = name, PnpInstanceId = pnpId, Bus = bus });
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
                MessageBox.Show("Aucun périphérique VIRPIL dans le registre.", "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Supprimer {virpilDevices.Count} instance(s) VIRPIL ?\n\n" +
                "Redémarrez le PC ensuite, puis rebranchez les contrôleurs.",
                "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            ClearLog();
            Log("=== Suppression VIRPIL ===\n");

            var snapshot = virpilDevices.ToList();

            // Étape 1 : pnputil /remove-device pour chaque instance
            Log("── Étape 1 : pnputil /remove-device...\n");
            foreach (var device in snapshot)
                await RemoveViaPnpUtil(device);

            // Étape 2 : DeviceClasses registry cleanup
            Log("\n── Étape 2 : nettoyage DeviceClasses...\n");
            int cleaned = await Task.Run(CleanDeviceClasses);
            Log($"  {cleaned} entrée(s) supprimée(s).");

            Log("\n══════════════════════════");
            Log("→ Redémarrez le PC");
            Log("→ Rebranchez les contrôleurs VIRPIL");
            Log("→ Windows réinstalle les drivers automatiquement");

            await Task.Delay(600);
            await LoadDevicesAsync();
        }

        private async Task RemoveViaPnpUtil(USBDevice device)
        {
            Log($"  [{device.Bus}] {device.Name}");
            Log($"    {device.PnpInstanceId}");

            // BUG CLASSIQUE : lire stdout ET stderr en parallèle (async).
            // Si on lit l'un puis l'autre séquentiellement, le buffer de l'un
            // se remplit pendant qu'on attend l'autre → deadlock permanent.
            var (stdout, stderr, exitCode) = await RunProcessAsync(
                PnpUtilPath,
                $"/remove-device \"{device.PnpInstanceId}\" /uninstall");

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Log($"    {line.TrimEnd()}");

            if (!string.IsNullOrWhiteSpace(stderr))
                Log($"    ⚠ stderr: {stderr.Trim()}");

            Log(exitCode == 0 ? "    ✓ OK" : $"    ⚠ Code retour: {exitCode}");
            Log("");
        }

        // Lecture stdout + stderr en parallèle pour éviter le deadlock
        private static async Task<(string Stdout, string Stderr, int ExitCode)> RunProcessAsync(
            string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;

            // Lecture simultanée indispensable — ne jamais faire ReadToEnd() l'un après l'autre
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync();

            return (stdoutTask.Result, stderrTask.Result, proc.ExitCode);
        }

        // ── DEVICE CLASSES CLEANUP ────────────────────────────────────────

        private int CleanDeviceClasses()
        {
            int count = 0;
            const string dcPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses";

            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                using var dcKey = hklm.OpenSubKey(dcPath);
                if (dcKey == null) return 0;

                foreach (string classGuid in dcKey.GetSubKeyNames())
                {
                    try
                    {
                        using var classKey = hklm.OpenSubKey($@"{dcPath}\{classGuid}", writable: true);
                        if (classKey == null) continue;

                        foreach (string entry in classKey.GetSubKeyNames()
                            .Where(k => k.ToUpperInvariant().Contains(VIRPIL_VID))
                            .ToList())
                        {
                            try
                            {
                                classKey.DeleteSubKeyTree(entry, throwOnMissingSubKey: false);
                                string shortEntry = entry.Length > 60 ? entry[..60] + "…" : entry;
                                Log($"  ✓ DeviceClasses\\{classGuid[..8]}...\\{shortEntry}");
                                count++;
                            }
                            catch (Exception ex) { Log($"  ⚠ {ex.Message}"); }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log($"  ⚠ DeviceClasses: {ex.Message}"); }

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
