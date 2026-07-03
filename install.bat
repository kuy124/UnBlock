@echo off
:: Check for Admin Privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -STA -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo Loading Installer... Please wait.
powershell -STA -NoProfile -ExecutionPolicy Bypass -Command "$content = Get-Content -LiteralPath '%~f0'; $start = $false; $script = ($content | Where-Object { if ($_ -match '^##POWERSHELL_START##') { $start = $true; return $false }; if ($_ -match '^##POWERSHELL_END##') { $start = $false }; $start }) -join [char]10; Invoke-Expression $script"
exit /b

##POWERSHELL_START##
Add-Type -AssemblyName System.Windows.Forms
$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
$dialog.Description = "Select where you want to install UnBlock. (An 'UnBlock' folder will be created inside your selection)."
$dialog.ShowNewFolderButton = $true
$dialog.SelectedPath = [Environment]::GetFolderPath("ProgramFiles")

if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
    $InstallDir = Join-Path $dialog.SelectedPath "UnBlock"
} else {
    $InstallDir = Join-Path [Environment]::GetFolderPath("ProgramFiles") "UnBlock"
}

$CsPath = Join-Path $InstallDir "Unlocker.cs"
$ExePath = Join-Path $InstallDir "Unlocker.exe"
$UninstallerPath = Join-Path $InstallDir "uninstall.bat"

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Optimized C# 3.0 / 4.0 Compliant GUI Code (with silent JIT Warmup mechanism)
$CsCode = @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;
using System.Threading.Tasks;
using System.Text;

public class UnlockerForm : Form {
    private string targetPath;
    private ListView listView;
    private Button btnKill;
    private Button btnKillAll;
    private Button btnClose;
    private Label lblTarget;
    private Label lblTitle;
    private ProgressBar progressBar;

