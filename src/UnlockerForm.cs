using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

public partial class UnlockerForm : Form {

    private HashSet<string> targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private List<ProcessItem> currentScanResults = new List<ProcessItem>();

    private ListView listView;
    private ImageList imageList;
    private Button btnUnlock;
    private Button btnUnlockAll;
    private Button btnKill;
    private Button btnKillAll;
    private Button btnForceDelete;
    private Button btnElevate;
    private Button btnClose;
    private Button btnAddFile;
    private Button btnAddFolder;
    private Label lblTarget;
    private Label lblAdminState;
    private Label lblStatus;
    private TextBox txtFilter;
    private ProgressBar progressBar;
    private ToolTip toolTip;
    
    private bool isAdmin;
    private string logFile;
    private static ushort CachedFileTypeIndex = 0;
    private System.Windows.Forms.Timer ipcTimer;
    
    private bool isInitializing = true;
    private bool isScanning = false;
    private bool rescanPending = false;
    private readonly object ipcLock = new object();
    private readonly object scanLock = new object();
    private static readonly Dictionary<int, string> ProcessPathMap = new Dictionary<int, string>();
    private static readonly Dictionary<int, string> ProcessNameMap = new Dictionary<int, string>();
    private static DateTime lastSnapshotTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(8);
    private static readonly object CacheLock = new object();

    private static readonly Dictionary<string, Icon> IconCache = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
    private static Icon defaultFileIcon;
    private static readonly object iconCacheLock = new object();
    public UnlockerForm(List<string> paths) {
        isInitializing = true; 

        foreach (var p in paths) {
            if (!string.IsNullOrEmpty(p)) {
                targetPaths.Add(p.TrimEnd('"'));
            }
        }
        
        WindowsIdentity id = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(id);
        isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

        if (isAdmin) EnableDebugPrivilege();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logDir = Path.Combine(appData, "UnBlock");
        Directory.CreateDirectory(logDir);
        logFile = Path.Combine(logDir, "UnBlock.log");

        Log("======================================");
        Log("UnBlock Started (Turbo Multi-Target Mode)");
        Log("Running as Administrator: " + isAdmin);

        InitFileTypeIndex(); 
        InitializeComponent();
        SetupIpcTimer(); 

        isInitializing = false; 

        UpdateTargetLabel();
        StartAsyncScan(false); 
    }
    private static void EnableDebugPrivilege() {
        IntPtr token;
        if (OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token)) {
            LUID luid;
            if (LookupPrivilegeValue(null, "SeDebugPrivilege", out luid)) {
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Luid = luid;
                tp.Attributes = 0x00000002;
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            CloseHandle(token);
        }
    }

