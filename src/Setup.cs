using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Modern graphical installer: compiles application sources locally,
// registers context verbs and scheduled tasks, then cleans up temporary setup files.
internal static class Setup {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    private static DialogResult ShowTopMostMessage(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) {
        using (Form topForm = new Form()) {
            topForm.Size = new Size(1, 1);
            topForm.StartPosition = FormStartPosition.Manual;
            topForm.Location = new Point(-2000, -2000);
            topForm.ShowInTaskbar = false;
            topForm.TopMost = true;
            topForm.FormBorderStyle = FormBorderStyle.None;
            topForm.Show();
            topForm.BringToFront();
            topForm.Activate();
            SetForegroundWindow(topForm.Handle);
            return MessageBox.Show(topForm, text, caption, buttons, icon);
        }
    }

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
            ShowTopMostMessage("Could not find the 'src' folder next to setup.exe.\n\nPlease extract the entire archive before running setup.", "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string existingDir = GetExistingInstallLocation();

        if (silent) {
            string targetDir = !string.IsNullOrEmpty(customDir) ? customDir :
                               (!string.IsNullOrEmpty(existingDir) ? existingDir :
                               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UnBlock"));
            ExecuteInstall(sources, targetDir, true, false);
            if (!developerCopy && !keepSetup) SpawnSelfCleanup(Process.GetCurrentProcess().Id, setupExe, srcDir);
            return;
        }

        using (SetupWizardForm wizard = new SetupWizardForm(sources, existingDir, customDir, developerCopy || keepSetup)) {
            Application.Run(wizard);
            if (wizard.InstallSucceeded && !developerCopy && !keepSetup) {
                SpawnSelfCleanup(Process.GetCurrentProcess().Id, setupExe, srcDir);
            }
        }
    }

    internal static string GetExistingInstallLocation() {
        try {
            foreach (string procName in new string[] { "Unlocker", "UnBlockWatcher" }) {
                Process[] procs = Process.GetProcessesByName(procName);
                foreach (Process p in procs) {
                    try {
                        string exePath = p.MainModule != null ? p.MainModule.FileName : null;
                        if (!string.IsNullOrEmpty(exePath)) {
                            string dir = Path.GetDirectoryName(exePath);
                            if (IsValidInstallDir(dir)) return CleanDir(dir);
                        }
                    } catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
        } catch { }

        var registryTargets = new[] {
            Tuple.Create(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
            Tuple.Create(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
            Tuple.Create(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default)
        };

        foreach (var target in registryTargets) {
            try {
                using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(target.Item1, target.Item2)) {
                    using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UnBlock")) {
                        if (key != null) {
                            string loc = key.GetValue("InstallLocation") as string;
                            if (IsValidInstallDir(loc)) return CleanDir(loc);

                            string icon = key.GetValue("DisplayIcon") as string;
                            string dirFromIcon = ExtractDirFromPathString(icon);
                            if (IsValidInstallDir(dirFromIcon)) return CleanDir(dirFromIcon);
                        }
                    }
                }
            } catch { }
        }

        string[] defaultDirs = new string[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UnBlock"),
            Environment.GetEnvironmentVariable("ProgramW6432") != null ? Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432"), "UnBlock") : null,
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") != null ? Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), "UnBlock") : null
        };

        foreach (string d in defaultDirs) {
            if (!string.IsNullOrEmpty(d) && IsValidInstallDir(d)) return CleanDir(d);
        }

        return null;
    }

    private static bool IsValidInstallDir(string dir) {
        if (string.IsNullOrEmpty(dir)) return false;
        try {
            return Directory.Exists(dir) && (File.Exists(Path.Combine(dir, "Unlocker.exe")) || File.Exists(Path.Combine(dir, "uninstall.exe")));
        } catch { return false; }
    }

    private static string CleanDir(string dir) {
        return string.IsNullOrEmpty(dir) ? dir : dir.Trim('"', ' ', '\t').TrimEnd('\\', '/');
    }

    private static string ExtractDirFromPathString(string raw) {
        if (string.IsNullOrEmpty(raw)) return null;
        try {
            string s = raw.Trim();
            if (s.StartsWith("\"")) {
                int nextQuote = s.IndexOf('"', 1);
                if (nextQuote > 1) return Path.GetDirectoryName(s.Substring(1, nextQuote - 1));
            }
            int spaceIdx = s.IndexOf(' ');
            return Path.GetDirectoryName(spaceIdx > 0 ? s.Substring(0, spaceIdx) : s);
        } catch { return null; }
    }

    internal static void ExecuteInstall(List<string> sources, string installDir, bool applyWin11Classic, bool enableWatcher, Action<string, int> progressCallback = null) {
        if (progressCallback != null) progressCallback("Closing running instances...", 15);
        KillRunningInstances();

        string exePath = Path.Combine(installDir, "Unlocker.exe");
        Directory.CreateDirectory(installDir);

        if (progressCallback != null) progressCallback("Compiling application sources...", 40);
        CompileApplication(sources, exePath);

        string uninstallExePath = Path.Combine(installDir, "uninstall.exe");
        File.Copy(exePath, uninstallExePath, true);

        if (progressCallback != null) progressCallback("Registering Explorer context menus...", 65);
        RegisterContextMenu(exePath);
        RegisterArpEntry(installDir, exePath, uninstallExePath);

        if (enableWatcher) {
            if (progressCallback != null) progressCallback("Setting up background maintenance...", 80);
            DeployWatcher(exePath, installDir);
        }

        if (progressCallback != null) progressCallback("Warming up scan engine...", 90);
        RunWarmup(exePath);

        if (applyWin11Classic && Environment.OSVersion.Version.Build >= 22000) {
            ApplyWin11ClassicMenu();
        }

        if (progressCallback != null) progressCallback("Ready!", 100);
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
            ProcessStartInfo psi = new ProcessStartInfo(watcherExe, "[WATCHER] \"" + installDir + "\"");
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

    private static void ApplyWin11ClassicMenu() {
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
                finally { try { p.Dispose(); } catch { } }
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
                using (Process p = Process.GetProcessById(pidToWait)) { p.WaitForExit(20000); }
            } catch { }

            for (int i = 2; i < args.Length; i++) {
                string path = args[i];
                try {
                    if (Directory.Exists(path)) DeleteDirectoryWithRetry(path);
                    else if (File.Exists(path)) DeleteFileWithRetry(path);
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

// Modern Fluent Setup Wizard Form
internal class SetupWizardForm : Form {
    public bool InstallSucceeded { get; private set; }

    private readonly List<string> sources;
    private readonly string existingDir;
    private readonly bool isDevMode;

    private TextBox txtPath;
    private Button btnBrowse;
    private Button btnInstall;
    private Button btnFinish;
    private ProgressBar progressBar;
    private Label lblStatus;
    private CheckBox chkClassicMenu;
    private CheckBox chkWatcher;
    private Panel contentPanel;
    private Panel headerPanel;
    private Panel bannerBadge;

    public SetupWizardForm(List<string> sources, string existingDir, string customDir, bool isDevMode) {
        this.sources = sources;
        this.existingDir = existingDir;
        this.isDevMode = isDevMode;
        this.InstallSucceeded = false;

        this.Text = "UnBlock Setup";
        this.Size = new Size(540, 430);
        this.MinimumSize = new Size(540, 430);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = true;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(248, 249, 250);
        this.Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        BuildUI(customDir);
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        this.Activate();
        this.BringToFront();
        Setup.SetForegroundWindow(this.Handle);
    }

    private void BuildUI(string customDir) {
        // --- Header Banner ---
        headerPanel = new Panel() {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Color.FromArgb(27, 36, 44)
        };

        Label lblTitle = new Label() {
            Text = "UnBlock Setup",
            Location = new Point(22, 14),
            Size = new Size(300, 24),
            Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
            ForeColor = Color.White
        };

        Label lblSubtitle = new Label() {
            Text = "Resolves 'File in Use' and 'Folder Access Denied' errors on Windows.",
            Location = new Point(23, 40),
            Size = new Size(450, 18),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(170, 182, 192)
        };

        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblSubtitle);

        // --- Bottom Action Bar ---
        Panel bottomBar = new Panel() {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Color.FromArgb(241, 243, 245)
        };
        Panel topBorder = new Panel() { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(222, 226, 230) };
        bottomBar.Controls.Add(topBorder);

        btnInstall = new Button() {
            Text = !string.IsNullOrEmpty(existingDir) ? "Update / Overwrite" : "Install Now",
            Size = new Size(135, 32),
            Location = new Point(270, 12),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(39, 174, 96),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnInstall.FlatAppearance.BorderSize = 0;
        btnInstall.Click += BtnInstall_Click;

        btnFinish = new Button() {
            Text = "Cancel",
            Size = new Size(95, 32),
            Location = new Point(415, 12),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(225, 229, 233),
            ForeColor = Color.FromArgb(40, 40, 40),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnFinish.FlatAppearance.BorderSize = 0;
        btnFinish.Click += (s, e) => this.Close();

        bottomBar.Controls.Add(btnInstall);
        bottomBar.Controls.Add(btnFinish);

        // --- Main Content Area ---
        contentPanel = new Panel() {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 16, 24, 10),
            BackColor = Color.FromArgb(248, 249, 250)
        };

        int curY = 12;

        if (!string.IsNullOrEmpty(existingDir)) {
            bannerBadge = new Panel() {
                Location = new Point(24, curY),
                Size = new Size(474, 38),
                BackColor = Color.FromArgb(234, 244, 254)
            };
            bannerBadge.Paint += (s, pe) => {
                using (Pen p = new Pen(Color.FromArgb(170, 205, 245), 1)) {
                    pe.Graphics.DrawRectangle(p, 0, 0, bannerBadge.Width - 1, bannerBadge.Height - 1);
                }
            };
            Label lblBadge = new Label() {
                Text = "Existing installation detected. Setup will overwrite & upgrade it in place.",
                Location = new Point(10, 10),
                Size = new Size(454, 18),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(20, 90, 160)
            };
            bannerBadge.Controls.Add(lblBadge);
            contentPanel.Controls.Add(bannerBadge);
            curY += 46;
        }

        Label lblDest = new Label() {
            Text = "Installation Folder:",
            Location = new Point(24, curY),
            Size = new Size(200, 18),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 55, 60)
        };
        contentPanel.Controls.Add(lblDest);
        curY += 22;

        string defaultPath = !string.IsNullOrEmpty(customDir) ? customDir :
                            (!string.IsNullOrEmpty(existingDir) ? existingDir :
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UnBlock"));

        txtPath = new TextBox() {
            Text = defaultPath,
            Location = new Point(24, curY),
            Size = new Size(385, 26),
            Font = new Font("Segoe UI", 9F)
        };

        btnBrowse = new Button() {
            Text = "Browse...",
            Location = new Point(416, curY - 1),
            Size = new Size(82, 27),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(230, 235, 240),
            ForeColor = Color.FromArgb(40, 40, 40),
            Font = new Font("Segoe UI", 8.5F)
        };
        btnBrowse.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 210);
        btnBrowse.Click += BtnBrowse_Click;

        contentPanel.Controls.Add(txtPath);
        contentPanel.Controls.Add(btnBrowse);
        curY += 38;

        Label lblOpts = new Label() {
            Text = "Preferences & Integration:",
            Location = new Point(24, curY),
            Size = new Size(200, 18),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 55, 60)
        };
        contentPanel.Controls.Add(lblOpts);
        curY += 24;

        chkWatcher = new CheckBox() {
            Text = "Enable instant Explorer dialog replacement & background maintenance",
            Checked = true,
            Location = new Point(26, curY),
            Size = new Size(470, 20),
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(40, 40, 40)
        };
        contentPanel.Controls.Add(chkWatcher);
        curY += 24;

        if (Environment.OSVersion.Version.Build >= 22000) {
            chkClassicMenu = new CheckBox() {
                Text = "Windows 11: Show UnBlock directly on primary right-click menu",
                Checked = true,
                Location = new Point(26, curY),
                Size = new Size(470, 20),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(40, 40, 40)
            };
            contentPanel.Controls.Add(chkClassicMenu);
            curY += 28;
        }

        progressBar = new ProgressBar() {
            Location = new Point(24, curY + 6),
            Size = new Size(474, 8),
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };
        contentPanel.Controls.Add(progressBar);

        lblStatus = new Label() {
            Text = "",
            Location = new Point(24, curY + 18),
            Size = new Size(474, 20),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = Color.FromArgb(90, 95, 100),
            Visible = false
        };
        contentPanel.Controls.Add(lblStatus);

        this.Controls.Add(contentPanel);
        this.Controls.Add(bottomBar);
        this.Controls.Add(headerPanel);
        this.AcceptButton = btnInstall;
        this.CancelButton = btnFinish;
    }

    private void BtnBrowse_Click(object sender, EventArgs e) {
        using (FolderBrowserDialog dlg = new FolderBrowserDialog()) {
            dlg.Description = "Select the folder where you want to install UnBlock:";
            dlg.ShowNewFolderButton = true;
            if (Directory.Exists(txtPath.Text)) dlg.SelectedPath = txtPath.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) {
                txtPath.Text = dlg.SelectedPath.EndsWith("UnBlock", StringComparison.OrdinalIgnoreCase) ?
                               dlg.SelectedPath : Path.Combine(dlg.SelectedPath, "UnBlock");
            }
        }
    }

    private void BtnInstall_Click(object sender, EventArgs e) {
        string targetDir = txtPath.Text.Trim();
        if (string.IsNullOrEmpty(targetDir)) {
            MessageBox.Show(this, "Please enter a valid installation directory.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        btnInstall.Enabled = false;
        btnBrowse.Enabled = false;
        txtPath.Enabled = false;
        chkWatcher.Enabled = false;
        if (chkClassicMenu != null) chkClassicMenu.Enabled = false;

        progressBar.Visible = true;
        progressBar.Value = 10;
        lblStatus.Visible = true;
        lblStatus.Text = "Preparing installation...";

        bool win11Classic = chkClassicMenu != null && chkClassicMenu.Checked;
        bool watcher = chkWatcher.Checked;

        ThreadPool.QueueUserWorkItem(s => {
            try {
                Setup.ExecuteInstall(sources, targetDir, win11Classic, watcher, (msg, pct) => {
                    if (this.IsHandleCreated && !this.IsDisposed) {
                        this.BeginInvoke(new MethodInvoker(() => {
                            lblStatus.Text = msg;
                            progressBar.Value = Math.Min(100, Math.Max(0, pct));
                        }));
                    }
                });

                InstallSucceeded = true;

                this.BeginInvoke(new MethodInvoker(() => {
                    ShowCompletionView(targetDir);
                }));
            } catch (Exception ex) {
                this.BeginInvoke(new MethodInvoker(() => {
                    progressBar.Visible = false;
                    lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                    lblStatus.Text = "Installation failed!";
                    btnInstall.Enabled = true;
                    btnBrowse.Enabled = true;
                    txtPath.Enabled = true;
                    MessageBox.Show(this, "Setup encountered an error:\n\n" + ex.Message, "Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
            }
        });
    }

    private void ShowCompletionView(string targetDir) {
        contentPanel.Controls.Clear();

        Panel successCard = new Panel() {
            Location = new Point(24, 16),
            Size = new Size(474, 210),
            BackColor = Color.White
        };
        successCard.Paint += (s, pe) => {
            using (Pen p = new Pen(Color.FromArgb(220, 226, 230), 1)) {
                pe.Graphics.DrawRectangle(p, 0, 0, successCard.Width - 1, successCard.Height - 1);
            }
        };

        Label lblSuccessTitle = new Label() {
            Text = "Installation Complete!",
            Location = new Point(20, 16),
            Size = new Size(434, 26),
            Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(39, 174, 96)
        };

        string devNote = isDevMode ? "\n\n(Developer mode: setup files and sources were preserved.)" : "";
        Label lblSuccessDetails = new Label() {
            Text = "UnBlock is now installed and ready to use.\n\n" +
                   "• Right-click any locked file or folder in Windows Explorer and choose 'UnBlock'.\n" +
                   "• Right-click empty folder space and choose 'UnBlock This Folder'.\n\n" +
                   "Installed to:\n" + targetDir + devNote,
            Location = new Point(22, 48),
            Size = new Size(432, 145),
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(50, 55, 60)
        };

        successCard.Controls.Add(lblSuccessTitle);
        successCard.Controls.Add(lblSuccessDetails);
        contentPanel.Controls.Add(successCard);

        btnInstall.Visible = false;
        btnFinish.Text = "Finish";
        btnFinish.BackColor = Color.FromArgb(39, 174, 96);
        btnFinish.ForeColor = Color.White;
        btnFinish.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        btnFinish.Size = new Size(110, 32);
        btnFinish.Location = new Point(400, 12);
        btnFinish.Focus();
        this.AcceptButton = btnFinish;
    }
}