    // --- Win32 Native API Declarations ---
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_SHARE_NONE = 0;
    private const uint GENERIC_READ = 0x80000000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32 {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RM_UNIQUE_PROCESS {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_PROCESS_INFO {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, [In] RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Auto)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

    // --- Caching Mappings ---
    private static readonly Dictionary<int, string> ProcessPathMap = new Dictionary<int, string>();
    private static readonly Dictionary<int, string> ProcessNameMap = new Dictionary<int, string>();
    private static DateTime lastSnapshotTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(8);
    private static readonly object CacheLock = new object();

    public UnlockerForm(string path) {
        this.targetPath = path.TrimEnd('"');
        InitializeComponent();
        StartAsyncScan();
    }

    private void InitializeComponent() {
        this.Text = "UnBlock";
        this.Size = new Size(550, 420);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.White;

        lblTarget = new Label() {
            Text = "Target: " + targetPath,
            Location = new Point(20, 15),
            Size = new Size(500, 35),
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            ForeColor = Color.DimGray
        };

        lblTitle = new Label() {
            Text = "Scanning Resource Locks...",
            Location = Point.Empty,
            Size = new Size(500, 20),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(51, 51, 51)
        };
        lblTitle.Location = new Point(20, 50);

        progressBar = new ProgressBar() {
            Location = new Point(20, 75),
            Size = new Size(495, 15),
            Style = ProgressBarStyle.Continuous,
            Visible = true
        };

        listView = new ListView() {
            Location = new Point(20, 100),
            Size = new Size(495, 210),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Segoe UI", 9, FontStyle.Regular)
        };
        listView.Columns.Add("Process Name", 140);
        listView.Columns.Add("PID", 60);
        listView.Columns.Add("Path", 290);
        listView.SelectedIndexChanged += delegate {
            btnKill.Enabled = listView.SelectedItems.Count > 0 && listView.SelectedItems[0].Tag is ProcessItem;
        };

        btnKill = new Button() {
            Text = "Terminate Process",
            Location = new Point(140, 330),
            Size = new Size(140, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Enabled = false
        };
        btnKill.FlatAppearance.BorderSize = 0;
        btnKill.Click += BtnKill_Click;

        btnKillAll = new Button() {
            Text = "Terminate All",
            Location = new Point(290, 330),
            Size = new Size(130, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(209, 52, 56),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Enabled = false
        };
        btnKillAll.FlatAppearance.BorderSize = 0;
        btnKillAll.Click += BtnKillAll_Click;

        btnClose = new Button() {
            Text = "Close",
            Location = new Point(430, 330),
            Size = new Size(85, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(243, 243, 243),
            ForeColor = Color.Black,
            Font = new Font("Segoe UI", 9, FontStyle.Regular)
        };
        btnClose.FlatAppearance.BorderColor = Color.LightGray;
        btnClose.Click += delegate { this.Close(); };

        this.Controls.Add(lblTarget);
        this.Controls.Add(lblTitle);
        this.Controls.Add(progressBar);
        this.Controls.Add(listView);
        this.Controls.Add(btnKill);
        this.Controls.Add(btnKillAll);
        this.Controls.Add(btnClose);
    }

    private class ProcessItem {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
    }

    private void StartAsyncScan() {
        Task.Factory.StartNew(delegate {
            try {
                List<ProcessItem> results = Run3TierScan(targetPath, delegate(int val) {
                    this.BeginInvoke(new MethodInvoker(delegate {
                        progressBar.Value = val;
                    }));
                });

                this.BeginInvoke(new MethodInvoker(delegate {
                    progressBar.Visible = false;
                    lblTitle.Text = "Locking Processes";
                    PopulateListView(results);
                }));
            } catch (Exception ex) {
                this.BeginInvoke(new MethodInvoker(delegate {
                    progressBar.Visible = false;
                    MessageBox.Show("Scan error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
            }
        });
    }

    private void PopulateListView(List<ProcessItem> items) {
        listView.Items.Clear();
        foreach (var item in items) {
            ListViewItem lvi = new ListViewItem(new string[] { item.Name, item.Pid.ToString(), item.Path });
            lvi.Tag = item;
            listView.Items.Add(lvi);
        }

        if (listView.Items.Count == 0) {
            ListViewItem emptyItem = new ListViewItem(new string[] { "N/A", "N/A", "No locking processes found." });
            listView.Items.Add(emptyItem);
            btnKill.Enabled = false;
            btnKillAll.Enabled = false;
        } else {
            btnKillAll.Enabled = true;
        }
    }

    // --- Core 3-Tier Scan Logic ---
    private List<ProcessItem> Run3TierScan(string path, Action<int> progressCallback) {
        var finalLockingProcesses = new List<ProcessItem>();
        var addedPids = new HashSet<int>();

        // Proactively snapshots PIDs and resolves Executable Paths in memory
        RefreshProcessSnapshot();

        if (Directory.Exists(path)) {
            // 1. FAST PROCESS MATCH PATH: Instantly identify any process executing inside this directory tree
            lock (CacheLock) {
                foreach (KeyValuePair<int, string> kvp in ProcessPathMap) {
                    int pid = kvp.Key;
                    string procPath = kvp.Value;
                    if (procPath != null && procPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)) {
                        if (addedPids.Add(pid)) {
                            finalLockingProcesses.Add(new ProcessItem {
                                Pid = pid,
                                Name = GetProcessName(pid),
                                Path = procPath
                            });
                        }
                    }
                }
            }

            // 2. PARALLEL BFS SCAN PATH: Gathers sub-files shallowest-first to prevent disk head thrashing
            var files = new List<string>();
            var dirQueue = new Queue<string>();
            dirQueue.Enqueue(path);

            try {
                while (dirQueue.Count > 0 && files.Count < 10000) {
                    string currentDir = dirQueue.Dequeue();
                    try {
                        string[] dirFiles = Directory.GetFiles(currentDir, "*.*");
                        files.AddRange(dirFiles);
                    } catch {}

                    if (files.Count >= 10000) break;

                    try {
                        string[] subDirs = Directory.GetDirectories(currentDir);
                        foreach (string sub in subDirs) {
                            dirQueue.Enqueue(sub);
                        }
                    } catch {}
                }
            } catch {}

            if (files.Count == 0) {
                progressCallback(100);
                return finalLockingProcesses;
            }

            // Tier 1 Parallel Heuristic Filter
            var lockedFiles = new List<string>();
            object lockObj = new object();
            int processedCount = 0;
            int totalFiles = files.Count;

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, (int)(Environment.ProcessorCount * 0.75)) }, delegate(string file) {
                if (IsFileLockedHeuristic(file)) {
                    lock (lockObj) {
                        lockedFiles.Add(file);
                    }
                }
                int current = System.Threading.Interlocked.Increment(ref processedCount);
                if (current % 100 == 0 || current == totalFiles) {
                    int percent = (int)(((double)current / totalFiles) * 50); // First 50% for fast filtering
                    progressCallback(percent);
                }
            });

            // Tier 2 Batching for Restart Manager
            if (lockedFiles.Count > 0) {
                int batchSize = 200;
                int totalBatches = (int)Math.Ceiling((double)lockedFiles.Count / batchSize);

                for (int i = 0; i < lockedFiles.Count; i += batchSize) {
                    int size = Math.Min(batchSize, lockedFiles.Count - i);
                    string[] chunk = new string[size];
                    lockedFiles.CopyTo(i, chunk, 0, size);

                    var pids = GetProcessesLockingFiles(chunk);
                    foreach (int pid in pids) {
                        if (addedPids.Add(pid)) {
                            finalLockingProcesses.Add(new ProcessItem {
                                Pid = pid,
                                Name = GetProcessName(pid),
                                Path = GetProcessPath(pid) ?? "Unknown (Protected/Elevated)"
                            });
                        }
                    }

                    int currentBatch = (i / batchSize) + 1;
                    int percent = 50 + (int)(((double)currentBatch / totalBatches) * 50);
                    progressCallback(percent);
                }
            } else {
                progressCallback(100);
            }
        } else if (File.Exists(path)) {
            progressCallback(25);
            if (!IsFileLockedHeuristic(path)) {
                progressCallback(100);
                return finalLockingProcesses;
            }
            progressCallback(50);
            var pids = GetProcessesLockingFiles(new string[] { path });
            foreach (int pid in pids) {
                if (addedPids.Add(pid)) {
                    finalLockingProcesses.Add(new ProcessItem {
                        Pid = pid,
                        Name = GetProcessName(pid),
                        Path = GetProcessPath(pid) ?? "Unknown (Protected/Elevated)"
                    });
                }
            }
            progressCallback(100);
        }

        return finalLockingProcesses;
    }

    public static bool IsFileLockedHeuristic(string filePath) {
        try {
            // First attempt: Open for reading with zero sharing.
            // If the file is unlocked, this succeeds instantly even on read-only/permission-restricted files.
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None)) {
                return false;
            }
        } catch (IOException ex) {
            int errorCode = Marshal.GetHRForException(ex) & 0xFFFF;
            // 32 = ERROR_SHARING_VIOLATION, 33 = ERROR_LOCK_VIOLATION
            return (errorCode == 32 || errorCode == 33);
        } catch (UnauthorizedAccessException) {
            // Under access-restricted directories (e.g. Program Files), some unlocked files throw Access Denied.
            // We verify by attempting to open with full ReadWrite sharing.
            try {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    // Succeeded with full sharing - meaning the file is completely UNLOCKED (just write-protected)
                    return false;
                }
            } catch (IOException ex) {
                int errorCode = Marshal.GetHRForException(ex) & 0xFFFF;
                return (errorCode == 32 || errorCode == 33);
            } catch {
                // Any other exception indicates strict ACL permissions, not an active process handle lock.
                return false;
            }
        } catch {
            return false;
        }
    }

    public static List<int> GetProcessesLockingFiles(string[] paths) {
        var pids = new List<int>();
        if (paths == null || paths.Length == 0) return pids;

        uint handle;
        string key = Guid.NewGuid().ToString();
        int res = RmStartSession(out handle, 0, key);
        if (res != 0) return pids;

        try {
            res = RmRegisterResources(handle, (uint)paths.Length, paths, 0, null, 0, null);
            if (res != 0) return pids;

            uint pnProcInfoNeeded = 0;
            uint pnProcInfo = 0;
            uint lpdwRebootReasons = 0;

            res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);
            if (res == 234) { // ERROR_MORE_DATA
                RM_PROCESS_INFO[] processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;
                res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);
                if (res == 0) {
                    for (int i = 0; i < pnProcInfo; i++) {
                        pids.Add(processInfo[i].Process.dwProcessId);
                    }
                }
            }
        } finally {
            RmEndSession(handle);
        }
        return pids;
    }

