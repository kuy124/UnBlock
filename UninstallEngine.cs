using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Watcher maintenance mode, native uninstaller, and cleanup helper.
internal static class Uninstaller {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    internal static void RunWatcherMode(string targetDir) {
        string targetExe = Path.Combine(targetDir, "Unlocker.exe");

        while (true) {
            Thread.Sleep(1500);

            bool isAppAvailable = File.Exists(targetExe);
            bool keysCurrentlyRegistered = AreContextKeysRegistered();

            if (isAppAvailable && !keysCurrentlyRegistered) {
                RestoreRegistryKeys(targetExe);
            }
            else if (!isAppAvailable && keysCurrentlyRegistered) {
                // Install folder was deleted manually -> purge every leftover immediately
                PerformUninstallSteps(false);
                SpawnCleanupHelper(Process.GetCurrentProcess().Id,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnBlock"));
                Environment.Exit(0);
            }
        }
    }

    private static bool AreContextKeysRegistered() {
        try {
            using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)) {
                using (var k = baseKey.OpenSubKey(@"SOFTWARE\Classes\Directory\shell\UnBlock")) {
                    return k != null;
                }
            }
        } catch {
            return true;
        }
    }

    internal static void RunInteractiveUninstall(bool silent) {
        if (!IsCurrentUserElevated()) {
            try {
                ProcessStartInfo relaunch = new ProcessStartInfo();
                relaunch.FileName = Application.ExecutablePath;
                relaunch.Arguments = "[UNINSTALL]" + (silent ? " /SILENT" : "");
                relaunch.Verb = "runas";
                Process.Start(relaunch);
                return;
            } catch { }
        }

        if (!silent) {
            DialogResult choice = MessageBox.Show(
                "This will completely remove UnBlock from your computer:\n\n" +
                "ΓÇó  Right-click context menu entries\n" +
                "ΓÇó  Background maintenance task\n" +
                "ΓÇó  All leftover files and folders\n\n" +
                "Continue with the uninstall?",
                "Uninstall UnBlock", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;
        }

        PerformUninstallSteps(true);

        string installDir = "";
        try { installDir = Path.GetDirectoryName(Application.ExecutablePath); } catch { }
        string localAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnBlock");
        SpawnCleanupHelper(Process.GetCurrentProcess().Id, installDir, localAppDataDir);

        if (!silent) {
            MessageBox.Show("UnBlock has been completely uninstalled.", "Uninstall Complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static bool IsCurrentUserElevated() {
        WindowsIdentity id = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(id);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void PerformUninstallSteps(bool killInstances) {
        if (killInstances) KillRunningInstances();
        DeleteScheduledTask();
        CleanRegistryOnly();
        RemoveClassicMenuPreference();
        try {
            string localAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnBlock");
            DeleteDirectoryWithRetry(localAppDataDir);
        } catch { }
    }

    private static void RemoveClassicMenuPreference() {
        try {
            using (Microsoft.Win32.RegistryKey marker = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\UnBlock")) {
                if (marker == null || marker.GetValue("ClassicMenu") == null) return;
            }
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\UnBlock", false);
        } catch { }
    }

    private static void KillRunningInstances() {
        int currentPid = Process.GetCurrentProcess().Id;
        foreach (string name in new string[] { "Unlocker", "UnBlockWatcher" }) {
            Process[] procs = Process.GetProcessesByName(name);
            foreach (Process p in procs) {
                try {
                    if (p.Id == currentPid) continue;
                    p.Kill();
                    p.WaitForExit(3000);
                } catch { }
                finally {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

    private static void DeleteScheduledTask() {
        RunHiddenProcess("schtasks.exe", "/delete /tn \"UnBlock-Cleanup\" /f");
    }

    private static void RunHiddenProcess(string fileName, string arguments) {
        try {
            using (Process p = new Process()) {
                p.StartInfo.FileName = fileName;
                p.StartInfo.Arguments = arguments;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.Start();
                p.WaitForExit(10000);
            }
        } catch { }
    }

    private static void DeleteDirectoryWithRetry(string path) {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        for (int i = 0; i < 6; i++) {
            try {
                Directory.Delete(path, true);
                if (!Directory.Exists(path)) return;
            } catch { }
            Thread.Sleep(400);
        }
    }

    private static void SpawnCleanupHelper(int pidToWait, params string[] directories) {
        try {
            string self = Application.ExecutablePath;
            string helperPath = Path.Combine(Path.GetTempPath(), "UnBlockCleanup-" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".tmp");
            File.Copy(self, helperPath, true);

            StringBuilder argBuilder = new StringBuilder("[CLEANUP] " + pidToWait);
            foreach (string dir in directories) {
                if (!string.IsNullOrEmpty(dir)) argBuilder.Append(" \"" + dir + "\"");
            }

            ProcessStartInfo psi = new ProcessStartInfo(helperPath, argBuilder.ToString());
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        } catch { }
    }

    internal static void RunCleanupHelper(string[] args) {
        try {
            int pidToWait = int.Parse(args[1]);
            try {
                using (Process p = Process.GetProcessById(pidToWait)) {
                    p.WaitForExit(20000);
                }
            } catch { }

            DeleteScheduledTask();
            CleanRegistryOnly();

            for (int i = 2; i < args.Length; i++) {
                DeleteDirectoryWithRetry(args[i]);
            }

            SelfDestruct();
        } catch { }
        Environment.Exit(0);
    }

    private static void SelfDestruct() {
        try {
            string self = Application.ExecutablePath;
            string renamed = self + ".pending-delete";
            try { File.Move(self, renamed); } catch { renamed = self; }
            MoveFileEx(renamed, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        } catch { }
    }

    private static void CleanRegistryOnly() {
        try {
            using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)) {
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\*\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\Directory\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\Directory\Background\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\Drive\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UnBlock", false);
            }
        } catch { }
    }

    private static void RestoreRegistryKeys(string exePath) {
        try {
            using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)) {
                
                // 1. Restore: Right Click -> Files
                using (var k = baseKey.CreateSubKey(@"SOFTWARE\Classes\*\shell\UnBlock")) {
                    k.SetValue("", "UnBlock");
                    k.SetValue("Icon", "shell32.dll,239");
                    using (var cmd = k.CreateSubKey("command")) {
                        cmd.SetValue("", string.Format("\"{0}\" \"%1\"", exePath));
                    }
                }
                
                // 2. Restore: Right Click -> Folders
                using (var k = baseKey.CreateSubKey(@"SOFTWARE\Classes\Directory\shell\UnBlock")) {
                    k.SetValue("", "UnBlock");
                    k.SetValue("Icon", "shell32.dll,239");
                    using (var cmd = k.CreateSubKey("command")) {
                        cmd.SetValue("", string.Format("\"{0}\" \"%1\"", exePath));
                    }
                }
                
                // 3. Restore: Right Click -> Empty Space
                using (var k = baseKey.CreateSubKey(@"SOFTWARE\Classes\Directory\Background\shell\UnBlock")) {
                    k.SetValue("", "UnBlock This Folder");
                    k.SetValue("Icon", "shell32.dll,239");
                    using (var cmd = k.CreateSubKey("command")) {
                        cmd.SetValue("", string.Format("\"{0}\" \"%V\"", exePath));
                    }
                }

                // 4. Restore: Right Click -> Drives
                using (var k = baseKey.CreateSubKey(@"SOFTWARE\Classes\Drive\shell\UnBlock")) {
                    k.SetValue("", "UnBlock");
                    k.SetValue("Icon", "shell32.dll,239");
                    using (var cmd = k.CreateSubKey("command")) {
                        cmd.SetValue("", string.Format("\"{0}\" \"%1\"", exePath));
                    }
                }
            }
        } catch { }
    }
}
