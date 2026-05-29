using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace VirpilCleanup
{
    public partial class MainWindow : Window
    {
        private const string VIRPIL_VID = "VID_3344";
        private const int PROCESS_TIMEOUT_MS = 30_000;

        private static readonly string PnpUtil =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

        private List<string> foundInstanceIds = new();

        // Lock pour protéger logBuffer : Log() est appelé depuis Task.Run (threads background)
        // StringBuilder n'est pas thread-safe — sans lock, ToString() peut corrompre la mémoire
        private readonly object logLock = new();
        private readonly StringBuilder logBuffer = new();

        public MainWindow()
        {
            InitializeComponent();
            this.Title = "VPC (VIRPIL) USB Device Cleaner";

            if (!IsRunningAsAdmin())
            {
                Log("ERREUR : droits administrateur requis.");
                Log("Relancez via LaunchAsAdmin.bat ou clic droit -> Executer en tant qu'administrateur.");
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
            try   { await ScanAsync(); }
            finally { SetButtonsEnabled(true); }   // toujours réactivé même si exception
        }

        private async void RemoveDevices_Click(object sender, RoutedEventArgs e)
        {
            SetButtonsEnabled(false);
            try   { await RemoveAsync(); }
            finally { SetButtonsEnabled(true); }   // toujours réactivé même si exception
        }

        private void SetButtonsEnabled(bool enabled)
        {
            RefreshButton.IsEnabled = enabled;
            RemoveButton.IsEnabled = enabled;
        }

        // ─────────────────────────────────────────────────────────────────
        // SCAN  –  deux passes pnputil indépendantes, résultats fusionnés
        // ─────────────────────────────────────────────────────────────────

        private async Task ScanAsync()
        {
            ClearLog();
            Log($"--- Recherche VIRPIL ({VIRPIL_VID}) ---\n");
            Log($"Outil : {PnpUtil}\n");

            var ids = await Task.Run(FindVirpilInstanceIds);
            foundInstanceIds = ids;

            if (foundInstanceIds.Count == 0)
            {
                Log("=> Aucun peripherique VID_3344 trouve.");
                Log("   Registre deja propre, ou appareils jamais branches.");
            }
            else
            {
                Log($"=> {foundInstanceIds.Count} instance(s) :\n");
                for (int i = 0; i < foundInstanceIds.Count; i++)
                    Log($"  [{i + 1}] {foundInstanceIds[i]}");
                Log("\nCliquez 'Supprimer' pour tout effacer.");
            }
        }

        private List<string> FindVirpilInstanceIds()
        {
            // Passe 1 : appareils connectés
            // Passe 2 : ghosts (non branchés, encore dans le registre)
            //
            // BUG CORRIGE : les deux passes sont parsées SÉPARÉMENT puis fusionnées.
            // Concaténer les deux sorties brutes puis parser d'une traite provoquait
            // un carry-over d'état entre les passes : si pass1 se terminait avec
            // isVirpil=true, le premier appareil de pass2 héritait ce flag et était
            // faussement ajouté à la liste même s'il n'était pas VIRPIL.
            var pass1 = ParseVirpilIds(RunProcess(PnpUtil, "/enum-devices /ids").Stdout,         "PASSE 1 (connectes)");
            var pass2 = ParseVirpilIds(RunProcess(PnpUtil, "/enum-devices /ids /disconnected").Stdout, "PASSE 2 (ghosts)");

            // Déduplication : un appareil connecté peut apparaître dans les deux passes
            return pass1.Concat(pass2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Regex langue-agnostique : cherche ":   USB\..." ou ":   HID\..."
        // Ne dépend PAS du libellé ("Instance ID:" EN ou "ID d'instance :" FR)
        // évite tous les problèmes d'apostrophe ou de locale
        private static readonly Regex InstanceIdRegex =
            new Regex(@":\s+((?:USB|HID)\\[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private List<string> ParseVirpilIds(string pnputilStdout, string passLabel)
        {
            var result = new List<string>();

            Log($"--- {passLabel} ---");
            foreach (var line in pnputilStdout.Split('\n'))
            {
                string t = line.Trim();
                if (t.ToUpperInvariant().Contains(VIRPIL_VID) || InstanceIdRegex.IsMatch(t))
                    Log($"  {t}");
            }
            Log("");

            string? currentId = null;
            bool isVirpil = false;

            foreach (string rawLine in pnputilStdout.Split('\n'))
            {
                string line = rawLine.Trim();
                var m = InstanceIdRegex.Match(line);

                if (m.Success)
                {
                    if (currentId != null && isVirpil)
                        result.Add(currentId);

                    currentId = m.Groups[1].Value.Trim();
                    isVirpil = false;
                }
                else if (line.ToUpperInvariant().Contains(VIRPIL_VID))
                {
                    isVirpil = true;
                }
            }
            if (currentId != null && isVirpil)
                result.Add(currentId);

            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // SUPPRESSION  –  un par un, sortie complète affichée
        // ─────────────────────────────────────────────────────────────────

        private async Task RemoveAsync()
        {
            if (foundInstanceIds.Count == 0)
            {
                MessageBox.Show("Aucun peripherique VIRPIL trouve.", "Info",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Supprimer {foundInstanceIds.Count} instance(s) VIRPIL ?\n\n" +
                "Apres la suppression, DEBRANCHEZ immediatement\n" +
                "les controleurs (sinon Windows les reenumere).\n\n" +
                "Redemarrez le PC ensuite.",
                "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            ClearLog();
            Log($"=== SUPPRESSION {foundInstanceIds.Count} instance(s) VIRPIL ===\n");

            // HID enfants en premier, puis USB parents
            // Supprimer un parent USB avant ses enfants HID peut bloquer la suppression
            var snapshot = foundInstanceIds
                .OrderBy(id => id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToList();

            Log("Ordre de suppression : HID (enfants) avant USB (parents)\n");

            int ok = 0, fail = 0;

            for (int i = 0; i < snapshot.Count; i++)
            {
                string instanceId = snapshot[i];
                Log($"[{i + 1}/{snapshot.Count}] {instanceId}");

                string args = $"/remove-device \"{instanceId}\" /subtree /uninstall /force";
                Log($"  pnputil {args}");

                var r = await Task.Run(() => RunProcess(PnpUtil, args));

                foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Log($"  {line.TrimEnd()}");

                if (!string.IsNullOrWhiteSpace(r.Stderr))
                    foreach (var line in r.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        Log($"  [ERR] {line.TrimEnd()}");

                if (r.TimedOut)
                {
                    Log($"  TIMEOUT ({PROCESS_TIMEOUT_MS / 1000}s) - pnputil ne repond pas");
                    fail++;
                }
                else
                {
                    Log($"  Code retour : {r.ExitCode}");
                    if (r.ExitCode == 0) ok++; else fail++;
                }
                Log("");
            }

            Log("\n--- Nettoyage DeviceClasses ---\n");
            int cleaned = await Task.Run(CleanDeviceClasses);
            Log($"{cleaned} entree(s) DeviceClasses supprimee(s).\n");

            Log("--- Nettoyage Enum\\USB et Enum\\HID ---\n");
            int enumCleaned = await Task.Run(CleanEnumRegistry);
            Log($"{enumCleaned} entree(s) Enum supprimee(s).\n");

            Log($"=== TERMINE : {ok} OK  /  {fail} echec(s) ===");

            // Pause obligatoire : Windows reenumere les peripheriques encore branches.
            // L'utilisateur doit debrancher AVANT qu'on rescanne.
            MessageBox.Show(
                $"Suppression terminee : {ok} OK, {fail} echec(s).\n\n" +
                "DEBRANCHEZ MAINTENANT tous vos controleurs VIRPIL/VPC\n" +
                "avant de cliquer OK.\n\n" +
                "Si vous ne les debranchez pas, Windows va les reenumerer\n" +
                "immediatement et ils reapparaitront dans la liste.",
                "Action requise", MessageBoxButton.OK, MessageBoxImage.Warning);

            await ScanAsync();
        }

        // ─────────────────────────────────────────────────────────────────
        // PROCESS  –  threads séparés stdout/stderr, timeout 30s
        // ─────────────────────────────────────────────────────────────────

        private record ProcessResult(string Stdout, string Stderr, int ExitCode, bool TimedOut);

        private static ProcessResult RunProcess(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)!;

            string stdout = "";
            string stderr = "";

            // Deux threads pour lire stdout+stderr simultanément
            // (ReadToEnd séquentiel → deadlock si buffers pleins)
            var t1 = new Thread(() => stdout = proc.StandardOutput.ReadToEnd()) { IsBackground = true };
            var t2 = new Thread(() => stderr = proc.StandardError.ReadToEnd())  { IsBackground = true };
            t1.Start();
            t2.Start();

            // Timeout pour éviter un blocage infini si pnputil freeze
            bool finished = proc.WaitForExit(PROCESS_TIMEOUT_MS);
            if (!finished)
            {
                try { proc.Kill(); } catch { /* déjà mort */ }
            }

            t1.Join(5_000);
            t2.Join(5_000);

            return new ProcessResult(stdout, stderr, finished ? proc.ExitCode : -1, !finished);
        }

        // ─────────────────────────────────────────────────────────────────
        // DEVICECLASSES  –  nettoyage registre
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
                                string label = entry.Length > 50 ? entry[..50] + "..." : entry;
                                Log($"  OK {classGuid[..8]}...\\{label}");
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
        // ENUM  –  nettoyage des arbres USB et HID en registre
        // ─────────────────────────────────────────────────────────────────

        private int CleanEnumRegistry()
        {
            int count = 0;
            const string enumUsbPath = @"SYSTEM\CurrentControlSet\Enum\USB";
            const string enumHidPath = @"SYSTEM\CurrentControlSet\Enum\HID";

            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                count += CleanEnumPath(hklm, enumUsbPath);
                count += CleanEnumPath(hklm, enumHidPath);
            }
            catch (Exception ex) { Log($"  FAIL CleanEnum: {ex.Message}"); }

            return count;
        }

        private int CleanEnumPath(RegistryKey hklm, string enumPath)
        {
            int count = 0;

            try
            {
                using var enumKey = hklm.OpenSubKey(enumPath);
                if (enumKey == null) return 0;

                foreach (string vendorId in enumKey.GetSubKeyNames().ToList())
                {
                    if (!vendorId.ToUpperInvariant().Contains(VIRPIL_VID)) continue;

                    try
                    {
                        using var vendorKey = hklm.OpenSubKey($@"{enumPath}\{vendorId}", writable: true);
                        if (vendorKey == null) continue;

                        foreach (string deviceId in vendorKey.GetSubKeyNames().ToList())
                        {
                            try
                            {
                                vendorKey.DeleteSubKeyTree(deviceId, throwOnMissingSubKey: false);
                                Log($"  OK {enumPath[(enumPath.LastIndexOf('\\') + 1)..]}\\{vendorId}\\{deviceId}");
                                count++;
                            }
                            catch (Exception ex) { Log($"  FAIL {ex.Message}"); }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return count;
        }

        // ─────────────────────────────────────────────────────────────────
        // LOG  –  thread-safe (lock sur logBuffer)
        // ─────────────────────────────────────────────────────────────────

        private void Log(string message)
        {
            // BUG CORRIGE : StringBuilder n'est pas thread-safe.
            // Log() est appelé depuis Task.Run() (thread pool) et depuis le thread UI.
            // Sans lock, ToString() pendant un AppendLine() concurrent corrompt l'état interne.
            string snapshot;
            lock (logLock)
            {
                logBuffer.AppendLine(message);
                snapshot = logBuffer.ToString();
            }
            Dispatcher.BeginInvoke(() =>
            {
                LogTextBox.Text = snapshot;
                LogTextBox.ScrollToEnd();
            });
        }

        private void ClearLog()
        {
            lock (logLock) { logBuffer.Clear(); }
            Dispatcher.Invoke(() => LogTextBox.Text = "");
        }
    }
}
