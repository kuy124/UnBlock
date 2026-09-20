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
    private Button btnKill;
    private Button btnKillRecycle;
    private Button btnKillRename;
    private Button btnKillMove;
    private Button btnKillCopy;
    private Button btnReload;
    private Button btnMore;
    private ContextMenuStrip moreMenu;
    private ToolStripMenuItem miUnlockAll;
    private ToolStripMenuItem miKillAll;
    private ToolStripMenuItem miKillPermanent;
    private ToolStripMenuItem miKillRestart;
    private ToolStripMenuItem miRepairPermissions;
    private ToolStripMenuItem miCancelScan;
    private ToolStripMenuItem miCancelAction;
    private ToolStripMenuItem miClearTargets;
    private Button btnElevate;
    private Button btnClose;
    private Button btnAddFile;
    private Button btnAddFolder;
    private Button btnThemeToggle;
    private Label lblTarget;
    private Label lblAdminState;
    private Label lblStatus;
    private Label lblAppTitle;
    private TextBox txtFilter;
    private ProgressBar progressBar;
    private ToolTip toolTip;
    private Panel headerPanel;
    private FlowLayoutPanel headerButtons;
    private Panel toolbarBorder;
    private Panel actionBarBorder;
    private Panel actionBar;
    private FlowLayoutPanel actionStrip;
    private Panel actionStripHost;
    private Panel closeHost;
    private UiTheme theme;
    
    private bool isAdmin;
    private string logFile;
    private static ushort CachedFileTypeIndex = 0;
    private static readonly object fileTypeIndexLock = new object();
    private System.Windows.Forms.Timer ipcTimer;
    
    private bool isInitializing = true;
    private bool isScanning = false;
    private bool rescanPending = false;
    private bool isActionRunning = false;
    private CancellationTokenSource scanCancellation;
    private CancellationTokenSource operationCancellation;
    private ScanResult lastScanResult;
    private string pendingActionId;
    private FileActionRequest pendingElevationRequest;
    private readonly object ipcLock = new object();
    private readonly object scanLock = new object();
    private static readonly Dictionary<int, string> ProcessPathMap = new Dictionary<int, string>();
    private static readonly Dictionary<int, string> ProcessNameMap = new Dictionary<int, string>();
    private static readonly Dictionary<int, int> ProcessParentMap = new Dictionary<int, int>();
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

        theme = UiTheme.For(LoadThemePreference());

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

    private static UiTheme.Mode LoadThemePreference() {
        try {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\UnBlock")) {
                string saved = key == null ? null : key.GetValue("UiTheme") as string;
                if (string.Equals(saved, "dark", StringComparison.OrdinalIgnoreCase)) return UiTheme.Mode.Dark;
                if (string.Equals(saved, "light", StringComparison.OrdinalIgnoreCase)) return UiTheme.Mode.Light;
            }
        } catch { }

        // No saved choice: follow the Windows app theme so the tool matches the desktop.
        try {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                object appsUseLight = key == null ? null : key.GetValue("AppsUseLightTheme");
                if (appsUseLight is int && (int)appsUseLight == 0) return UiTheme.Mode.Dark;
            }
        } catch { }
        return UiTheme.Mode.Light;
    }

    private void SaveThemePreference(UiTheme.Mode mode) {
        try {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\UnBlock")) {
                if (key != null) key.SetValue("UiTheme", mode == UiTheme.Mode.Dark ? "dark" : "light");
            }
        } catch { }
    }

    private void BtnThemeToggle_Click(object sender, EventArgs e) {
        UiTheme.Mode next = theme.IsDark ? UiTheme.Mode.Light : UiTheme.Mode.Dark;
        ApplyTheme(UiTheme.For(next));
        SaveThemePreference(next);
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
        bool actionRunning;
        lock (scanLock) { scanning = isScanning; actionRunning = isActionRunning; }

        bool hasSelection = listView.SelectedItems.Count > 0 && listView.SelectedItems[0].Tag is ProcessItem;
        bool hasItems = currentScanResults.Count > 0 && !scanning && !actionRunning;
        hasSelection = hasSelection && !scanning && !actionRunning;

        btnUnlock.Enabled = hasSelection;
        btnKill.Enabled = hasSelection;
        btnKillRecycle.Enabled = targetPaths.Count > 0 && !scanning && !actionRunning;
        btnKillRename.Enabled = targetPaths.Count == 1 && !scanning && !actionRunning;
        btnKillMove.Enabled = targetPaths.Count > 0 && !scanning && !actionRunning;
        btnKillCopy.Enabled = targetPaths.Count > 0 && !scanning && !actionRunning;
        btnReload.Enabled = !scanning && !actionRunning && targetPaths.Count > 0;
        if (btnMore != null) btnMore.Enabled = true;

        if (miUnlockAll != null) miUnlockAll.Enabled = hasItems;
        if (miKillAll != null) miKillAll.Enabled = hasItems;
        if (miKillPermanent != null) miKillPermanent.Enabled = targetPaths.Count > 0 && !scanning && !actionRunning;
        if (miKillRestart != null) miKillRestart.Enabled = targetPaths.Count > 0 && !scanning && !actionRunning;
        if (miRepairPermissions != null) miRepairPermissions.Enabled = targetPaths.Count > 0 && !scanning && !actionRunning;
        if (miCancelScan != null) miCancelScan.Enabled = scanning;
        if (miCancelAction != null) miCancelAction.Enabled = actionRunning;
        if (miClearTargets != null) miClearTargets.Enabled = targetPaths.Count > 0 && !scanning && !actionRunning;
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
        PendingActionStore.CleanupExpired();

        ipcTimer = new System.Windows.Forms.Timer();
        ipcTimer.Interval = 50; 
        ipcTimer.Tick += (sender, e) => {
            ProcessExistingPendingFiles(pendingDir);
        };
        ipcTimer.Start();
    }

    private void ProcessExistingPendingFiles(string pendingDir) {
        try {
            if (!string.IsNullOrEmpty(pendingActionId)) {
                FileActionResult result = FileActionCoordinator.ConsumeResult(pendingActionId);
                if (result != null) {
                    BeginInvoke(new MethodInvoker(delegate { FinishFileAction(result); }));
                }
            }
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

    private void UnlockerForm_DragEnter(object sender, DragEventArgs e) {
        e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void UnlockerForm_DragDrop(object sender, DragEventArgs e) {
        string[] dropped = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
        if (dropped == null) return;
        bool added = false;
        foreach (string path in dropped) {
            if (!string.IsNullOrEmpty(path) && targetPaths.Add(path)) added = true;
        }
        if (added) {
            UpdateTargetLabel();
            RequestScan();
        }
    }

    private void BtnCancelScan_Click(object sender, EventArgs e) {
        if (scanCancellation != null) scanCancellation.Cancel();
    }

    private void BtnClearTargets_Click(object sender, EventArgs e) {
        if (targetPaths.Count == 0) return;
        if (MessageBox.Show(this, "Remove all queued targets from this window?", "Clear Targets", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        targetPaths.Clear();
        currentScanResults.Clear();
        lastScanResult = null;
        UpdateTargetLabel();
        ShowListMessage("No targets queued. Drag files or folders here, or use + File / + Folder.");
        UpdateButtonStates();
    }

    private List<string> GetTargetSnapshot() {
        lock (ipcLock) return new List<string>(targetPaths);
    }

    private void BeginKillDelete(DeleteMode mode, bool advanced) {
        List<string> targets = GetTargetSnapshot();
        if (targets.Count == 0) return;
        if (advanced && (!isAdmin || MessageBox.Show(this, "This permanently deletes a protected Windows target. Windows may become unstable. Continue?", "Advanced Protected Target Action", MessageBoxButtons.YesNo, MessageBoxIcon.Stop) != DialogResult.Yes)) return;
        var requests = new List<FileOperationRequest>();
        foreach (string path in targets) requests.Add(new FileOperationRequest { Kind = FileOperationKind.Delete, DeleteMode = mode, SourcePath = path });
        BeginKillFileAction(requests, mode == DeleteMode.RecycleBin ? "Kill & Recycle" : mode == DeleteMode.Permanent ? "Kill & Permanent Delete" : "Kill & Delete at Restart", advanced);
    }

    private void BeginKillRename() {
        List<string> targets = GetTargetSnapshot();
        if (targets.Count == 0) return;
        string source = targets[0];
        string currentName = Path.GetFileName(source.TrimEnd('\\', '/'));
        string newName = PromptForText("Rename Target", "New file or folder name:", currentName);
        if (string.IsNullOrEmpty(newName)) return;
        BeginKillFileAction(new List<FileOperationRequest> {
            new FileOperationRequest { Kind = FileOperationKind.Rename, SourcePath = source, DestinationPath = newName }
        }, "Kill & Rename", false);
    }

    private void BeginKillMoveOrCopy(bool copy) {
        List<string> targets = GetTargetSnapshot();
        if (targets.Count == 0) return;
        using (FolderBrowserDialog dialog = new FolderBrowserDialog()) {
            dialog.Description = copy ? "Select the destination folder for the copy:" : "Select the destination folder for the move:";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var requests = new List<FileOperationRequest>();
            foreach (string path in targets) requests.Add(new FileOperationRequest {
                Kind = copy ? FileOperationKind.Copy : FileOperationKind.Move,
                SourcePath = path,
                DestinationPath = dialog.SelectedPath,
                ReplaceExisting = false
            });
            BeginKillFileAction(requests, copy ? "Kill & Copy" : "Kill & Move", false);
        }
    }

    private void RepairTargetPermissions() {
        if (!isAdmin) {
            PromptForElevation();
            return;
        }
        List<string> targets = GetTargetSnapshot();
        if (targets.Count == 0) return;
        if (MessageBox.Show(this, "This grants full access to the local Administrators and Users groups for the selected targets. Continue?", "Repair Permissions", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var requests = new List<FileOperationRequest>();
        foreach (string path in targets) requests.Add(new FileOperationRequest { Kind = FileOperationKind.RepairPermissions, SourcePath = path });
        BeginDirectFileAction(requests, "Repair Permissions");
    }

    private void BeginKillFileAction(List<FileOperationRequest> requests, string title, bool allowProtectedTargets) {
        FileActionRequest action = new FileActionRequest {
            Title = title,
            Operations = requests,
            LockActionMode = LockActionMode.DetectedLockersOnly,
            AllowProtectedTargets = allowProtectedTargets
        };
        StartPreparedAction(action);
    }

    private void BeginDirectFileAction(List<FileOperationRequest> requests, string title) {
        FileActionRequest action = new FileActionRequest {
            Title = title,
            Operations = requests,
            LockActionMode = LockActionMode.None,
            AllowProtectedTargets = true
        };
        StartPreparedAction(action);
    }

    private void StartPreparedAction(FileActionRequest request) {
        if (request == null || request.Operations == null || request.Operations.Count == 0) return;
        if (operationCancellation != null) operationCancellation.Cancel();
        operationCancellation = new CancellationTokenSource();
        CancellationToken token = operationCancellation.Token;
        lock (scanLock) { isActionRunning = true; }
        SetStatus("Preparing " + request.Title + "...", Color.FromArgb(52, 73, 94));
        UpdateButtonStates();
        Task.Factory.StartNew<FileActionPlan>(delegate {
            return FileActionCoordinator.Prepare(request, token, delegate(string message, int value) { ReportActionProgress(message, value); });
        }, CancellationToken.None).ContinueWith(delegate(Task<FileActionPlan> task) {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(delegate {
                if (task.IsCanceled || task.IsFaulted) {
                    FinishFileAction(new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Failed, ErrorMessage = task.Exception == null ? "Action preparation failed." : task.Exception.GetBaseException().Message });
                    return;
                }
                FileActionPlan plan = task.Result;
                if (!string.IsNullOrEmpty(plan.ErrorMessage)) {
                    FinishFileAction(new FileActionResult { ActionId = request.ActionId, Status = token.IsCancellationRequested ? FileActionStatus.Cancelled : FileActionStatus.Failed, ErrorMessage = plan.ErrorMessage });
                    return;
                }
                if (request.LockActionMode == LockActionMode.None) ExecutePreparedFileAction(request, plan, token);
                else ConfirmAndExecuteFileAction(request, plan, token, 0);
            }));
        });
    }

    private void ConfirmAndExecuteFileAction(FileActionRequest request, FileActionPlan initialPlan, CancellationToken token, int recheckCount) {
        if (token.IsCancellationRequested) {
            FinishFileAction(new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Cancelled, ErrorMessage = "Operation cancelled." });
            return;
        }
        if (!ShowKillActionConfirmation(request, initialPlan)) {
            FinishFileAction(new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Cancelled, ErrorMessage = "Action cancelled by the user." });
            return;
        }
        if (recheckCount >= 3) {
            FinishFileAction(new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Failed, ErrorMessage = "The locking processes changed repeatedly. The action was stopped before termination." });
            return;
        }
        SetStatus("Rechecking locks before termination...", Color.FromArgb(52, 73, 94));
        Task.Factory.StartNew<FileActionPlan>(delegate {
            return FileActionCoordinator.Prepare(request, token, delegate(string message, int value) { ReportActionProgress(message, value); });
        }, CancellationToken.None).ContinueWith(delegate(Task<FileActionPlan> task) {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(delegate {
                if (task.IsCanceled || task.IsFaulted) {
                    FinishFileAction(new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Failed, ErrorMessage = "The lock recheck failed." });
                    return;
                }
                FileActionPlan currentPlan = task.Result;
                if (!string.IsNullOrEmpty(currentPlan.ErrorMessage)) {
                    FinishFileAction(new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Failed, ErrorMessage = currentPlan.ErrorMessage });
                    return;
                }
                if (!string.Equals(initialPlan.LockerSignature, currentPlan.LockerSignature, StringComparison.Ordinal)) {
                    ConfirmAndExecuteFileAction(request, currentPlan, token, recheckCount + 1);
                    return;
                }
                ExecutePreparedFileAction(request, currentPlan, token);
            }));
        });
    }

    private void ExecutePreparedFileAction(FileActionRequest request, FileActionPlan plan, CancellationToken token) {
        SetStatus("Terminating detected lockers...", Color.FromArgb(192, 57, 43));
        Task.Factory.StartNew<FileActionResult>(delegate {
            return FileActionCoordinator.ExecuteConfirmed(request, plan, isAdmin, token, delegate(string message, int value) { ReportActionProgress(message, value); });
        }, CancellationToken.None).ContinueWith(delegate(Task<FileActionResult> task) {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(delegate {
                if (task.IsCanceled || task.IsFaulted) {
                    FinishFileAction(new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Failed, ErrorMessage = "The file action failed." });
                    return;
                }
                FileActionResult result = task.Result;
                if (result.Status == FileActionStatus.NeedsElevation && result.CanRetryElevated && !isAdmin) {
                    request.PreviouslyTerminatedPids = result.TerminatedPids;
                    string error;
                    if (FileActionCoordinator.LaunchElevated(request, out error)) {
                        pendingActionId = request.ActionId;
                        pendingElevationRequest = request;
                        SetStatus("Waiting for elevated retry...", Color.FromArgb(211, 84, 0));
                        UpdateButtonStates();
                    } else {
                        FinishFileAction(new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Failed, ErrorMessage = "Elevation was not started: " + error });
                    }
                    return;
                }
                FinishFileAction(result);
            }));
        });
    }

    private bool ShowKillActionConfirmation(FileActionRequest request, FileActionPlan plan) {
        StringBuilder text = new StringBuilder();
        text.AppendLine(request.Title + " will terminate detected locking processes, then continue.");
        text.AppendLine();
        text.AppendLine("Targets:");
        foreach (FileOperationRequest operation in request.Operations) text.AppendLine("  " + operation.SourcePath);
        if (plan.Lockers.Count == 0) {
            text.AppendLine();
            text.AppendLine("No active lockers are currently detected. No unrelated process will be terminated.");
        } else {
            text.AppendLine();
            text.AppendLine("Processes to terminate:");
            foreach (ProcessItem item in plan.Lockers) text.AppendLine("  " + item.Name + " (PID " + item.Pid + ") - " + (item.Path ?? "Executable unavailable"));
        }
        text.AppendLine();
        text.AppendLine("Terminated applications may lose unsaved work. Continue?");
        return MessageBox.Show(this, text.ToString(), request.Title, MessageBoxButtons.YesNo, plan.Lockers.Count == 0 ? MessageBoxIcon.Question : MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private void ReportActionProgress(string message, int value) {
        if (IsDisposed || !IsHandleCreated) return;
        try {
            BeginInvoke(new MethodInvoker(delegate {
                if (progressBar != null) {
                    progressBar.Visible = true;
                    progressBar.Value = Math.Min(100, Math.Max(0, value));
                }
                SetStatus(message, Color.FromArgb(52, 73, 94));
            }));
        } catch { }
    }

    private void FinishFileAction(FileActionResult result) {
        pendingActionId = null;
        pendingElevationRequest = null;
        lock (scanLock) { isActionRunning = false; }
        if (operationCancellation != null) operationCancellation.Dispose();
        operationCancellation = null;
        if (result != null && result.Targets != null) {
            foreach (FileActionTargetResult target in result.Targets) if (target.Success) targetPaths.Remove(target.SourcePath);
        }
        UpdateTargetLabel();
        UpdateButtonStates();
        progressBar.Visible = false;
        StringBuilder report = new StringBuilder();
        if (result != null && result.Targets != null) {
            foreach (FileActionTargetResult target in result.Targets) report.AppendLine((target.Success ? "Completed: " : "Failed: ") + target.SourcePath + (target.Success && target.Scheduled ? " (scheduled for restart)" : target.Success ? "" : " - " + target.ErrorMessage));
        }
        if (result != null && result.UnresolvablePids.Count > 0) report.AppendLine("Unresolved locking PID(s): " + string.Join(", ", result.UnresolvablePids.ToArray()));
        if (result != null && !string.IsNullOrEmpty(result.ErrorMessage)) report.AppendLine(result.ErrorMessage);
        if (result == null || result.Status == FileActionStatus.Cancelled) SetStatus("File action cancelled.", Color.FromArgb(127, 140, 141));
        else if (result.Status == FileActionStatus.Completed) SetStatus("File action completed.", Color.FromArgb(39, 174, 96));
        else SetStatus("File action completed with warnings.", Color.FromArgb(211, 84, 0));
        if (result != null && result.Status != FileActionStatus.Cancelled && report.Length > 0) MessageBox.Show(this, report.ToString(), "File Action Result", MessageBoxButtons.OK, result.Status == FileActionStatus.Completed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        StartAsyncScan(true);
    }

    private void BtnCancelAction_Click(object sender, EventArgs e) {
        if (operationCancellation != null) operationCancellation.Cancel();
        if (pendingActionId != null) {
            pendingActionId = null;
            pendingElevationRequest = null;
            lock (scanLock) { isActionRunning = false; }
            SetStatus("Elevated retry cancelled.", Color.FromArgb(127, 140, 141));
            UpdateButtonStates();
        }
    }

    private string PromptForText(string title, string labelText, string initialValue) {
        using (Form prompt = new Form()) {
            prompt.Text = title;
            prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
            prompt.StartPosition = FormStartPosition.CenterParent;
            prompt.ClientSize = new Size(420, 125);
            prompt.MinimizeBox = false;
            prompt.MaximizeBox = false;
            Label label = new Label { Text = labelText, Location = new Point(12, 12), Size = new Size(390, 20) };
            TextBox input = new TextBox { Text = initialValue, Location = new Point(12, 38), Size = new Size(390, 24) };
            Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(240, 78), Size = new Size(78, 28) };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(324, 78), Size = new Size(78, 28) };
            prompt.Controls.Add(label); prompt.Controls.Add(input); prompt.Controls.Add(ok); prompt.Controls.Add(cancel);
            prompt.AcceptButton = ok; prompt.CancelButton = cancel; input.SelectAll(); input.Focus();
            return prompt.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : null;
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

    private void ShowProcessDetails(ProcessItem item) {
        if (item == null) return;
        SetStatus("Reading process details...");
        Task.Factory.StartNew<ProcessDetails>(delegate { return GetProcessDetails(item.Pid, true); }).ContinueWith(delegate(Task<ProcessDetails> task) {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(delegate {
                ProcessDetails details = task.IsFaulted ? null : task.Result;
                if (details == null) {
                    MessageBox.Show(this, "Process details are unavailable.", "Process Details", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var text = new StringBuilder();
                text.AppendLine(details.Name + " (PID " + details.Pid + ")");
                text.AppendLine("Executable: " + (details.ExecutablePath ?? "Unavailable"));
                text.AppendLine("Account: " + (details.Account ?? "Unavailable"));
                text.AppendLine("Parent PID: " + (details.ParentPid == 0 ? "Unavailable" : details.ParentPid.ToString()));
                text.AppendLine("Architecture: " + (details.IsWow64 ? "32-bit process" : "64-bit or unavailable"));
                text.AppendLine("Command line: " + (details.CommandLine ?? "Unavailable"));
                if (item.Matches != null && item.Matches.Count > 0) {
                    text.AppendLine();
                    text.AppendLine("Matched paths:");
                    foreach (LockMatch match in item.Matches) text.AppendLine("  " + match.LockedPath + " (" + match.Source + ")");
                }
                SetStatus("Ready.");
                MessageBox.Show(this, text.ToString(), "Process Details", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }));
        });
    }

    private void StartAsyncScan(bool forceRefresh = false) {
        lock (scanLock) {
            if (isScanning) {
                rescanPending = true;
                return;
            }
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

        if (scanCancellation != null) scanCancellation.Cancel();
        scanCancellation = new CancellationTokenSource();
        CancellationToken token = scanCancellation.Token;
        Task.Factory.StartNew<ScanResult>(delegate {
            Log("Initiating scan...");
            ScanRequest request = new ScanRequest(targetsSnapshot);
            request.ForceRefresh = forceRefresh;
            request.CancellationToken = token;
            request.Deadline = TimeSpan.FromSeconds(30);
            return RunScan(request, delegate(int val) {
                if (IsDisposed || !IsHandleCreated) return;
                try {
                    BeginInvoke(new MethodInvoker(delegate {
                        if (!IsDisposed && progressBar.Value != val) progressBar.Value = Math.Min(100, Math.Max(0, val));
                    }));
                } catch { }
            });
        }, token).ContinueWith(delegate(Task<ScanResult> task) {
            if (IsDisposed || !IsHandleCreated) return;
            try {
                BeginInvoke(new MethodInvoker(delegate {
                    ScanResult scan = task.IsCanceled ? new ScanResult { Status = ScanStatus.Cancelled, ErrorMessage = "Scan was cancelled." } :
                        task.IsFaulted ? new ScanResult { Status = ScanStatus.Failed, ErrorMessage = task.Exception == null ? "Scan failed." : task.Exception.GetBaseException().Message } : task.Result;
                    lastScanResult = scan;
                    currentScanResults = scan.Processes ?? new List<ProcessItem>();
                    progressBar.Visible = false;
                    if (scan.Status == ScanStatus.Cancelled) {
                        SetStatus("Scan cancelled. Completed results are shown.", Color.FromArgb(127, 140, 141));
                    } else if (scan.Status == ScanStatus.TimedOut) {
                        SetStatus("Scan timed out. Partial results are shown.", Color.FromArgb(211, 84, 0));
                    } else if (scan.Status == ScanStatus.Partial) {
                        SetStatus("Scan partially completed. Review the available results.", Color.FromArgb(211, 84, 0));
                    } else if (scan.Status == ScanStatus.Failed) {
                        SetStatus("Scan failed: " + scan.ErrorMessage, Color.FromArgb(192, 57, 43));
                    } else if (currentScanResults.Count == 1) {
                        SetStatus("1 locking process found.", Color.FromArgb(192, 57, 43));
                    } else if (currentScanResults.Count > 1) {
                        SetStatus(string.Format("{0} locking processes found.", currentScanResults.Count), Color.FromArgb(192, 57, 43));
                    } else {
                        SetStatus("No active locks found.", Color.FromArgb(39, 174, 96));
                    }
                    ApplyFilter(txtFilter.Text.Trim());
                    bool repeat;
                    lock (scanLock) { isScanning = false; repeat = rescanPending; rescanPending = false; }
                    UpdateButtonStates();
                    if (repeat) StartAsyncScan(true);
                }));
            } catch { }
        });
    }

    private static string GetAccessInfo(ProcessItem item) {
        if (item.IsModuleLock) {
            return item.IsDir ? "Loaded Module Directory Lock" : "Active DLL / Module Lock";
        }

        uint grantedAccess = item.GrantedAccess;
        bool isDir = item.IsDir;

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

    private static string GetPrimaryLockedPath(ProcessItem item) {
        if (item != null && item.Matches != null && item.Matches.Count > 0) {
            foreach (LockMatch match in item.Matches) {
                if (match != null && !string.IsNullOrEmpty(match.LockedPath)) return match.LockedPath;
            }
        }
        return item == null ? "" : item.Path;
    }

    private static bool HasMatchingLockedPath(ProcessItem item, string filterText) {
        if (item == null || item.Matches == null) return false;
        foreach (LockMatch match in item.Matches) {
            if (match != null && match.LockedPath != null && match.LockedPath.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
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
                (x.Path != null && x.Path.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0) ||
                HasMatchingLockedPath(x, filterText)
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
                GetPrimaryLockedPath(item)
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
        } catch (ArgumentException) {
            // The process may have exited after the scan and before this call.
            return true;
        } catch (InvalidOperationException) {
            // Treat an exit race as cleared; verification follows the kill pass.
            return true;
        } catch {
            return false;
        }
    }

    private void BtnUnlock_Click(object sender, EventArgs e) {
        List<ProcessItem> selected = GetSelectedProcessItems();
        if (selected.Count == 0) return;
        bool failed = false;
        bool incompatible = false;
        foreach (ProcessItem item in selected) {
            if (item.Handles.Count == 0 || item.IsModuleLock) { incompatible = true; continue; }
            if (!UnlockSafely(item.Pid, item.Handles, item.Name)) failed = true;
        }
        if (!failed && !incompatible) {
            MessageBox.Show("Selected handle(s) successfully closed.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            StartAsyncScan(true);
        } else if (failed && !isAdmin) {
            PromptForElevation();
        } else {
            MessageBox.Show(incompatible ? "Some selected entries are executable or module locks and must be terminated instead of unlocked." : "One or more selected handles could not be closed.", "Incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            StartAsyncScan(true);
        }
    }

    private List<ProcessItem> GetSelectedProcessItems() {
        var selected = new List<ProcessItem>();
        foreach (ListViewItem row in listView.SelectedItems) {
            ProcessItem item = row.Tag as ProcessItem;
            if (item != null) selected.Add(item);
        }
        return selected;
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
        List<ProcessItem> selected = GetSelectedProcessItems();
        if (selected.Count == 0) return;
        if (MessageBox.Show("Forcibly terminate " + selected.Count + " selected process(es)? Unsaved work may be lost.", "Confirm Process Termination", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        bool failed = false;
        foreach (ProcessItem item in selected) {
            if (item.Pid == 4 || !KillProcessSafely(item.Pid, item.Name)) failed = true;
        }
        if (failed && !isAdmin) PromptForElevation();
        else if (failed) MessageBox.Show("One or more selected processes could not be terminated.", "Incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else MessageBox.Show("Selected process(es) terminated.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        StartAsyncScan(true);
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
        BeginKillDelete(DeleteMode.RecycleBin, false);
    }

    private void BtnElevate_Click(object sender, EventArgs e) {
        PromptForElevation();
    }

    internal static bool KillProcessDirect(int pid, string name) {
        if (pid == 4) return false;
        try {
            using (var p = Process.GetProcessById(pid)) {
                if (!p.HasExited) p.Kill();
                if (!p.WaitForExit(3000) && !p.HasExited) return false;
            }
            return true;
        } catch (ArgumentException) {
            // The process may have exited after the scan and before this call.
            return true;
        } catch (InvalidOperationException) {
            // Treat an exit race as cleared; verification follows the kill pass.
            return true;
        } catch {
            return false;
        }
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
        if (scanCancellation != null) scanCancellation.Cancel();
        if (operationCancellation != null) operationCancellation.Cancel();
        if (ipcTimer != null) {
            ipcTimer.Stop();
            ipcTimer.Dispose();
        }
        base.OnFormClosed(e);
    }

    internal static bool UnlockSafelyDirect(int pid, List<IntPtr> handles, string name) {
        if (handles == null || handles.Count == 0) return true;
        IntPtr hProcess = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
        if (hProcess == IntPtr.Zero) return false;

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
}