    public static void RefreshProcessSnapshot() {
        lock (CacheLock) {
            if (DateTime.UtcNow - lastSnapshotTime < CacheTtl) return;

            IntPtr hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (hSnapshot == INVALID_HANDLE_VALUE) return;

            try {
                PROCESSENTRY32 pe32 = new PROCESSENTRY32();
                pe32.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

                if (Process32First(hSnapshot, ref pe32)) {
                    var activePids = new HashSet<int>();
                    do {
                        int pid = (int)pe32.th32ProcessID;
                        activePids.Add(pid);
                        ProcessNameMap[pid] = pe32.szExeFile;

                        // Proactively resolves executable location for zero-latency comparisons
                        string fullPath = QueryProcessPathDirect(pid);
                        if (fullPath != null) {
                            ProcessPathMap[pid] = fullPath;
                        }
                    } while (Process32Next(hSnapshot, ref pe32));

                    // Cache cleanup for terminated processes
                    var stalePids = new List<int>();
                    foreach (var key in ProcessPathMap.Keys) {
                        if (!activePids.Contains(key)) stalePids.Add(key);
                    }
                    foreach (var pid in stalePids) {
                        ProcessPathMap.Remove(pid);
                        ProcessNameMap.Remove(pid);
                    }
                }
                lastSnapshotTime = DateTime.UtcNow;
            } finally {
                CloseHandle(hSnapshot);
            }
        }
    }

