using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using System.Drawing;
using System.Threading;
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
    private Label lblAdminState;
    private ProgressBar progressBar;
    
    // State tracking
    private bool isAdmin;
    private string logFile;

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

    private static readonly Dictionary<int, string> ProcessPathMap = new Dictionary<int, string>();
    private static readonly Dictionary<int, string> ProcessNameMap = new Dictionary<int, string>();
    private static DateTime lastSnapshotTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(8);
    private static readonly object CacheLock = new object();

    public UnlockerForm(string path) {
        this.targetPath = path.TrimEnd('"');
        
        WindowsIdentity id = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(id);
        isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logDir = Path.Combine(appData, "UnBlock");
        Directory.CreateDirectory(logDir);
        logFile = Path.Combine(logDir, "UnBlock.log");

        Log("======================================");
        Log("UnBlock Started");
        Log("Target: " + targetPath);
        Log("Running as Administrator: " + isAdmin);

        InitializeComponent();
        StartAsyncScan(false);
    }

    private void Log(string message) {
        try {
            File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + message + Environment.NewLine);
        } catch { } 
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
            Size = new Size(300, 35),
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            ForeColor = Color.DimGray
        };

        lblAdminState = new Label() {
            Text = isAdmin ? "Administrator" : "Standard User",
            Location = new Point(320, 15),
            Size = new Size(195, 20),
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            ForeColor = isAdmin ? Color.Green : Color.DarkOrange,
            TextAlign = ContentAlignment.TopRight
        };

        lblTitle = new Label() {
            Text = "Scanning Resource Locks...",
            Location = new Point(20, 50),
            Size = new Size(500, 20),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(51, 51, 51)
        };

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
        this.Controls.Add(lblAdminState);
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

    private void StartAsyncScan(bool forceRefresh = false) {
        MethodInvoker initUI = delegate {
            progressBar.Value = 0;
            progressBar.Visible = true;
            lblTitle.Text = "Scanning Resource Locks (Turbo Mode)...";
        };

        if (this.InvokeRequired) {
            this.BeginInvoke(initUI);
        } else {
            initUI();
        }

        // Prevent Windows ThreadPool delay by aggressively forcing instantaneous worker threads
        int minWorker, minIOC;
        ThreadPool.GetMinThreads(out minWorker, out minIOC);
        ThreadPool.SetMinThreads(Math.Max(minWorker, Environment.ProcessorCount * 16), Math.Max(minIOC, 64));

        Task.Factory.StartNew(delegate {
            try {
                Log("Initiating Scan...");
                List<ProcessItem> results = Run3TierScan(targetPath, forceRefresh, delegate(int val) {
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
                Log("Scan error: " + ex.Message);
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

        Log("Populated UI with " + items.Count + " locking processes.");
        if (listView.Items.Count == 0) {
            ListViewItem emptyItem = new ListViewItem(new string[] { "N/A", "N/A", "No locking processes found." });
            listView.Items.Add(emptyItem);
            btnKill.Enabled = false;
            btnKillAll.Enabled = false;
        } else {
            btnKillAll.Enabled = true;
        }
    }

    // High-speed API collision check to instantly reveal locks without opening the file
    private static bool IsPathLockedFast(string path, bool isDir) {
        uint flags = isDir ? FILE_FLAG_BACKUP_SEMANTICS : 0;
        IntPtr handle = CreateFile(path, 0, FILE_SHARE_NONE, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        
        if (handle == INVALID_HANDLE_VALUE) {
            int err = Marshal.GetLastWin32Error();
            return (err == 32 || err == 33); // 32 = ERROR_SHARING_VIOLATION, 33 = ERROR_LOCK_VIOLATION
        }
        CloseHandle(handle);
        return false;
    }

    private List<ProcessItem> Run3TierScan(string path, bool forceRefresh, Action<int> progressCallback) {
        var finalLockingProcesses = new List<ProcessItem>();
        var addedPids = new HashSet<int>();

        progressCallback(5);
        RefreshProcessSnapshot(forceRefresh);
        progressCallback(10);

        string normalizedPath = path;
        bool isDir = Directory.Exists(path);
        if (isDir && !normalizedPath.EndsWith(Path.DirectorySeparatorChar.ToString()) && !normalizedPath.EndsWith(Path.AltDirectorySeparatorChar.ToString())) {
            normalizedPath += Path.DirectorySeparatorChar;
        }

        // Tier 1: Check Process Executable Paths (Finds apps running *from* inside the folder)
        lock (CacheLock) {
            foreach (KeyValuePair<int, string> kvp in ProcessPathMap) {
                int pid = kvp.Key;
                string procPath = kvp.Value;
                if (procPath != null) {
                    bool match = isDir ? 
                        (procPath.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) || procPath.Equals(path, StringComparison.OrdinalIgnoreCase)) :
                        procPath.Equals(path, StringComparison.OrdinalIgnoreCase);

                    if (match && addedPids.Add(pid)) {
                        finalLockingProcesses.Add(new ProcessItem {
                            Pid = pid,
                            Name = GetProcessName(pid),
                            Path = procPath
                        });
                    }
                }
            }
        }
        progressCallback(20);

        // Tier 2: Highly Aggressive Multi-Threaded Pre-Collision Scan
        // Blasts through huge directories concurrently using ThreadPool Injection
        var lockedPaths = new ConcurrentBag<string>();
        
        if (isDir) {
            if (IsPathLockedFast(path, true)) lockedPaths.Add(path);

            int scannedCount = 0;
            long lastUpdate = DateTime.Now.Ticks;
            var tcs = new TaskCompletionSource<bool>();
            int activeWorkers = 1; // Starts at 1 for the root directory thread

            // C# 5.0 compatible Action delegate wrapper for recursion
            Action<string> ProcessDirectory = null;
            ProcessDirectory = delegate(string dir)
            {
                try
                {
                    string[] files = Directory.GetFiles(dir);
                    foreach (string file in files) {
                        int c = Interlocked.Increment(ref scannedCount);
                        
                        // Sparse UI updates to prevent thread blocking (Updates only once per 60ms)
                        if (c % 1000 == 0) {
                            long now = DateTime.Now.Ticks;
                            long lastTime = Interlocked.Read(ref lastUpdate);
                            if (TimeSpan.FromTicks(now - lastTime).TotalMilliseconds > 60) {
                                Interlocked.Exchange(ref lastUpdate, now);
                                progressCallback(20 + Math.Min(50, c / 2000));
                            }
                        }

                        if (IsPathLockedFast(file, false)) {
                            lockedPaths.Add(file);
                        }
                    }

                    string[] subDirs = Directory.GetDirectories(dir);
                    foreach (string sub in subDirs) {
                        if (IsPathLockedFast(sub, true)) lockedPaths.Add(sub);

                        try {
                            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) {
                                Interlocked.Increment(ref activeWorkers);
                                string safeSub = sub; // Fix modified closure for older C# versions
                                ThreadPool.QueueUserWorkItem(delegate { ProcessDirectory(safeSub); });
                            }
                        } catch { } // Ignore inaccessible sub-folders
                    }
                }
                catch { } // Ignore locked master folders
                finally {
                    if (Interlocked.Decrement(ref activeWorkers) == 0) {
                        tcs.TrySetResult(true);
                    }
                }
            };

            // Kick off the multi-threaded blitz
            ThreadPool.QueueUserWorkItem(delegate { ProcessDirectory(path); });
            tcs.Task.Wait(); // Wait for all concurrent workers to exhaust the directory tree

        } else if (File.Exists(path)) {
            if (IsPathLockedFast(path, false)) lockedPaths.Add(path);
        }

        progressCallback(75);

        // Tier 3: Resolve PIDs using Restart Manager exclusively on Confirmed Locked files
        var lockedArray = lockedPaths.ToArray();
        if (lockedArray.Length > 0) {
            int chunkSize = 2000;
            int totalChunks = (lockedArray.Length + chunkSize - 1) / chunkSize;

            for (int i = 0; i < totalChunks; i++) {
                var chunk = new List<string>();
                for (int j = i * chunkSize; j < Math.Min((i + 1) * chunkSize, lockedArray.Length); j++) {
                    chunk.Add(lockedArray[j]);
                }

                var rmPids = GetProcessesLockingFiles(chunk.ToArray());
                
                foreach (int pid in rmPids) {
                    if (addedPids.Add(pid)) {
                        finalLockingProcesses.Add(new ProcessItem {
                            Pid = pid,
                            Name = GetProcessName(pid),
                            Path = GetProcessPath(pid) ?? "Unknown (Protected/Elevated)"
                        });
                    }
                }
                
                int progress = 75 + (int)(((i + 1) / (double)totalChunks) * 20); // Scales 75 -> 95
                progressCallback(progress);
            }
        }

        progressCallback(100);
        return finalLockingProcesses;
    }

    public static bool IsFileLockedHeuristic(string filePath) {
        return IsPathLockedFast(filePath, false);
    }

    public static List<int> GetProcessesLockingFiles(string[] paths) {
        var pids = new List<int>();
        if (paths == null || paths.Length == 0) return pids;

        var validPaths = new List<string>(paths.Length);
        foreach (string p in paths) {
            try {
                if (File.Exists(p) || Directory.Exists(p)) validPaths.Add(p);
            } catch { }
        }
        if (validPaths.Count == 0) return pids;

        uint handle;
        string key = Guid.NewGuid().ToString();
        int res = RmStartSession(out handle, 0, key);
        if (res != 0) return pids;

        try {
            string[] pathArray = validPaths.ToArray();
            res = RmRegisterResources(handle, (uint)pathArray.Length, pathArray, 0, null, 0, null);
            
            // Failsafe: Passing directories directly can cause ERROR_ACCESS_DENIED (Code 5) on older Windows.
            // When this happens, we strip directories from the list and only use RM on the raw files.
            if (res == 5) {
                RmEndSession(handle);
                
                var filesOnly = new List<string>();
                foreach (string p in validPaths) {
                    try {
                        if (File.Exists(p)) filesOnly.Add(p);
                    } catch { }
                }
                
                if (filesOnly.Count == 0) return pids;
                
                res = RmStartSession(out handle, 0, Guid.NewGuid().ToString());
                if (res != 0) return pids;
                
                pathArray = filesOnly.ToArray();
                res = RmRegisterResources(handle, (uint)pathArray.Length, pathArray, 0, null, 0, null);
            }

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

    public static void RefreshProcessSnapshot(bool force = false) {
        lock (CacheLock) {
            if (!force && (DateTime.UtcNow - lastSnapshotTime < CacheTtl)) return;

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

                        string fullPath = QueryProcessPathDirect(pid);
                        if (fullPath != null) {
                            ProcessPathMap[pid] = fullPath;
                        }
                    } while (Process32Next(hSnapshot, ref pe32));

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

    private bool KillProcessSafely(int pid, string name) {
        try {
            using (var p = Process.GetProcessById(pid)) {
                Log("Attempting to terminate " + name + " (PID: " + pid + ")...");
                p.Kill();
                
                if (!p.WaitForExit(1500)) {
                    Log("Warning: " + name + " (PID: " + pid + ") did not fully exit within 1.5 seconds.");
                } else {
                    Log("Successfully terminated " + name + " (PID: " + pid + ").");
                }
            }
            return true;
        } catch (Exception ex) {
            Log("Failed to terminate " + name + " (PID: " + pid + "). Error: " + ex.Message);
            return false;
        }
    }

    private void BtnKill_Click(object sender, EventArgs e) {
        if (listView.SelectedItems.Count == 0) return;
        var item = listView.SelectedItems[0].Tag as ProcessItem;
        if (item == null) return;

        var confirm = MessageBox.Show("Are you sure you want to terminate '" + item.Name + "'?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm == DialogResult.Yes) {
            if (KillProcessSafely(item.Pid, item.Name)) {
                MessageBox.Show("Process successfully terminated.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                StartAsyncScan(true); 
            } else {
                if (!isAdmin) {
                    PromptForElevation();
                } else {
                    MessageBox.Show("Failed to terminate process. It may be a critical system service or access is denied.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
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
                if (!KillProcessSafely(pi.Pid, pi.Name)) {
                    failedAny = true;
                }
            }

            if (!failedAny) {
                MessageBox.Show("All processes successfully terminated.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                StartAsyncScan(true); 
            } else {
                if (!isAdmin) {
                    PromptForElevation();
                } else {
                    MessageBox.Show("Failed to terminate one or more processes. See the log in AppData for details.", "Incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    StartAsyncScan(true);
                }
            }
        }
    }

    private void PromptForElevation() {
        Log("Prompting user for elevation...");
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
                Log("Elevation failed: " + ex.Message);
                MessageBox.Show("Failed to elevate: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    [STAThread]
    public static void Main(string[] args) {
        if (args.Length == 1 && args[0] == "[WARMUP]") {
            try {
                string tempFile = Path.GetTempFileName();
                RefreshProcessSnapshot(true);
                IsFileLockedHeuristic(tempFile);
                GetProcessesLockingFiles(new string[] { tempFile });
                if (File.Exists(tempFile)) File.Delete(tempFile);
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