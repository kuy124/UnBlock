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
    private HashSet<string> targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private List<ProcessItem> currentScanResults = new List<ProcessItem>();

    private ListView listView;
    private ImageList imageList;
    private Button btnUnlock;
    private Button btnUnlockAll;
    private Button btnKill;
    private Button btnKillAll;
    private Button btnClose;
    private Button btnAddFile;
    private Button btnAddFolder;
    private Label lblTarget;
    private Label lblTitle;
    private Label lblAdminState;
    private Label lblFilter;
    private TextBox txtFilter;
    private ProgressBar progressBar;
    private ToolTip toolTip;
    
    private bool isAdmin;
    private string logFile;
    private static ushort CachedFileTypeIndex = 0;
    private static Mutex singleInstanceMutex;
    private System.Windows.Forms.Timer ipcTimer;
    
    private bool isInitializing = true;
    private bool isScanning = false;
    private readonly object ipcLock = new object();
    private readonly object scanLock = new object();

    private enum Severity {
        Low,      // Benign / Green
        Medium,   // Active Read / Orange
        High      // Severe Write/Delete Lockout / Red
    }

    // --- Win32 Native API Declarations ---
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, ref int ReturnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr ObjectHandle, int ObjectInformationClass, IntPtr ObjectInformation, int ObjectInformationLength, ref int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    // --- Privilege Adjustment Constants ---
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID {
        public uint LowPart;
        public int HighPart;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }
    
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
    
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);
    
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint DUPLICATE_CLOSE_SOURCE = 0x00000001;
    private const uint DUPLICATE_SAME_ACCESS = 2;
    private const uint FILE_TYPE_DISK = 1;
    private const int SystemExtendedHandleInformation = 0x40;
    
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint DELETE_ACCESS = 0x00010000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
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

    private struct HandleInfo {
        public IntPtr HandleValue;
        public ushort ObjectTypeIndex;
        public uint GrantedAccess;
    }

    private class TargetMatchInfo {
        public string OriginalPath { get; set; }
        public string NormalizedPath { get; set; }
        public bool IsDir { get; set; }
        public bool IsNetwork { get; set; }
        public string networkSearchPath { get; set; }
        public string TargetDevicePath { get; set; }
        public string DevicePathWithSlash { get; set; }
    }

    private static readonly Dictionary<int, string> ProcessPathMap = new Dictionary<int, string>();
    private static readonly Dictionary<int, string> ProcessNameMap = new Dictionary<int, string>();
    private static DateTime lastSnapshotTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(8);
    private static readonly object CacheLock = new object();

    public UnlockerForm(List<string> paths) {
        isInitializing = true; // Block scanning during construction to aggregate startup paths safely

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
        SetupIpcTimer(); // Safe high-frequency direct directory sweep

        isInitializing = false; 

        UpdateTargetLabel();
        StartAsyncScan(false); // Exactly one scan triggered for all loaded items
    }

    private static void InitFileTypeIndex() {
        if (CachedFileTypeIndex != 0) return;
        
        string tempFile = Path.GetTempFileName();
        IntPtr hFile = CreateFile(tempFile, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hFile != INVALID_HANDLE_VALUE) {
            int bufferSize = 0x10000;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try {
                int length = 0;
                while (NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, ref length) == unchecked((int)0xC0000004)) {
                    bufferSize = length + 0x10000;
                    Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal(bufferSize);
                }

                bool is64Bit = Marshal.SizeOf(typeof(IntPtr)) == 8;
                long handleCount = is64Bit ? Marshal.ReadInt64(buffer) : Marshal.ReadInt32(buffer);
                IntPtr ptr = new IntPtr(buffer.ToInt64() + (is64Bit ? 16 : 8));
                int entrySize = is64Bit ? 40 : 28;
                int currentPid = Process.GetCurrentProcess().Id;

                for (long i = 0; i < handleCount; i++) {
                    int pid = is64Bit ? (int)Marshal.ReadInt64(ptr, 8) : Marshal.ReadInt32(ptr, 4);
                    IntPtr handleValue = is64Bit ? Marshal.ReadIntPtr(ptr, 16) : Marshal.ReadIntPtr(ptr, 8);
                    
                    if (pid == currentPid && handleValue == hFile) {
                        CachedFileTypeIndex = (ushort)Marshal.ReadInt16(ptr, is64Bit ? 30 : 18);
                        break;
                    }
                    ptr = new IntPtr(ptr.ToInt64() + entrySize);
                }
            } catch {
            } finally {
                Marshal.FreeHGlobal(buffer);
                CloseHandle(hFile);
                try { File.Delete(tempFile); } catch { }
            }
        }
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

    private void InitializeComponent() {
        this.Text = "UnBlock File & Folder Unlocker";
        this.Size = new Size(720, 500);
        this.MinimumSize = new Size(550, 400);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.FromArgb(240, 242, 245);
        this.toolTip = new ToolTip();
        
        try {
            IntPtr hIcon = ExtractIcon(IntPtr.Zero, "shell32.dll", 239);
            if (hIcon != IntPtr.Zero) { this.Icon = Icon.FromHandle(hIcon); }
        } catch { }

        // Layout Fix: Explicitly initialize panel width to match the form first.
        // This prevents the anchoring system from sliding controls off-screen during stretch operations.
        Panel headerPanel = new Panel() {
            Width = 720, 
            Height = 80,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(30, 39, 46)
        };

        lblTarget = new Label() {
            Location = new Point(20, 15),
            Size = new Size(310, 22), // Balanced layout allocation
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.White,
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        lblTitle = new Label() {
            Text = "Ready to scan.",
            Location = new Point(20, 42),
            Size = new Size(310, 20), // Balanced layout allocation
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            ForeColor = Color.FromArgb(189, 195, 199),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        // Layout Scaling Fix: Shifted and widened button dimensions to prevent text truncation
        btnAddFile = new Button() {
            Text = "+ File",
            Location = new Point(340, 22), 
            Size = new Size(100, 28),     // Generous spacing prevents "+ " clipping
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 152, 219),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnAddFile.FlatAppearance.BorderSize = 0;
        btnAddFile.Click += BtnAddFile_Click;
        toolTip.SetToolTip(btnAddFile, "Browse and add a file to process.");

        btnAddFolder = new Button() {
            Text = "+ Folder",
            Location = new Point(450, 22), 
            Size = new Size(110, 28),     // Generous spacing prevents "+ " clipping
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(41, 128, 185),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        btnAddFolder.FlatAppearance.BorderSize = 0;
        btnAddFolder.Click += BtnAddFolder_Click;
        toolTip.SetToolTip(btnAddFolder, "Browse and add a folder to process.");

        lblAdminState = new Label() {
            Text = isAdmin ? "🛡️ Admin" : "⚠️ Standard User",
            Location = new Point(570, 25), 
            Size = new Size(120, 22),     
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = isAdmin ? Color.FromArgb(46, 204, 113) : Color.FromArgb(243, 156, 18),
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        headerPanel.Controls.Add(lblTarget);
        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(btnAddFile);
        headerPanel.Controls.Add(btnAddFolder);
        headerPanel.Controls.Add(lblAdminState);

        lblFilter = new Label() {
            Text = "Filter results:",
            Location = new Point(20, 95),
            Size = new Size(100, 20),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };

        txtFilter = new TextBox() {
            Location = new Point(125, 94),
            Size = new Size(220, 23),
            Font = new Font("Segoe UI", 9, FontStyle.Regular)
        };
        txtFilter.TextChanged += TxtFilter_TextChanged;
        toolTip.SetToolTip(txtFilter, "Type here to dynamically filter results by Name, PID, or Path.");

        progressBar = new ProgressBar() {
            Location = new Point(20, 125),
            Size = new Size(660, 5),
            Style = ProgressBarStyle.Continuous,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        imageList = new ImageList();
        imageList.ImageSize = new Size(16, 16);
        imageList.ColorDepth = ColorDepth.Depth32Bit;

        listView = new ListView() {
            Location = new Point(20, 135),
            Size = new Size(660, 245),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle,
            SmallImageList = imageList
        };
        listView.Columns.Add("Process Name", 150);
        listView.Columns.Add("PID", 60);
        listView.Columns.Add("Access Severity", 150);
        listView.Columns.Add("Locked Path", 280);
        listView.SelectedIndexChanged += delegate { UpdateButtonStates(); };
        listView.DoubleClick += ListView_DoubleClick;
        
        ContextMenu contextMenu = new ContextMenu();
        MenuItem openLocationItem = new MenuItem("Open Process File Location");
        openLocationItem.Click += delegate {
            if (listView.SelectedItems.Count > 0) {
                var pItem = listView.SelectedItems[0].Tag as ProcessItem;
                if (pItem != null && File.Exists(pItem.Path)) {
                    try { Process.Start("explorer.exe", "/select,\"" + pItem.Path + "\""); } catch { }
                }
            }
        };
        contextMenu.MenuItems.Add(openLocationItem);
        listView.ContextMenu = contextMenu;

        btnUnlock = CreateStyledButton("Unlock Selected", 20, 395, 125, Color.FromArgb(46, 204, 113), Color.White);
        btnUnlock.Click += BtnUnlock_Click;
        toolTip.SetToolTip(btnUnlock, "Forcefully close the file handle owned by the selected process.");

        btnUnlockAll = CreateStyledButton("Unlock All", 155, 395, 100, Color.FromArgb(39, 174, 96), Color.White);
        btnUnlockAll.Click += BtnUnlockAll_Click;
        toolTip.SetToolTip(btnUnlockAll, "Close all locked active handles found in the list.");

        btnKill = CreateStyledButton("Kill Process", 265, 395, 110, Color.FromArgb(231, 76, 60), Color.White);
        btnKill.Click += BtnKill_Click;
        toolTip.SetToolTip(btnKill, "Forcibly terminate the selected locking program.");

        btnKillAll = CreateStyledButton("Kill All", 385, 395, 90, Color.FromArgb(192, 57, 43), Color.White);
        btnKillAll.Click += BtnKillAll_Click;
        toolTip.SetToolTip(btnKillAll, "Forcibly terminate all processes holding locking handles.");

        btnClose = CreateStyledButton("Close", 580, 395, 100, Color.FromArgb(149, 165, 166), Color.Black);
        btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnClose.Click += delegate { this.Close(); };

        this.Controls.Add(headerPanel);
        this.Controls.Add(lblFilter);
        this.Controls.Add(txtFilter);
        this.Controls.Add(progressBar);
        this.Controls.Add(listView);
        this.Controls.Add(btnUnlock);
        this.Controls.Add(btnUnlockAll);
        this.Controls.Add(btnKill);
        this.Controls.Add(btnKillAll);
        this.Controls.Add(btnClose);

        UpdateButtonStates();
    }

    private Button CreateStyledButton(string text, int x, int y, int width, Color backColor, Color foreColor) {
        Button btn = new Button() {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = foreColor,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    private void UpdateButtonStates() {
        bool hasSelection = listView.SelectedItems.Count > 0 && listView.SelectedItems[0].Tag is ProcessItem;
        bool hasItems = currentScanResults.Count > 0;
        
        btnUnlock.Enabled = hasSelection;
        btnKill.Enabled = hasSelection;
        btnUnlockAll.Enabled = hasItems;
        btnKillAll.Enabled = hasItems;
    }

    private void UpdateTargetLabel() {
        if (targetPaths.Count == 0) {
            lblTarget.Text = "Target: [No files/folders selected]";
            lblTitle.Text = "Awaiting manual selection. Use '+ File' or '+ Folder' above.";
        } else if (targetPaths.Count == 1) {
            string singlePath = "";
            foreach (var p in targetPaths) { singlePath = p; break; }
            lblTarget.Text = "Target: " + singlePath;
        } else {
            lblTarget.Text = string.Format("Target: [Multiple Items] ({0} files/folders loaded)", targetPaths.Count);
        }
    }

    private class ProcessItem {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public uint GrantedAccess { get; set; }
        public bool IsDir { get; set; }
        public List<IntPtr> Handles { get; set; }

        public ProcessItem() {
            Handles = new List<IntPtr>();
        }
    }

    private void SetupIpcTimer() {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pendingDir = Path.Combine(appData, "UnBlock\\Pending");
        try {
            Directory.CreateDirectory(pendingDir);
        } catch { }

        ipcTimer = new System.Windows.Forms.Timer();
        ipcTimer.Interval = 50; // Check LocalAppData directory sweep every 50ms (0% CPU impact, bulletproof accuracy)
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
                    StartAsyncScan(true);
                }
            }
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
                StartAsyncScan(true);
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
                StartAsyncScan(true);
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
            if (isScanning) return; // Prevent concurrent scans from overlapping
            isScanning = true;
        }

        HashSet<string> targetsSnapshot;
        lock (ipcLock) {
            if (targetPaths.Count == 0) {
                // Safeguard Fix: InvokeUI updates safely without invoking BeginInvoke inside constructor execution
                MethodInvoker updateEmptyUI = delegate {
                    progressBar.Visible = false;
                    currentScanResults.Clear();
                    listView.Items.Clear();
                    ListViewItem emptyItem = new ListViewItem(new string[] { "N/A", "N/A", "", "Click '+ File' or '+ Folder' to analyze lock states." });
                    emptyItem.ForeColor = Color.Gray;
                    listView.Items.Add(emptyItem);
                    UpdateButtonStates();
                    lock (scanLock) { isScanning = false; }
                };

                if (this.InvokeRequired) this.BeginInvoke(updateEmptyUI);
                else updateEmptyUI();

                return;
            }
            // Snapshot copy prevents collection modification issues during background tasks
            targetsSnapshot = new HashSet<string>(targetPaths, StringComparer.OrdinalIgnoreCase);
        }

        MethodInvoker initUI = delegate {
            progressBar.Value = 0;
            progressBar.Visible = true;
            lblTitle.Text = "Scanning Resource Locks (Turbo Mode)...";
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
                    lblTitle.Text = string.Format("Found {0} locked resource(s).", results.Count);
                    currentScanResults = results;
                    ApplyFilter(txtFilter.Text.Trim());
                    lock (scanLock) { isScanning = false; }
                }));
            } catch (Exception ex) {
                Log("Scan error: " + ex.Message);
                this.BeginInvoke(new MethodInvoker(delegate {
                    progressBar.Visible = false;
                    MessageBox.Show("Scan error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    lock (scanLock) { isScanning = false; }
                }));
            }
        });
    }

    private static string GetAccessInfo(uint grantedAccess, bool isDir) {
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
        return Severity.High; // Write, delete, exclusive, full control, modify
    }

    private void ApplyFilter(string filterText) {
        listView.BeginUpdate();
        listView.Items.Clear();
        imageList.Images.Clear();

        Icon defaultIcon = null;
        try {
            IntPtr hIcon = ExtractIcon(IntPtr.Zero, "shell32.dll", 2); // Fallback generic program icon
            if (hIcon != IntPtr.Zero) defaultIcon = Icon.FromHandle(hIcon);
        } catch { }

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
            Icon procIcon = defaultIcon;
            if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path)) {
                try { procIcon = Icon.ExtractAssociatedIcon(item.Path); } catch { }
            }

            if (procIcon != null) imageList.Images.Add(procIcon);
            else {
                Bitmap bmp = new Bitmap(16, 16);
                imageList.Images.Add(bmp);
            }

            string accessInfo = GetAccessInfo(item.GrantedAccess, item.IsDir);
            Severity severity = GetSeverity(accessInfo);

            ListViewItem lvi = new ListViewItem(new string[] { 
                item.Name, 
                item.Pid.ToString(), 
                accessInfo, 
                item.Path 
            });
            lvi.ImageIndex = iconIndex;
            lvi.Tag = item;

            // Apply color coding styling directly based on Severity classifications
            lvi.UseItemStyleForSubItems = false;
            if (severity == Severity.High) {
                // High Severity - Severe Lockout (Crimson Red)
                lvi.ForeColor = Color.Black;
                lvi.SubItems[0].ForeColor = Color.FromArgb(44, 62, 80); // Dark slate
                lvi.SubItems[1].ForeColor = Color.FromArgb(44, 62, 80);
                lvi.SubItems[2].ForeColor = Color.FromArgb(192, 57, 43); // Bold Alizarin Red
                lvi.SubItems[2].Font = new Font(listView.Font, FontStyle.Bold);
                lvi.SubItems[3].ForeColor = Color.FromArgb(44, 62, 80);
            } else if (severity == Severity.Medium) {
                // Medium Severity - Active Reader (Pumpkin Orange)
                lvi.ForeColor = Color.Black;
                lvi.SubItems[0].ForeColor = Color.FromArgb(44, 62, 80);
                lvi.SubItems[1].ForeColor = Color.FromArgb(44, 62, 80);
                lvi.SubItems[2].ForeColor = Color.FromArgb(211, 84, 0); // Bold Pumpkin Orange
                lvi.SubItems[2].Font = new Font(listView.Font, FontStyle.Bold);
                lvi.SubItems[3].ForeColor = Color.FromArgb(44, 62, 80);
            } else {
                // Low Severity - Benign Usage (Muted Gray & Emerald Green)
                // Fades benign rows to immediately highlight severe blockers
                lvi.ForeColor = Color.Gray;
                lvi.SubItems[0].ForeColor = Color.Gray;
                lvi.SubItems[1].ForeColor = Color.Gray;
                lvi.SubItems[2].ForeColor = Color.FromArgb(39, 174, 96); // Soft Emerald Green
                lvi.SubItems[2].Font = new Font(listView.Font, FontStyle.Bold);
                lvi.SubItems[3].ForeColor = Color.Gray;
            }

            listView.Items.Add(lvi);
            iconIndex++;
        }

        if (listView.Items.Count == 0) {
            string msg = (currentScanResults.Count == 0) ? "No active locking processes found on target(s)." : "No search matches found.";
            ListViewItem emptyItem = new ListViewItem(new string[] { "N/A", "N/A", "", msg });
            emptyItem.ForeColor = Color.Gray;
            listView.Items.Add(emptyItem);
        }

        listView.EndUpdate();
        UpdateButtonStates();
    }

    private static bool IsPathStrictlyLocked(string path) {
        uint shareMode = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
        IntPtr handle = CreateFile(path, DELETE_ACCESS | GENERIC_WRITE, shareMode, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle != INVALID_HANDLE_VALUE) {
            CloseHandle(handle);
            return false;
        }

        int err = Marshal.GetLastWin32Error();
        if (err == 32 || err == 33) return true; // SHARING_VIOLATION or LOCK_VIOLATION

        if (err == 5) { // ACCESS_DENIED fallback check
            handle = CreateFile(path, DELETE_ACCESS, shareMode, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle != INVALID_HANDLE_VALUE) {
                CloseHandle(handle);
                return false; 
            }
            err = Marshal.GetLastWin32Error();
            if (err == 32 || err == 33) return true;
        }
        return false;
    }

    private List<ProcessItem> RunFastHandleScan(HashSet<string> targets, bool forceRefresh, Action<int> progressCallback) {
        var finalLockingProcesses = new Dictionary<int, ProcessItem>();
        var addedPids = new HashSet<int>();

        progressCallback(5);
        RefreshProcessSnapshot(forceRefresh);
        progressCallback(10);

        var targetList = new List<TargetMatchInfo>();
        foreach (string rawTarget in targets) {
            try {
                if (string.IsNullOrEmpty(rawTarget)) continue;
                string target = rawTarget;
                bool isDir = Directory.Exists(target);
                if (isDir && !target.EndsWith(Path.DirectorySeparatorChar.ToString()) && !target.EndsWith(Path.AltDirectorySeparatorChar.ToString())) {
                    target += Path.DirectorySeparatorChar;
                }

                bool isNetwork = target.StartsWith(@"\\");
                string networkSearchPath = isNetwork ? target.Substring(2).TrimEnd('\\', '/') : null;
                string driveLetter = Path.GetPathRoot(target).TrimEnd('\\', '/');
                string targetDevicePath = target;

                if (!isNetwork && !string.IsNullOrEmpty(driveLetter)) {
                    StringBuilder sb = new StringBuilder(512);
                    if (QueryDosDevice(driveLetter, sb, sb.Capacity) != 0) {
                        string devicePathRoot = sb.ToString();
                        targetDevicePath = target.Replace(driveLetter, devicePathRoot);
                    }
                }
                
                string devicePathWithSlash = targetDevicePath;
                if (!devicePathWithSlash.EndsWith("\\")) devicePathWithSlash += "\\";

                targetList.Add(new TargetMatchInfo {
                    OriginalPath = rawTarget,
                    NormalizedPath = target,
                    IsDir = isDir,
                    IsNetwork = isNetwork,
                    networkSearchPath = networkSearchPath,
                    TargetDevicePath = targetDevicePath,
                    DevicePathWithSlash = devicePathWithSlash
                });
            } catch { }
        }

        if (targetList.Count == 0) return new List<ProcessItem>();

        // Tier 1: Process Executable Paths
        lock (CacheLock) {
            foreach (KeyValuePair<int, string> kvp in ProcessPathMap) {
                int pid = kvp.Key;
                string procPath = kvp.Value;
                if (procPath != null) {
                    foreach (var info in targetList) {
                        bool match = info.IsDir ? 
                            (procPath.StartsWith(info.NormalizedPath, StringComparison.OrdinalIgnoreCase) || procPath.Equals(info.OriginalPath, StringComparison.OrdinalIgnoreCase)) :
                            procPath.Equals(info.OriginalPath, StringComparison.OrdinalIgnoreCase);

                        if (match && addedPids.Add(pid)) {
                            ProcessItem pItem = new ProcessItem {
                                Pid = pid,
                                Name = GetProcessName(pid),
                                Path = procPath,
                                GrantedAccess = 0x0012019f, // Active Executable Lock
                                IsDir = info.IsDir
                            };
                            finalLockingProcesses[pid] = pItem;
                            break;
                        }
                    }
                }
            }
        }
        progressCallback(20);

        // Tier 2: System Handles Map
        int bufferSize = 0x10000;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        int length = 0;
        int status;

        while ((status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, ref length)) == unchecked((int)0xC0000004)) {
            bufferSize = length + 0x10000; 
            Marshal.FreeHGlobal(buffer);
            buffer = Marshal.AllocHGlobal(bufferSize);
        }

        if (status != 0) {
            Marshal.FreeHGlobal(buffer);
            return new List<ProcessItem>(finalLockingProcesses.Values);
        }

        progressCallback(40);

        bool is64Bit = Marshal.SizeOf(typeof(IntPtr)) == 8;
        long handleCount = is64Bit ? Marshal.ReadInt64(buffer) : Marshal.ReadInt32(buffer);
        IntPtr ptr = new IntPtr(buffer.ToInt64() + (is64Bit ? 16 : 8));
        int entrySize = is64Bit ? 40 : 28;

        var handlesByPid = new Dictionary<int, List<HandleInfo>>();
        int currentPid = Process.GetCurrentProcess().Id;

        for (long i = 0; i < handleCount; i++) {
            int pid = is64Bit ? (int)Marshal.ReadInt64(ptr, 8) : Marshal.ReadInt32(ptr, 4);
            ushort objTypeIndex = (ushort)Marshal.ReadInt16(ptr, is64Bit ? 30 : 18);
            uint grantedAccess = (uint)Marshal.ReadInt32(ptr, is64Bit ? 24 : 12);
            
            if (pid != currentPid && pid > 0 && (CachedFileTypeIndex == 0 || objTypeIndex == CachedFileTypeIndex)) {
                IntPtr handleValue = is64Bit ? Marshal.ReadIntPtr(ptr, 16) : Marshal.ReadIntPtr(ptr, 8);
                if (!handlesByPid.ContainsKey(pid)) handlesByPid[pid] = new List<HandleInfo>();
                
                HandleInfo hInfo = new HandleInfo {
                    HandleValue = handleValue,
                    ObjectTypeIndex = objTypeIndex,
                    GrantedAccess = grantedAccess
                };
                handlesByPid[pid].Add(hInfo);
            }
            ptr = new IntPtr(ptr.ToInt64() + entrySize);
        }

        Marshal.FreeHGlobal(buffer);
        progressCallback(50);

        int processed = 0;
        int total = handlesByPid.Count;
        IntPtr currentProcessHandle = GetCurrentProcess();
        object lockObj = new object();
        ConcurrentDictionary<string, bool> pathLockCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(handlesByPid, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, delegate(KeyValuePair<int, List<HandleInfo>> kvp) {
            int pid = kvp.Key;
            Interlocked.Increment(ref processed);
            if (processed % 10 == 0) progressCallback(50 + (int)((processed / (float)total) * 45));

            IntPtr hProcess = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
            if (hProcess == IntPtr.Zero) return;

            try {
                foreach (var hInfo in kvp.Value) {
                    IntPtr dupHandle = IntPtr.Zero;
                    if (DuplicateHandle(hProcess, hInfo.HandleValue, currentProcessHandle, out dupHandle, 0, false, DUPLICATE_SAME_ACCESS)) {
                        try {
                            if (GetFileType(dupHandle) == FILE_TYPE_DISK) {
                                string objName = GetObjectNameSafe(dupHandle); 
                                if (!string.IsNullOrEmpty(objName)) {
                                    
                                    foreach (var info in targetList) {
                                        bool match = false;
                                        if (info.IsNetwork) {
                                            string normalizedObj = objName.Replace('/', '\\');
                                            if (info.IsDir) {
                                                match = normalizedObj.EndsWith("\\" + info.networkSearchPath, StringComparison.OrdinalIgnoreCase) || 
                                                        normalizedObj.IndexOf("\\" + info.networkSearchPath + "\\", StringComparison.OrdinalIgnoreCase) >= 0;
                                            } else {
                                                match = normalizedObj.EndsWith("\\" + info.networkSearchPath, StringComparison.OrdinalIgnoreCase);
                                            }
                                        } else {
                                            match = info.IsDir ? 
                                                (objName.StartsWith(info.DevicePathWithSlash, StringComparison.OrdinalIgnoreCase) || objName.Equals(info.TargetDevicePath, StringComparison.OrdinalIgnoreCase)) :
                                                objName.Equals(info.TargetDevicePath, StringComparison.OrdinalIgnoreCase);
                                        }

                                        if (match) {
                                            string dosPath = info.OriginalPath;
                                            if (!info.IsNetwork) {
                                                if (objName.StartsWith(info.DevicePathWithSlash, StringComparison.OrdinalIgnoreCase)) {
                                                    dosPath = info.OriginalPath.TrimEnd('\\', '/') + "\\" + objName.Substring(info.DevicePathWithSlash.Length);
                                                } else if (objName.Equals(info.TargetDevicePath, StringComparison.OrdinalIgnoreCase)) {
                                                    dosPath = info.OriginalPath.TrimEnd('\\', '/');
                                                }
                                            }

                                            bool isStrictlyLocked = pathLockCache.GetOrAdd(dosPath, delegate(string p) { return IsPathStrictlyLocked(p); });
                                            if (isStrictlyLocked) {
                                                lock (lockObj) {
                                                    ProcessItem item;
                                                    if (!finalLockingProcesses.TryGetValue(pid, out item)) {
                                                        item = new ProcessItem {
                                                            Pid = pid,
                                                            Name = GetProcessName(pid),
                                                            Path = GetProcessPath(pid) ?? "Unknown System Component",
                                                            GrantedAccess = hInfo.GrantedAccess,
                                                            IsDir = info.IsDir
                                                        };
                                                        finalLockingProcesses[pid] = item;
                                                    } else {
                                                        if (hInfo.GrantedAccess > item.GrantedAccess) {
                                                            item.GrantedAccess = hInfo.GrantedAccess;
                                                        }
                                                    }
                                                    item.Handles.Add(hInfo.HandleValue);
                                                }
                                                break; 
                                            }
                                        }
                                    }
                                }
                            }
                        } finally {
                            CloseHandle(dupHandle);
                        }
                    }
                }
            } catch {
            } finally {
                CloseHandle(hProcess);
            }
        });

        progressCallback(100);
        return new List<ProcessItem>(finalLockingProcesses.Values);
    }

    private static string GetObjectNameSafe(IntPtr handle) {
        var task = Task.Factory.StartNew(delegate { return GetObjectNameInternal(handle); });
        if (task.Wait(100)) return task.Result; 
        return null; 
    }

    private static string GetObjectNameInternal(IntPtr handle) {
        int length = 2048;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try {
            int status = NtQueryObject(handle, 1, buffer, length, ref length); // 1 = ObjectNameInformation
            if (status == unchecked((int)0xC0000004) || status == unchecked((int)0x80000005)) { 
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(length);
                status = NtQueryObject(handle, 1, buffer, length, ref length);
            }
            if (status >= 0) {
                int nameLength = Marshal.ReadInt16(buffer);
                if (nameLength > 0) {
                    IntPtr namePtr = Marshal.SizeOf(typeof(IntPtr)) == 8 ? Marshal.ReadIntPtr(buffer, 8) : Marshal.ReadIntPtr(buffer, 4);
                    if (namePtr != IntPtr.Zero) return Marshal.PtrToStringUni(namePtr, nameLength / 2);
                }
            }
        } catch {
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
        return null;
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
                        if (fullPath != null) ProcessPathMap[pid] = fullPath;
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
        if (pid == 4) return "NTAUTHORITY\\SYSTEM";
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess != IntPtr.Zero) {
            try {
                int size = 1024;
                StringBuilder sb = new StringBuilder(size);
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size)) return sb.ToString();
            } finally {
                CloseHandle(hProcess);
            }
        }
        return null;
    }

    private static string GetProcessPath(int pid) {
        if (pid == 4) return "NTAUTHORITY\\SYSTEM";
        lock (CacheLock) {
            string cachedPath;
            if (ProcessPathMap.TryGetValue(pid, out cachedPath)) return cachedPath;
        }
        return QueryProcessPathDirect(pid);
    }

    private static string GetProcessName(int pid) {
        if (pid == 4) return "System (Kernel)";
        lock (CacheLock) {
            string name;
            if (ProcessNameMap.TryGetValue(pid, out name)) return name;
            return "Unknown";
        }
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

    private void BtnUnlock_Click(object sender, EventArgs e) {
        if (listView.SelectedItems.Count == 0) return;
        var item = listView.SelectedItems[0].Tag as ProcessItem;
        if (item == null) return;

        if (item.Handles.Count == 0) {
            MessageBox.Show("This process is executing directly from the target folder. It cannot be unlocked; it must be terminated.", "Cannot Unlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                if (pi.Handles.Count == 0) hasProcessExecs = true;
                else if (!UnlockSafely(pi.Pid, pi.Handles, pi.Name)) failedAny = true;
            }
        }

        if (!failedAny && !hasProcessExecs) {
            MessageBox.Show("All compatible handles successfully closed.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            StartAsyncScan(true); 
        } else if (hasProcessExecs && !failedAny) {
            MessageBox.Show("Closed active handles, but some processes are executing directly from a target folder and must be terminated manually.", "Partial Success", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private void PromptForElevation() {
        if (MessageBox.Show("Administrative privileges are highly recommended to modify this process.\n\nRestart UnBlock as Administrator?", "Elevation Recommended", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
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
            } catch { }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        if (ipcTimer != null) {
            ipcTimer.Stop();
            ipcTimer.Dispose();
        }
        base.OnFormClosed(e);
    }

    private static void RunWatcherMode(string targetDir) {
        string targetExe = Path.Combine(targetDir, "Unlocker.exe");
        bool keysCurrentlyRegistered = true; // Assume they exist when PC boots
        
        while (true) {
            Thread.Sleep(2000); // Check every 2 seconds
            
            bool isAppAvailable = File.Exists(targetExe);
            
            if (isAppAvailable && !keysCurrentlyRegistered) {
                // The drive was plugged back in! Restore the right-click menus.
                RestoreRegistryKeys(targetExe);
                keysCurrentlyRegistered = true;
            } 
            else if (!isAppAvailable && keysCurrentlyRegistered) {
                // The drive is unplugged. Hide the right-click menus temporarily.
                CleanRegistryOnly();
                keysCurrentlyRegistered = false;
            }
        }
    }

    private static void CleanRegistryOnly() {
        try {
            using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)) {
                // Remove right-click menus temporarily
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\*\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\Directory\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\Directory\Background\shell\UnBlock", false);
                
                // Note: We deliberately LEAVE the "Uninstall" key alone so you can 
                // still officially uninstall it from Windows Settings if you want to!
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
            }
        } catch { }
    }

    [STAThread]
    public static void Main(string[] args) {
        if (args.Length == 1 && args[0] == "[WARMUP]") {
            try { 
                InitFileTypeIndex();
                RefreshProcessSnapshot(true); 
            } catch {}
            return;
        }

        // --- DYNAMIC BACKGROUND WATCHER BOOTSTRAP ---
        if (args.Length >= 2 && args[0] == "[WATCHER]") {
            RunWatcherMode(args[1]);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Crisp programmatic rendering on high-res / scaled layouts
        try {
            if (Environment.OSVersion.Version.Major >= 6) {
                SetProcessDPIAware();
            }
        } catch { }

        // Determine if another instance is already running
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pendingDir = Path.Combine(appData, "UnBlock\\Pending");
        Directory.CreateDirectory(pendingDir);

        bool createdNew = true;
        try {
            singleInstanceMutex = new Mutex(true, "Global\\UnBlock_SingleInstance_Mutex", out createdNew);
        } catch (UnauthorizedAccessException) {
            try {
                // Defensive Fallback Fix: Creates a Local Session Mutex if user has standard non-administrator privileges
                singleInstanceMutex = new Mutex(true, "Local\\UnBlock_SingleInstance_Mutex", out createdNew);
            } catch {
                createdNew = true;
            }
        } catch {
            createdNew = true;
        }

        if (!createdNew) {
            // Another instance is running. Pass our target paths and terminate.
            if (args.Length > 0) {
                try {
                    string tempBase = Path.Combine(pendingDir, Guid.NewGuid().ToString());
                    string tempWritePath = tempBase + ".tmp_write";
                    string tempFinalPath = tempBase + ".tmp";
                    
                    // Atomic write-and-rename operation prevents target file lockouts
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