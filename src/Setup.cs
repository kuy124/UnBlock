using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Dedicated GUI installer: compiles the application sources locally,
// registers everything, then removes itself and the src folder.
// Compiled binary is shipped as setup.exe (see README for rebuild instructions).
internal static class Setup {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    [STAThread]
    private static void Main(string[] args) {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length >= 2 && args[0] == "[SETUPCLEANUP]") {
            RunSetupCleanup(args);
            return;
        }

        try { Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System); } catch { }

        bool silent = false;
        bool keepSetup = false;
        string customDir = null;
        StringBuilder relaunchArgs = new StringBuilder();
        foreach (string a in args) {
            if (string.Equals(a, "/SILENT", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase)) {
                silent = true;
                relaunchArgs.Append(" /SILENT");
            } else if (string.Equals(a, "/KEEPSETUP", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/K", StringComparison.OrdinalIgnoreCase)) {
                keepSetup = true;
                relaunchArgs.Append(" /KEEPSETUP");
            } else if (a.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase)) {
                customDir = a.Substring(5).Trim('"');
                relaunchArgs.Append(" \"" + a + "\"");
            }
        }

        if (!IsElevated()) {
            try {
                ProcessStartInfo relaunch = new ProcessStartInfo();
                relaunch.FileName = Application.ExecutablePath;
                relaunch.Arguments = relaunchArgs.ToString().Trim();
                relaunch.Verb = "runas";
                Process.Start(relaunch);
                return;
            } catch { }
        }

        string setupExe = Application.ExecutablePath;
        string baseDir = "";
        try { baseDir = Path.GetDirectoryName(setupExe); } catch { }
        string srcDir = Path.Combine(baseDir, "src");

        // Developer protection: a source checkout keeps a .git entry next to
        // setup.exe, so the setup never deletes itself or the sources there.
        bool developerCopy = Directory.Exists(Path.Combine(baseDir, ".git")) ||
                             File.Exists(Path.Combine(baseDir, ".git"));

        List<string> sources = new List<string>();
        try {
            if (Directory.Exists(srcDir)) {
                foreach (string f in Directory.GetFiles(srcDir, "*.cs")) {
                    if (!Path.GetFileName(f).Equals("Setup.cs", StringComparison.OrdinalIgnoreCase)) sources.Add(f);
                }
            }
        } catch { }

        if (sources.Count == 0) {
            MessageBox.Show("Could not find the 'src' folder with the .cs source files next to setup.exe.\n\nPlease make sure the entire package (setup.exe + src folder) is extracted together before running.", "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string installDir = customDir;
        if (string.IsNullOrEmpty(installDir)) {
            if (silent) {
                installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UnBlock");
            } else {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog()) {
                    dlg.Description = "Select where you want to install UnBlock. (An 'UnBlock' folder will be created inside your selection).";
                    dlg.ShowNewFolderButton = true;
                    dlg.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    installDir = Path.Combine(dlg.SelectedPath, "UnBlock");
                }
            }
        }

        KillRunningInstances();

        string exePath = Path.Combine(installDir, "Unlocker.exe");
        try {
            Directory.CreateDirectory(installDir);
            CompileApplication(sources, exePath);

            string uninstallExePath = Path.Combine(installDir, "uninstall.exe");
            File.Copy(exePath, uninstallExePath, true);

            RegisterContextMenu(exePath);
            RegisterArpEntry(installDir, exePath, uninstallExePath);
            DeployWatcher(exePath, installDir);
            RunWarmup(exePath);
            OfferWindows11Menu();
        } catch (Exception ex) {
            MessageBox.Show("Setup failed!\n\n" + ex.Message, "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!silent) {
            string note = "";
            if (developerCopy || keepSetup) {
                note = "\n\n(Developer mode - setup.exe and the src folder were kept.)";
            }
            MessageBox.Show("UnBlock was installed successfully!\n\nYou can now Right-Click any locked file or folder and choose UnBlock.\n\nInstalled to:\n" + installDir + note, "Setup Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Remove the setup itself and the extracted sources now that everything is installed.
        // Developer checkouts (a .git entry next to setup.exe) and /KEEPSETUP are never touched.
        if (!developerCopy && !keepSetup) {
            SpawnSelfCleanup(Process.GetCurrentProcess().Id, setupExe, srcDir);
        }
    }

    private static void CompileApplication(List<string> sources, string outputExe) {
        using (CodeDomProvider provider = CodeDomProvider.CreateProvider("CSharp")) {
            CompilerParameters options = new CompilerParameters();
            options.GenerateExecutable = true;
            options.GenerateInMemory = false;
            options.OutputAssembly = outputExe;
            options.CompilerOptions = "/target:winexe /optimize+";
            options.ReferencedAssemblies.Add("System.dll");
            options.ReferencedAssemblies.Add("System.Core.dll");
            options.ReferencedAssemblies.Add("System.Drawing.dll");
            options.ReferencedAssemblies.Add("System.Windows.Forms.dll");

            CompilerResults results = provider.CompileAssemblyFromFile(options, sources.ToArray());
            if (results.Errors.HasErrors) {
                StringBuilder sb = new StringBuilder();
                int shown = 0;
                foreach (CompilerError e in results.Errors) {
                    if (!e.IsWarning) {
                        sb.AppendLine(e.ErrorText);
                        if (++shown >= 5) break;
                    }
                }
                throw new Exception("Compilation failed:\n" + sb.ToString());
            }
        }
    }

    private static void RegisterContextMenu(string exePath) {
        CreateVerb(@"SOFTWARE\Classes\*\shell\UnBlock", "UnBlock", exePath, "%1");
        CreateVerb(@"SOFTWARE\Classes\Directory\shell\UnBlock", "UnBlock", exePath, "%1");
        CreateVerb(@"SOFTWARE\Classes\Directory\Background\shell\UnBlock", "UnBlock This Folder", exePath, "%V");
        CreateVerb(@"SOFTWARE\Classes\Drive\shell\UnBlock", "UnBlock", exePath, "%1");
    }

    private static void CreateVerb(string path, string label, string exePath, string argPlaceholder) {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path)) {
            k.SetValue("", label);
            k.SetValue("Icon", "shell32.dll,239");
            using (Microsoft.Win32.RegistryKey cmd = k.CreateSubKey("command")) {
                cmd.SetValue("", "\"" + exePath + "\" \"" + argPlaceholder + "\"");
            }
        }
    }

    private static void RegisterArpEntry(string installDir, string exePath, string uninstallExePath) {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UnBlock")) {
            k.SetValue("DisplayName", "UnBlock File & Folder Unlocker");
            k.SetValue("DisplayVersion", "2.1.0");
            k.SetValue("Publisher", "UnBlock");
            k.SetValue("UninstallString", "\"" + uninstallExePath + "\"");
            k.SetValue("QuietUninstallString", "\"" + uninstallExePath + "\" /SILENT");
            k.SetValue("InstallLocation", "\"" + installDir + "\"");
            k.SetValue("DisplayIcon", "\"" + exePath + "\"");
            k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
    }

    private static void DeployWatcher(string exePath, string installDir) {
        string localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnBlock");
        if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);

        string watcherExe = Path.Combine(localDir, "UnBlockWatcher.exe");
        File.Copy(exePath, watcherExe, true);

        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                k.SetValue("UnBlockWatcher", "\"" + watcherExe + "\" [WATCHER] \"" + installDir + "\"");
            }
        } catch { }

        RunHiddenProcess("schtasks.exe", "/create /tn \"UnBlock-Cleanup\" /sc ONLOGON /ru SYSTEM /rl HIGHEST /tr \"\\\"" + watcherExe + "\\\" [WATCHER] \\\"" + installDir + "\\\"\" /f");
        
        try {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = watcherExe;
            psi.Arguments = "[WATCHER] \"" + installDir + "\"";
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        } catch { }
    }

    private static void RunWarmup(string exePath) {
        try {
            using (Process p = new Process()) {
                p.StartInfo.FileName = exePath;
                p.StartInfo.Arguments = "[WARMUP]";
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.Start();
                p.WaitForExit(30000);
            }
        } catch { }
    }

    private static void OfferWindows11Menu() {
        if (Environment.OSVersion.Version.Build < 22000) return;

        DialogResult choice = MessageBox.Show(
            "Put UnBlock directly on the Windows 11 right-click menu?\n\n" +
            "By default, Windows 11 hides classic entries behind 'Show more options'. " +
            "Choosing Yes restores the classic full right-click menu system-wide so UnBlock is always one click away.\n\n" +
            "(Note: Explorer will restart once to apply.)",
            "Windows 11 Right-Click Menu", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (choice != DialogResult.Yes) return;

        try {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32")) {
                k.SetValue("", "");
            }
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\UnBlock")) {
                k.SetValue("ClassicMenu", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            foreach (Process p in Process.GetProcessesByName("explorer")) {
                try { p.Kill(); } catch { }
            }
        } catch { }
    }

    private static bool IsElevated() {
        WindowsIdentity id = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(id);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
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

    private static void SpawnSelfCleanup(int pidToWait, params string[] paths) {
        try {
            string self = Application.ExecutablePath;
            string helperPath = Path.Combine(Path.GetTempPath(), "UnBlockSetup-" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".tmp");
            File.Copy(self, helperPath, true);

            StringBuilder argBuilder = new StringBuilder("[SETUPCLEANUP] " + pidToWait);
            foreach (string path in paths) {
                if (!string.IsNullOrEmpty(path)) argBuilder.Append(" \"" + path + "\"");
            }

            ProcessStartInfo psi = new ProcessStartInfo(helperPath, argBuilder.ToString());
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Process.Start(psi);
        } catch { }
    }

    private static void RunSetupCleanup(string[] args) {
        try {
            try { Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System); } catch { }

            int pidToWait = int.Parse(args[1]);
            try {
                using (Process p = Process.GetProcessById(pidToWait)) {
                    p.WaitForExit(20000);
                }
            } catch { }

            for (int i = 2; i < args.Length; i++) {
                string path = args[i];
                try {
                    if (Directory.Exists(path)) {
                        DeleteDirectoryWithRetry(path);
                    } else if (File.Exists(path)) {
                        DeleteFileWithRetry(path);
                    }
                } catch { }
            }

            SelfDestruct();
        } catch { }
        Environment.Exit(0);
    }

    private static void DeleteDirectoryWithRetry(string path) {
        for (int i = 0; i < 6; i++) {
            try {
                if (!Directory.Exists(path)) return;
                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
                if (!Directory.Exists(path)) return;
            } catch { }
            Thread.Sleep(400);
        }
    }

    private static void DeleteFileWithRetry(string path) {
        for (int i = 0; i < 6; i++) {
            try {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                if (!File.Exists(path)) return;
            } catch { }
            Thread.Sleep(400);
        }
    }

    private static void SelfDestruct() {
        try {
            string self = Application.ExecutablePath;
            string renamed = self + ".pending-delete";
            try { File.Move(self, renamed); } catch { renamed = self; }
            MoveFileEx(renamed, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        } catch { }
    }
}
