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
        private static readonly string PnpUtil =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

        // IDs exacts tels que rapportés par pnputil lui-même
        private List<string> foundInstanceIds = new();
        private readonly StringBuilder logBuffer = new();

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "VPC (VIRPIL) USB Device Cleaner";

            if (!IsRunningAsAdmin())
            {
                Log("ERREUR : droits administrateur requis.");
                Log("Relancez via LaunchAsAdmin.bat ou clic droit -> Exécuter en tant qu'administrateur.");
                SetButtonsEnabled(false);
                return;
            }

            Log("OK - Droits administrateur confirmes.\n");
            _ = ScanAsync();
        }

        private static bool IsRunningAsAdmin()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private async void LoadDevices_Click(object sender, RoutedEventArgs e)
        {
            SetButtonsEnabled(false);
            await ScanAsync();
            SetButtonsEnabled(true);
        }

        private async void RemoveDevices_Click(object sender, RoutedEventArgs e)
        {
            SetButtonsEnabled(false);
            await RemoveAsync();
            SetButtonsEnabled(true);
        }

        private void SetButtonsEnabled(bool enabled)
        {
            RefreshButton.IsEnabled = enabled;
            RemoveButton.IsEnabled = enabled;
        }

        // ─────────────────────────────────────────────────────────────────
        // SCAN : pnputil énumère tous les appareils, on filtre VID_3344
        // On utilise pnputil /enum-devices /ids pour avoir les Instance IDs
        // exacts que pnputil utilise en interne (pas le registre directement)
        // ─────────────────────────────────────────────────────────────────

        private async Task ScanAsync()
        {
            ClearLog();
            Log($"--- Recherche des périphériques VIRPIL (VID_3344) ---\n");
            Log($"Outil  : {PnpUtil}");
            Log($"Commande: pnputil /enum-devices /ids\n");

            var ids = await Task.Run(FindVirpilInstanceIds);
            foundInstanceIds = ids;

            if (foundInstanceIds.Count == 0)
            {
                Log("=> Aucun périphérique VID_3344 trouve.");
                Log("   Le registre est deja propre, ou les appareils n'ont jamais ete branches.");
            }
            else
            {
                Log($"=> {foundInstanceIds.Count} instance(s) trouvee(s) :\n");
                for (int i = 0; i < foundInstanceIds.Count; i++)
                    Log($"  [{i + 1}] {foundInstanceIds[i]}");
                Log("\nCliquez 'Supprimer' pour les effacer.");
            }
        }

        private List<string> FindVirpilInstanceIds()
        {
            var result = new List<string>();

            // Lancer pnputil /enum-devices /ids
            // /ids : affiche les Hardware IDs (contiennent VID_3344)
            // sans /connected ni /disconnected = tous appareils (branches + ghosts)
            string rawOutput = RunProcessSync(PnpUtil, "/enum-devices /ids");

            Log("--- Sortie brute de pnputil ---");
            // On montre uniquement les lignes pertinentes pour ne pas surcharger
            foreach (var line in rawOutput.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("Instance ID:") || t.ToUpperInvariant().Contains(VIRPIL_VID))
                    Log($"  {t}");
            }
            Log("--- Fin sortie pnputil ---\n");

            // Parser : on cherche les blocs "Instance ID:" dont les Hardware IDs contiennent VID_3344
            string? currentId = null;
            bool isVirpil = false;

            foreach (string rawLine in rawOutput.Split('\n'))
            {
                string line = rawLine.Trim();

                if (line.StartsWith("Instance ID:", StringComparison.OrdinalIgnoreCase))
                {
                    // Sauvegarder le precedent si c'etait VIRPIL
                    if (currentId != null && isVirpil && !result.Contains(currentId))
                        result.Add(currentId);

                    currentId = line.Substring(line.IndexOf(':') + 1).Trim();
                    isVirpil = false;
                }
                else if (line.ToUpperInvariant().Contains(VIRPIL_VID))
                {
                    isVirpil = true;
                }
            }
            // Dernier bloc
            if (currentId != null && isVirpil && !result.Contains(currentId))
                result.Add(currentId);

            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // SUPPRESSION : un par un, sortie complete affichee pour chaque
        // ─────────────────────────────────────────────────────────────────

        private async Task RemoveAsync()
        {
            if (foundInstanceIds.Count == 0)
            {
                MessageBox.Show("Aucun peripherique VIRPIL trouve. Rien a supprimer.",
                    "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Supprimer {foundInstanceIds.Count} instance(s) VIRPIL ?\n\n" +
                "Apres suppression, DEBRANCHEZ immediatement les\n" +
                "controleurs pour eviter que Windows les reenumere.\n\n" +
                "Redemarrez le PC ensuite.",
                "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            ClearLog();
            Log($"=== SUPPRESSION {foundInstanceIds.Count} instance(s) VIRPIL ===\n");

            int ok = 0;
            int fail = 0;

            for (int i = 0; i < foundInstanceIds.Count; i++)
            {
                string instanceId = foundInstanceIds[i];
                Log($"[{i + 1}/{foundInstanceIds.Count}] {instanceId}");

                string args = $"/remove-device \"{instanceId}\" /uninstall";
                Log($"  Commande: pnputil {args}");

                // Executer et afficher TOUTE la sortie
                string output = await Task.Run(() => RunProcessSync(PnpUtil, args));

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Log($"  {line.TrimEnd()}");

                // pnputil retourne 0 si succes
                bool success = output.ToUpperInvariant().Contains("OK") ||
                               output.ToUpperInvariant().Contains("SUCCESS") ||
                               output.ToUpperInvariant().Contains("REUSSI") ||
                               !output.ToUpperInvariant().Contains("ECHEC") &&
                               !output.ToUpperInvariant().Contains("FAILED") &&
                               !output.ToUpperInvariant().Contains("ERROR");

                if (success) ok++; else fail++;
                Log("");
            }

            // Nettoyage DeviceClasses
            Log("\n--- Nettoyage DeviceClasses (entrees restantes) ---\n");
            int cleaned = await Task.Run(CleanDeviceClasses);
            Log($"{cleaned} entree(s) DeviceClasses supprimee(s).\n");

            Log($"=== TERMINE : {ok} OK / {fail} echec(s) ===");
            Log("\n!!! DEBRANCHEZ VOS CONTROLEURS VIRPIL MAINTENANT !!!");
            Log("Puis redemarrez le PC, et rebranchez pour reinstaller.");

            // Rescan pour confirmer
            await Task.Delay(1500);
            await ScanAsync();
        }

        // ─────────────────────────────────────────────────────────────────
        // PROCESS : lecture stdout+stderr en parallele (evite deadlock)
        // ─────────────────────────────────────────────────────────────────

        private static string RunProcessSync(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var proc = Process.Start(psi)!;

            // Lecture simultanee stdout + stderr pour eviter deadlock
            // (si buffer stderr plein pendant ReadToEnd(stdout) -> deadlock)
            string stdout = "";
            string stderr = "";

            var stdoutThread = new System.Threading.Thread(() => stdout = proc.StandardOutput.ReadToEnd());
            var stderrThread = new System.Threading.Thread(() => stderr = proc.StandardError.ReadToEnd());

            stdoutThread.Start();
            stderrThread.Start();
            proc.WaitForExit();
            stdoutThread.Join();
            stderrThread.Join();

            string combined = stdout;
            if (!string.IsNullOrWhiteSpace(stderr))
                combined += "\n[STDERR] " + stderr;

            return combined;
        }

        // ─────────────────────────────────────────────────────────────────
        // DEVICECLASSES : nettoyage registre des interfaces de classe
        // ─────────────────────────────────────────────────────────────────

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
                            .Where(k => k.ToUpperInvariant().Contains(VIRPIL_VID)).ToList())
                        {
                            try
                            {
                                classKey.DeleteSubKeyTree(entry, throwOnMissingSubKey: false);
                                Log($"  OK DeviceClasses\\...\\{(entry.Length > 50 ? entry[..50] + "..." : entry)}");
                                count++;
                            }
                            catch (Exception ex) { Log($"  FAIL {ex.Message}"); }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log($"  FAIL DeviceClasses: {ex.Message}"); }

            return count;
        }

        // ─────────────────────────────────────────────────────────────────
        // LOG
        // ─────────────────────────────────────────────────────────────────

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
}