    private static string QueryProcessPathDirect(int pid) {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess != IntPtr.Zero) {
            try {
                int size = 1024;
                StringBuilder sb = new StringBuilder(size);
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size)) {
                    return sb.ToString();
                }
            } finally {
                CloseHandle(hProcess);
            }
        }
        return null;
    }

    private static string GetProcessPath(int pid) {
        lock (CacheLock) {
            string cachedPath;
            if (ProcessPathMap.TryGetValue(pid, out cachedPath)) return cachedPath;
        }
        return QueryProcessPathDirect(pid);
    }

    private static string GetProcessName(int pid) {
        lock (CacheLock) {
            string name;
            if (ProcessNameMap.TryGetValue(pid, out name)) {
                return name;
            }
            return "Unknown";
        }
    }

    private void BtnKill_Click(object sender, EventArgs e) {
        if (listView.SelectedItems.Count == 0) return;
        var item = listView.SelectedItems[0].Tag as ProcessItem;
        if (item == null) return;

        var confirm = MessageBox.Show("Are you sure you want to terminate '" + item.Name + "'?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm == DialogResult.Yes) {
            try {
                using (var p = Process.GetProcessById(item.Pid)) {
                    p.Kill();
                }
                MessageBox.Show("Process successfully terminated.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                StartAsyncScan();
            } catch {
                PromptForElevation();
            }
        }
    }

    private void BtnKillAll_Click(object sender, EventArgs e) {
        var targets = new List<ProcessItem>();
        foreach (ListViewItem lvi in listView.Items) {
            ProcessItem pi = lvi.Tag as ProcessItem;
            if (pi != null) {
                targets.Add(pi);
            }
        }

        if (targets.Count == 0) return;

        var confirm = MessageBox.Show("Are you sure you want to terminate all " + targets.Count + " locking processes?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm == DialogResult.Yes) {
            bool failedAny = false;
            foreach (var pi in targets) {
                try {
                    using (var p = Process.GetProcessById(pi.Pid)) {
                        p.Kill();
                    }
                } catch {
                    failedAny = true;
                }
            }

            if (!failedAny) {
                MessageBox.Show("All processes successfully terminated.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                StartAsyncScan();
            } else {
                PromptForElevation();
            }
        }
    }

    private void PromptForElevation() {
        var elevate = MessageBox.Show("Administrative privileges are required to close this process.\n\nWould you like to restart UnBlock as Administrator?", "Elevation Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (elevate == DialogResult.Yes) {
            try {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = Application.ExecutablePath;
                startInfo.Arguments = "\"" + targetPath + "\"";
                startInfo.Verb = "runas";
                Process.Start(startInfo);
                this.Close();
            } catch (Exception ex) {
                MessageBox.Show("Failed to elevate: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    [STAThread]
    public static void Main(string[] args) {
        if (args.Length == 1 && args[0] == "[WARMUP]") {
            try {
                string tempFile = Path.GetTempFileName();
                RefreshProcessSnapshot();
                IsFileLockedHeuristic(tempFile);
                List<int> pids = GetProcessesLockingFiles(new string[] { tempFile });
                if (File.Exists(tempFile)) {
                    File.Delete(tempFile);
                }
            } catch {}
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 0) {
            MessageBox.Show("Please select a file or folder by right-clicking it.", "UnBlock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        
        string fullPath = string.Join(" ", args);
        Application.Run(new UnlockerForm(fullPath));
    }
}
'@

Set-Content -Path $CsPath -Value $CsCode -Encoding UTF8 -Force

# Locate compiler
$csc = $null
if (Test-Path "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe") {
    $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
} elseif (Test-Path "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe") {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if ($null -eq $csc) {
    [System.Windows.Forms.MessageBox]::Show("Error: Built-in compiler not found. Compilation aborted.", "Error", "OK", "Error")
    Write-Host "Error: Built-in C# compiler (csc.exe) was not found."
    Exit
}

# Compile and actively check stdout/stderr for compilation troubleshooting
$argString = "/nologo /target:winexe /out:`"$ExePath`" /reference:System.Windows.Forms.dll,System.Drawing.dll,System.dll,System.Core.dll `"$CsPath`""
$pInfo = New-Object System.Diagnostics.ProcessStartInfo
$pInfo.FileName = $csc
$pInfo.Arguments = $argString
$pInfo.RedirectStandardOutput = $true
$pInfo.RedirectStandardError = $true
$pInfo.UseShellExecute = $false
$pInfo.CreateNoWindow = $true

$proc = [System.Diagnostics.Process]::Start($pInfo)
$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()

if ($proc.ExitCode -eq 0) {
    Remove-Item -Path $CsPath -Force -ErrorAction SilentlyContinue
} else {
    [System.Windows.Forms.MessageBox]::Show("Compilation failed!`n`nErrors:`n$stdout`n$stderr", "Error", "OK", "Error")
    Exit
}

# Silent post-install headless warmup (JIT-compiles logic, pre-loads DLLs, primes filesystem cache)
Start-Process -FilePath $ExePath -ArgumentList "[WARMUP]" -WindowStyle Hidden -Wait

# Write Uninstaller script locally
$UninstallerCode = @'
@echo off
net session >nul 2>&1
if %errorLevel% neq 0 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
echo Removing Registry Keys...
reg delete "HKLM\SOFTWARE\Classes\*\shell\UnBlock" /f >nul 2>&1
reg delete "HKLM\SOFTWARE\Classes\Directory\shell\UnBlock" /f >nul 2>&1
reg delete "HKLM\SOFTWARE\Classes\Directory\Background\shell\UnBlock" /f >nul 2>&1

echo Registry keys removed successfully.
powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show('UnBlock has been successfully uninstalled.', 'Uninstalled', 'OK', 'Information')"

start /b cmd /c "timeout /t 2 >nul & rmdir /s /q "%~dp0""
exit /b
'@
Set-Content -Path $UninstallerPath -Value $UninstallerCode -Encoding UTF8 -Force

# =========================================================================
# HARDENED REGISTRY SETUP (Idempotent natively via .NET)
# =========================================================================
$baseKey = [Microsoft.Win32.Registry]::LocalMachine

# 1. Right Click -> Files
$keyFile = $baseKey.CreateSubKey("SOFTWARE\Classes\*\shell\UnBlock")
$keyFile.SetValue("", "UnBlock File")
$keyFile.SetValue("Icon", "shell32.dll,240")
$keyFileCmd = $baseKey.CreateSubKey("SOFTWARE\Classes\*\shell\UnBlock\command")
$keyFileCmd.SetValue("", "`"$ExePath`" `"%1`"")

# 2. Right Click -> Folders
$keyDir = $baseKey.CreateSubKey("SOFTWARE\Classes\Directory\shell\UnBlock")
$keyDir.SetValue("", "UnBlock Folder")
$keyDir.SetValue("Icon", "shell32.dll,240")
$keyDirCmd = $baseKey.CreateSubKey("SOFTWARE\Classes\Directory\shell\UnBlock\command")
$keyDirCmd.SetValue("", "`"$ExePath`" `"%1`"")

# 3. Right Click -> Empty Space Inside a Folder
$keyBg = $baseKey.CreateSubKey("SOFTWARE\Classes\Directory\Background\shell\UnBlock")
$keyBg.SetValue("", "UnBlock This Folder")
$keyBg.SetValue("Icon", "shell32.dll,240")
$keyBgCmd = $baseKey.CreateSubKey("SOFTWARE\Classes\Directory\Background\shell\UnBlock\command")
$keyBgCmd.SetValue("", "`"$ExePath`" `"%V`"")

[System.Windows.Forms.MessageBox]::Show("Installation completed successfully!`n`nUnBlock has been installed to:`n$InstallDir", "Setup Complete", "OK", "Information")
##POWERSHELL_END##