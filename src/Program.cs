using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Program {
    private static Mutex singleInstanceMutex;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    public static void Main(string[] args) {
        if (args.Length == 1 && args[0] == "[WARMUP]") {
            try { 
                UnlockerForm.InitFileTypeIndex();
                UnlockerForm.RefreshProcessSnapshot(true); 
            } catch {}
            return;
        }

        // --- DYNAMIC BACKGROUND WATCHER BOOTSTRAP ---
        if (args.Length >= 2 && args[0] == "[WATCHER]") {
            Uninstaller.RunWatcherMode(args[1]);
            return;
        }

        // --- NATIVE UNINSTALLER (NO CONSOLE) ---
        bool silentUninstall = false;
        if (args.Length >= 1 && args[0] == "[UNINSTALL]") {
            foreach (string a in args) {
                if (string.Equals(a, "/SILENT", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase)) silentUninstall = true;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Uninstaller.RunInteractiveUninstall(silentUninstall);
            return;
        }

        // --- INTERNAL POST-EXIT CLEANUP HELPER ---
        if (args.Length >= 2 && args[0] == "[CLEANUP]") {
            Uninstaller.RunCleanupHelper(args);
            return;
        }

        // --- DEDICATED UNINSTALLER BINARY (uninstall.exe) ---
        try {
            if (Path.GetFileNameWithoutExtension(Application.ExecutablePath).Equals("uninstall", StringComparison.OrdinalIgnoreCase)) {
                bool silent = false;
                foreach (string a in args) {
                    if (string.Equals(a, "/SILENT", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase)) silent = true;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Uninstaller.RunInteractiveUninstall(silent);
                return;
            }
        } catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try {
            if (Environment.OSVersion.Version.Major >= 6) {
                SetProcessDPIAware();
            }
        } catch { }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pendingDir = Path.Combine(appData, "UnBlock\\Pending");
        Directory.CreateDirectory(pendingDir);

        bool createdNew = true;
        try {
            singleInstanceMutex = new Mutex(true, "Global\\UnBlock_SingleInstance_Mutex", out createdNew);
        } catch (UnauthorizedAccessException) {
            try {
                singleInstanceMutex = new Mutex(true, "Local\\UnBlock_SingleInstance_Mutex", out createdNew);
            } catch {
                createdNew = true;
            }
        } catch {
            createdNew = true;
        }

        if (!createdNew) {
            if (args.Length > 0) {
                try {
                    string tempBase = Path.Combine(pendingDir, Guid.NewGuid().ToString());
                    string tempWritePath = tempBase + ".tmp_write";
                    string tempFinalPath = tempBase + ".tmp";
                    
                    File.WriteAllLines(tempWritePath, args);
                    File.Move(tempWritePath, tempFinalPath);
                } catch { }
            }
            return; 
        }

        List<string> initialPaths = new List<string>();
        foreach (string arg in args) {
            if (!string.IsNullOrEmpty(arg) && arg != "[WARMUP]") {
                initialPaths.Add(arg.Trim('"', ' '));
            }
        }

        Application.Run(new UnlockerForm(initialPaths));

        if (singleInstanceMutex != null) {
            try { singleInstanceMutex.ReleaseMutex(); } catch { }
            singleInstanceMutex.Dispose();
        }
    }
}