    private void Log(string message) {
        try {
            File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + message + Environment.NewLine);
        } catch { } 
    }
    private void UpdateButtonStates() {
        bool scanning;
        lock (scanLock) { scanning = isScanning; }

        bool hasSelection = listView.SelectedItems.Count > 0 && listView.SelectedItems[0].Tag is ProcessItem;
        bool hasItems = currentScanResults.Count > 0 && !scanning;
        hasSelection = hasSelection && !scanning;

        btnUnlock.Enabled = hasSelection;
        btnKill.Enabled = hasSelection;
        btnUnlockAll.Enabled = hasItems;
        btnKillAll.Enabled = hasItems;
        btnForceDelete.Enabled = targetPaths.Count > 0 && !scanning;
    }

    private void UpdateTargetLabel() {
        if (targetPaths.Count == 0) {
            lblTarget.Text = "No files or folders selected";
            SetStatus("Add a file or folder to begin.");
        } else if (targetPaths.Count == 1) {
            string singlePath = "";
            foreach (var p in targetPaths) { singlePath = p; break; }
            lblTarget.Text = singlePath;
        } else {
            lblTarget.Text = string.Format("{0} items queued for analysis", targetPaths.Count);
        }
    }

    private void SetupIpcTimer() {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pendingDir = Path.Combine(appData, "UnBlock\\Pending");
        try {
            Directory.CreateDirectory(pendingDir);
        } catch { }

        ipcTimer = new System.Windows.Forms.Timer();
        ipcTimer.Interval = 50; 
        ipcTimer.Tick += (sender, e) => {
            ProcessExistingPendingFiles(pendingDir);
        };
        ipcTimer.Start();
    }

    private void ProcessExistingPendingFiles(string pendingDir) {
        try {
            if (!Directory.Exists(pendingDir)) return;
            string[] files = Directory.GetFiles(pendingDir, "*.tmp");
            foreach (string file in files) {
                ProcessSinglePendingFile(file);
            }
        } catch { }
    }

    private void ProcessSinglePendingFile(string filePath) {
        string[] lines = null;
        lock (ipcLock) {
            for (int i = 0; i < 10; i++) {
                try {
                    if (File.Exists(filePath)) {
                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                        using (var r = new StreamReader(fs)) {
                            List<string> raw = new List<string>();
                            string l;
                            while ((l = r.ReadLine()) != null) raw.Add(l);
                            lines = raw.ToArray();
                        }
                        break;
                    }
                } catch (IOException) {
                    Thread.Sleep(10);
                }
            }

            try {
                if (File.Exists(filePath)) File.Delete(filePath);
            } catch { }
        }

        if (lines != null && lines.Length > 0) {
            IngestLines(lines);
        }
    }

    private void IngestLines(string[] lines) {
        lock (ipcLock) {
            bool addedNew = false;
            foreach (string line in lines) {
                if (!string.IsNullOrEmpty(line)) {
                    string clean = line.Trim('"', ' ');
                    if (targetPaths.Add(clean)) {
                        addedNew = true;
                    }
                }
            }

            if (addedNew) {
                UpdateTargetLabel();
                if (!isInitializing) {
                    RequestScan();
                }
            }
        }
    }

    private void RequestScan() {
        bool scanning;
        lock (scanLock) { scanning = isScanning; }
        if (scanning) {
            rescanPending = true;
        } else {
            StartAsyncScan(true);
        }
    }

    private void BtnAddFile_Click(object sender, EventArgs e) {
        using (OpenFileDialog ofd = new OpenFileDialog()) {
            ofd.Title = "Select File to Unlock";
            ofd.Multiselect = true;
            if (ofd.ShowDialog() == DialogResult.OK) {
                foreach (string file in ofd.FileNames) {
                    targetPaths.Add(file);
                }
                UpdateTargetLabel();
                RequestScan();
            }
        }
    }

    private void BtnAddFolder_Click(object sender, EventArgs e) {
        using (FolderBrowserDialog fbd = new FolderBrowserDialog()) {
            fbd.Description = "Select Folder to Unlock";
            fbd.ShowNewFolderButton = false;
            if (fbd.ShowDialog() == DialogResult.OK) {
                targetPaths.Add(fbd.SelectedPath);
                UpdateTargetLabel();
                RequestScan();
            }
        }
    }

    private void TxtFilter_TextChanged(object sender, EventArgs e) {
        ApplyFilter(txtFilter.Text.Trim());
    }

    private void ListView_DoubleClick(object sender, EventArgs e) {
        if (listView.SelectedItems.Count > 0) {
            var pItem = listView.SelectedItems[0].Tag as ProcessItem;
            if (pItem != null && File.Exists(pItem.Path)) {
                try { Process.Start("explorer.exe", "/select,\"" + pItem.Path + "\""); } catch { }
            }
        }
    }
    private void StartAsyncScan(bool forceRefresh = false) {
        lock (scanLock) {
            if (isScanning) return; 
            isScanning = true;
        }

        HashSet<string> targetsSnapshot;
        lock (ipcLock) {
            if (targetPaths.Count == 0) {
                MethodInvoker updateEmptyUI = delegate {
                    progressBar.Visible = false;
                    currentScanResults.Clear();
                    ShowListMessage("No target selected yet - right-click any file or folder in Explorer and choose UnBlock, or use '+ File' / '+ Folder' above.");
                    SetStatus("Add a file or folder to begin.");
                    UpdateButtonStates();
                    lock (scanLock) { isScanning = false; }
                };

                if (this.InvokeRequired) this.BeginInvoke(updateEmptyUI);
                else updateEmptyUI();

                return;
            }
            targetsSnapshot = new HashSet<string>(targetPaths, StringComparer.OrdinalIgnoreCase);
        }

        MethodInvoker initUI = delegate {
            progressBar.Value = 0;
            progressBar.Visible = true;
            SetStatus("Scanning for locks...");
            listView.Items.Clear();
            UpdateButtonStates();
        };

        if (this.InvokeRequired) this.BeginInvoke(initUI);
        else initUI();

        int minW, minI;
        ThreadPool.GetMinThreads(out minW, out minI);
        ThreadPool.SetMinThreads(Math.Max(minW, Environment.ProcessorCount * 16 + 100), minI);

        Task.Factory.StartNew(delegate {
            try {
                Log("Initiating Scan...");
                List<ProcessItem> results = RunFastHandleScan(targetsSnapshot, forceRefresh, delegate(int val) {
                    this.BeginInvoke(new MethodInvoker(delegate {
                        if (progressBar.Value != val) progressBar.Value = Math.Min(100, Math.Max(0, val));
                    }));
                });

                this.BeginInvoke(new MethodInvoker(delegate {
                    progressBar.Visible = false;
                    if (results.Count == 1) {
                        SetStatus("1 locking process found.", Color.FromArgb(192, 57, 43));
                    } else if (results.Count > 1) {
                        SetStatus(string.Format("{0} locking processes found.", results.Count), Color.FromArgb(192, 57, 43));
                    } else {
                        SetStatus("No active locks found.", Color.FromArgb(39, 174, 96));
                    }
                    currentScanResults = results;
                    ApplyFilter(txtFilter.Text.Trim());
                    lock (scanLock) { isScanning = false; }
                    UpdateButtonStates();
                    if (rescanPending) {
                        rescanPending = false;
                        StartAsyncScan(true);
                    }
                }));
            } catch (Exception ex) {
                Log("Scan error: " + ex.Message);
                this.BeginInvoke(new MethodInvoker(delegate {
                    progressBar.Visible = false;
                    SetStatus("Scan failed: " + ex.Message, Color.FromArgb(192, 57, 43));
                    MessageBox.Show("Scan error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    lock (scanLock) { isScanning = false; }
                    UpdateButtonStates();
                    if (rescanPending) {
                        rescanPending = false;
                        StartAsyncScan(true);
                    }
                }));
            }
        });
    }

    private static string GetAccessInfo(ProcessItem item) {
        if (item.IsModuleLock) {
            return item.IsDir ? "Loaded Module Directory Lock" : "Active DLL / Module Lock";
        }

        uint grantedAccess = item.GrantedAccess;
        bool isDir = item.IsDir;

        // Evaluate typical bitmask flags for read, write, and delete permissions
        bool hasWrite = (grantedAccess & 0x0002) != 0 || (grantedAccess & 0x0004) != 0 || (grantedAccess & 0x0100) != 0 || (grantedAccess & 0x00040000) != 0;
        bool hasDelete = (grantedAccess & 0x00010000) != 0;
        bool hasRead = (grantedAccess & 0x0001) != 0;

        if (isDir) {
            if (hasWrite && hasDelete) return "Full Directory Control (Lock)";
            if (hasWrite) return "Directory Modify (Lock)";
            if (hasDelete) return "Directory Delete (Lock)";
            if (hasRead) return "Benign Directory Browse";
            return "Benign Directory Monitor";
        } else {
            if (hasWrite && hasDelete) return "Exclusive Write/Delete Lock";
            if (hasWrite) return "Active Write Lock";
            if (hasDelete) return "Delete-On-Close Lock";
            if (hasRead) return "Active Read Lock";
            return "Benign File Monitor";
        }
    }

    private static Severity GetSeverity(string accessInfo) {
        if (accessInfo.StartsWith("Benign")) {
            return Severity.Low;
        }
        if (accessInfo.StartsWith("Active Read")) {
            return Severity.Medium;
        }
        return Severity.High; 
    }

    private void ApplyFilter(string filterText) {
        listView.BeginUpdate();
        listView.Items.Clear();
        imageList.Images.Clear();

        Icon defaultIcon;
        lock (iconCacheLock) {
            if (defaultFileIcon == null) {
                try {
                    IntPtr hIcon = ExtractIcon(IntPtr.Zero, "shell32.dll", 2); 
                    if (hIcon != IntPtr.Zero) defaultFileIcon = Icon.FromHandle(hIcon);
                } catch { }
            }
            defaultIcon = defaultFileIcon;
        }

        int iconIndex = 0;
        var filtered = currentScanResults;
        if (!string.IsNullOrEmpty(filterText)) {
            filtered = currentScanResults.FindAll(x => 
                (x.Name != null && x.Name.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (x.Pid.ToString().Contains(filterText)) ||
                (x.Path != null && x.Path.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0)
            );
        }

        foreach (var item in filtered) {
            Icon procIcon = null;
            if (!string.IsNullOrEmpty(item.Path)) {
                lock (iconCacheLock) {
                    if (!IconCache.TryGetValue(item.Path, out procIcon)) {
                        try {
                            procIcon = File.Exists(item.Path) ? Icon.ExtractAssociatedIcon(item.Path) : null;
                        } catch { procIcon = null; }
                        IconCache[item.Path] = procIcon;
                    }
                }
            }
            if (procIcon == null) procIcon = defaultIcon;

            if (procIcon != null) imageList.Images.Add(procIcon);
            else {
                Bitmap bmp = new Bitmap(16, 16);
                imageList.Images.Add(bmp);
            }

            string accessInfo = GetAccessInfo(item);
            Severity severity = GetSeverity(accessInfo);

            ListViewItem lvi = new ListViewItem(new string[] { 
                item.Name, 
                item.Pid.ToString(), 
                accessInfo, 
                item.Path 
            });
            lvi.ImageIndex = iconIndex;
            lvi.Tag = item;

            lvi.UseItemStyleForSubItems = false;
            if (severity == Severity.High) {
                lvi.ForeColor = Color.Black;
                lvi.SubItems[0].ForeColor = Color.FromArgb(44, 62, 80); 
                lvi.SubItems[1].ForeColor = Color.FromArgb(44, 62, 80);
                lvi.SubItems[2].ForeColor = Color.FromArgb(192, 57, 43); 
                lvi.SubItems[2].Font = new Font(listView.Font, FontStyle.Bold);
                lvi.SubItems[3].ForeColor = Color.FromArgb(44, 62, 80);
            } else if (severity == Severity.Medium) {
                lvi.ForeColor = Color.Black;
                lvi.SubItems[0].ForeColor = Color.FromArgb(44, 62, 80);
                lvi.SubItems[1].ForeColor = Color.FromArgb(44, 62, 80);
                lvi.SubItems[2].ForeColor = Color.FromArgb(211, 84, 0); 
                lvi.SubItems[2].Font = new Font(listView.Font, FontStyle.Bold);
                lvi.SubItems[3].ForeColor = Color.FromArgb(44, 62, 80);
            } else {
                lvi.ForeColor = Color.Gray;
                lvi.SubItems[0].ForeColor = Color.Gray;
                lvi.SubItems[1].ForeColor = Color.Gray;
                lvi.SubItems[2].ForeColor = Color.FromArgb(39, 174, 96); 
                lvi.SubItems[2].Font = new Font(listView.Font, FontStyle.Bold);
                lvi.SubItems[3].ForeColor = Color.Gray;
            }

            listView.Items.Add(lvi);
            iconIndex++;
        }

        if (listView.Items.Count == 0) {
            if (currentScanResults.Count == 0) {
                ListViewItem emptyItem = new ListViewItem(new string[] { "", "", "", "No locking processes found - the target(s) are free to modify or delete." });
                emptyItem.ForeColor = Color.FromArgb(39, 174, 96);
                listView.Items.Add(emptyItem);
            } else {
                ListViewItem emptyItem = new ListViewItem(new string[] { "", "", "", "No results match the current search." });
                emptyItem.ForeColor = Color.Gray;
                listView.Items.Add(emptyItem);
            }
        }

        listView.EndUpdate();
        UpdateButtonStates();
    }
    private bool UnlockSafely(int pid, List<IntPtr> handles, string name) {
        if (handles.Count == 0) return true;
        Log("Attempting to unlock handles for " + name + " (PID: " + pid + ")...");
        IntPtr hProcess = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
        if (hProcess == IntPtr.Zero) {
            Log("Failed to open process for handle duplication.");
            return false;
        }

        try {
            bool allSuccess = true;
            foreach (IntPtr handle in handles) {
                IntPtr dupHandle;
                if (DuplicateHandle(hProcess, handle, GetCurrentProcess(), out dupHandle, 0, false, DUPLICATE_CLOSE_SOURCE)) {
                    CloseHandle(dupHandle);
                } else {
                    allSuccess = false;
                }
            }
            return allSuccess;
        } finally {
            CloseHandle(hProcess);
        }
    }

    private bool KillProcessSafely(int pid, string name) {
        if (pid == 4) return false;
        try {
            using (var p = Process.GetProcessById(pid)) {
                Log("Attempting to terminate " + name + " (PID: " + pid + ")...");
                p.Kill();
                if (!p.WaitForExit(2000)) Log("Warning: " + name + " (PID: " + pid + ") did not exit fully.");
            }
            return true;
        } catch {
            return false;
        }
    }

    private void ResetFilePermissions(string path) {
        try {
            using (var p = new Process()) {
                p.StartInfo.FileName = "takeown.exe";
                p.StartInfo.Arguments = "/F \"" + path + "\" /A"; 
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.Start();
                p.WaitForExit(3000);
            }

            using (var p = new Process()) {
                p.StartInfo.FileName = "icacls.exe";
                p.StartInfo.Arguments = "\"" + path + "\" /grant *S-1-5-32-544:F *S-1-5-32-545:F /T /C"; 
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.Start();
                p.WaitForExit(3000);
            }
        } catch { }
    }

    private static string GetSystemErrorMessage(int errCode) {
        switch (errCode) {
            case 2: return "File not found (ERROR_FILE_NOT_FOUND).";
            case 3: return "Path not found (ERROR_PATH_NOT_FOUND).";
            case 5: return "Access is denied (ERROR_ACCESS_DENIED). This means write permissions are restricted, the file is read-only, or administrator execution privileges are missing.";
            case 18: return "No more files left to analyze (ERROR_NO_MORE_FILES).";
            case 32: return "The file is being used by another active process (ERROR_SHARING_VIOLATION).";
            case 33: return "The file is locked by another active process (ERROR_LOCK_VIOLATION).";
            case 145: return "The directory is not empty (ERROR_DIR_NOT_EMPTY).";
            default: return "Unknown Windows Win32 Error.";
        }
    }

    private void ForceDeleteTargets() {
        if (targetPaths.Count == 0) {
            MessageBox.Show("No files or folders are currently loaded to delete.", "No Targets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var sbConfirm = new StringBuilder();
        sbConfirm.AppendLine("This will attempt to permanently delete the following target(s):");
        foreach (var p in targetPaths) {
            sbConfirm.AppendLine("ΓÇó " + p);
        }
        sbConfirm.AppendLine();
        sbConfirm.AppendLine("UnBlock will attempt to:");
        sbConfirm.AppendLine("1. Forcibly terminate all processes holding locks on these files.");
        sbConfirm.AppendLine("2. Strip Read-Only attributes & reset ACL security permissions.");
        sbConfirm.AppendLine("3. Instantly delete the files/directories.");
        sbConfirm.AppendLine("4. Register 'Delete-on-Reboot' as a fallback if immediate deletion fails.");
        sbConfirm.AppendLine();
        sbConfirm.AppendLine("Proceed with force-deletion?");

        if (MessageBox.Show(sbConfirm.ToString(), "Force Delete Targets?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) {
            return;
        }

        foreach (var pi in currentScanResults) {
            if (pi != null && pi.Pid != 4) {
                KillProcessSafely(pi.Pid, pi.Name);
            }
        }

        var report = new StringBuilder();
        bool anyFailedImmediate = false;

        foreach (string rawPath in targetPaths) {
            string path = rawPath.Trim();
            if (string.IsNullOrEmpty(path)) continue;

            report.AppendLine("Processing: " + path);

            bool exists = File.Exists(path) || Directory.Exists(path);
            if (!exists) {
                report.AppendLine("  -> File/Folder does not exist or was already deleted.");
                report.AppendLine();
                continue;
            }

            try {
                File.SetAttributes(path, FileAttributes.Normal);
            } catch (Exception ex) {
                report.AppendLine("  [Warning] Failed to clear file attributes: " + ex.Message);
            }

            if (isAdmin) {
                try {
                    ResetFilePermissions(path);
                    report.AppendLine("  [Success] Security permissions updated.");
                } catch (Exception ex) {
                    report.AppendLine("  [Warning] Could not reset permissions: " + ex.Message);
                }
            }

            try {
                bool success = false;
                int win32Err = 0;

                if (Directory.Exists(path)) {
                    success = RemoveDirectory(path);
                    if (!success) {
                        win32Err = Marshal.GetLastWin32Error();
                        if (win32Err == 145) { // ERROR_DIR_NOT_EMPTY
                            try {
                                Directory.Delete(path, true);
                                success = true;
                            } catch (Exception exDir) {
                                win32Err = Marshal.GetHRForException(exDir) & 0xFFFF;
                            }
                        }
                    }
                } else {
                    success = DeleteFile(path);
                    if (!success) {
                        win32Err = Marshal.GetLastWin32Error();
                    }
                }

                if (success) {
                    report.AppendLine("  [Success] File/Folder deleted successfully.");
                } else {
                    anyFailedImmediate = true;
                    report.AppendLine("  [Failed] Immediate deletion failed.");
                    report.AppendLine("  [System Error Code] " + win32Err + " - " + GetSystemErrorMessage(win32Err));

                    if (isAdmin) {
                        bool rebootRegistered = MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                        if (rebootRegistered) {
                            report.AppendLine("  [Success] Scheduled for permanent deletion on next reboot.");
                        } else {
                            int rebootErr = Marshal.GetLastWin32Error();
                            report.AppendLine("  [Failed] Could not schedule for reboot deletion (Error: " + rebootErr + ").");
                        }
                    } else {
                        report.AppendLine("  [Error] Standard user privileges cannot schedule reboot deletion.");
                    }
                }
            } catch (Exception ex) {
                anyFailedImmediate = true;
                int win32Err = Marshal.GetHRForException(ex) & 0xFFFF;
                report.AppendLine("  [Failed] Immediate deletion failed with exception: " + ex.Message);
                report.AppendLine("  [System Error Code] " + win32Err + " - " + GetSystemErrorMessage(win32Err));
            }
            report.AppendLine();
        }

        string title = anyFailedImmediate ? "Deletion Summary (Immediate Action Failed)" : "Deletion Successful";
        var icon = anyFailedImmediate ? MessageBoxIcon.Warning : MessageBoxIcon.Information;
        
        MessageBox.Show(report.ToString(), title, MessageBoxButtons.OK, icon);
        StartAsyncScan(true);
    }

    private void BtnUnlock_Click(object sender, EventArgs e) {
        if (listView.SelectedItems.Count == 0) return;
        var item = listView.SelectedItems[0].Tag as ProcessItem;
        if (item == null) return;

        if (item.Handles.Count == 0 || item.IsModuleLock) {
            MessageBox.Show("This file or directory is loaded directly as a running process, DLL, or mapped module. It cannot be unlocked by closing handles; you must terminate the process to free it.", "Cannot Unlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (UnlockSafely(item.Pid, item.Handles, item.Name)) {
            MessageBox.Show("Handle(s) successfully closed.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            StartAsyncScan(true); 
        } else {
            if (!isAdmin) PromptForElevation();
            else MessageBox.Show("Failed to close handle. The process might be heavily protected or kernel-level.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BtnUnlockAll_Click(object sender, EventArgs e) {
        bool failedAny = false;
        bool hasProcessExecs = false;
        
        foreach (var pi in currentScanResults) {
            if (pi != null) {
                if (pi.Handles.Count == 0 || pi.IsModuleLock) hasProcessExecs = true;
                else if (!UnlockSafely(pi.Pid, pi.Handles, pi.Name)) failedAny = true;
            }
        }

        if (!failedAny && !hasProcessExecs) {
            MessageBox.Show("All compatible handles successfully closed.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            StartAsyncScan(true); 
        } else if (hasProcessExecs && !failedAny) {
            MessageBox.Show("Closed active handles, but some processes are executing directly or loading DLLs from a target folder and must be terminated manually.", "Partial Success", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            StartAsyncScan(true);
        } else {
            if (!isAdmin) PromptForElevation();
            else MessageBox.Show("Failed to close one or more handles. Some apps may require forced termination.", "Incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            StartAsyncScan(true);
        }
    }

    private void BtnKill_Click(object sender, EventArgs e) {
        if (listView.SelectedItems.Count == 0) return;
        var item = listView.SelectedItems[0].Tag as ProcessItem;
        if (item == null) return;

        if (item.Pid == 4) {
            MessageBox.Show("You cannot terminate the Windows System Kernel.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (MessageBox.Show("Are you sure you want to forcibly terminate '" + item.Name + "'?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
            if (KillProcessSafely(item.Pid, item.Name)) {
                MessageBox.Show("Process successfully terminated.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                StartAsyncScan(true); 
            } else {
                if (!isAdmin) PromptForElevation();
                else MessageBox.Show("Failed to terminate process.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void BtnKillAll_Click(object sender, EventArgs e) {
        if (MessageBox.Show("Are you sure you want to kill ALL locking processes?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        bool failedAny = false;
        foreach (var pi in currentScanResults) {
            if (pi != null && pi.Pid != 4) {
                if (!KillProcessSafely(pi.Pid, pi.Name)) failedAny = true;
            }
        }

        if (!failedAny) {
            MessageBox.Show("Processes successfully terminated.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            StartAsyncScan(true); 
        } else {
            if (!isAdmin) PromptForElevation();
            else MessageBox.Show("Failed to terminate one or more processes.", "Incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            StartAsyncScan(true);
        }
    }

    private void BtnForceDelete_Click(object sender, EventArgs e) {
        ForceDeleteTargets();
    }

    private void BtnElevate_Click(object sender, EventArgs e) {
        PromptForElevation();
    }

    private void PromptForElevation() {
        try {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Application.ExecutablePath;
            
            StringBuilder argsBuilder = new StringBuilder();
            foreach (string path in targetPaths) {
                argsBuilder.AppendFormat("\"{0}\" ", path);
            }
            psi.Arguments = argsBuilder.ToString().TrimEnd();
            psi.Verb = "runas";
            Process.Start(psi);
            this.Close();
        } catch (Exception ex) {
            MessageBox.Show("Elevation failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        if (ipcTimer != null) {
            ipcTimer.Stop();
            ipcTimer.Dispose();
        }
        base.OnFormClosed(e);
    }
}
