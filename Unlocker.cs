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
using System.Reflection;

public class UnlockerForm : Form {
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
    private static Mutex singleInstanceMutex;
    private System.Windows.Forms.Timer ipcTimer;
    
    private bool isInitializing = true;
    private bool isScanning = false;
    private bool rescanPending = false;
    private readonly object ipcLock = new object();
    private readonly object scanLock = new object();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, string lParam);

    private const int EM_SETCUEBANNER = 0x1501;

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

    [DllImport("kernel32.dll", EntryPoint = "Module32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", EntryPoint = "Module32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    [DllImport("psapi.dll", EntryPoint = "GetMappedFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMappedFileName(IntPtr hProcess, IntPtr lpv, StringBuilder lpFilename, int nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DeleteFile(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool RemoveDirectory(string lpPathName);

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
    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;
    
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
        this.Size = new Size(830, 560);
        this.MinimumSize = new Size(790, 480);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.FromArgb(244, 246, 248);
        this.toolTip = new ToolTip();

        try {
            IntPtr hIcon = ExtractIcon(IntPtr.Zero, "shell32.dll", 239);
            if (hIcon != IntPtr.Zero) { this.Icon = Icon.FromHandle(hIcon); }
        } catch { }

        // ---------- Header ----------
        Panel headerPanel = new Panel() {
            Dock = DockStyle.Top,
            Height = 66,
            Width = 744,
            BackColor = Color.FromArgb(30, 39, 46)
        };

        Label lblAppTitle = new Label() {
            Text = "UnBlock",
            Location = new Point(16, 8),
            Size = new Size(220, 26),
            Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
            ForeColor = Color.White
        };

        lblTarget = new Label() {
            Location = new Point(18, 37),
            Size = new Size(400, 20),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = Color.FromArgb(189, 195, 199),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        FlowLayoutPanel headerButtons = new FlowLayoutPanel() {
            Dock = DockStyle.Right,
            WrapContents = false,
            BackColor = Color.FromArgb(30, 39, 46),
            Padding = new Padding(0, 15, 14, 0)
        };

        btnAddFile = MakeHeaderButton("+ File", Color.FromArgb(52, 152, 219));
        btnAddFile.Click += BtnAddFile_Click;
        toolTip.SetToolTip(btnAddFile, "Browse and add a file to analyze.");

        btnAddFolder = MakeHeaderButton("+ Folder", Color.FromArgb(41, 128, 185));
        btnAddFolder.Click += BtnAddFolder_Click;
        toolTip.SetToolTip(btnAddFolder, "Browse and add a folder to analyze.");

        headerButtons.Controls.Add(btnAddFile);
        headerButtons.Controls.Add(btnAddFolder);

        if (!isAdmin) {
            btnElevate = MakeHeaderButton("\uD83D\uDEE1 Elevate", Color.FromArgb(241, 196, 15));
            btnElevate.ForeColor = Color.Black;
            btnElevate.Click += BtnElevate_Click;
            toolTip.SetToolTip(btnElevate, "Restart UnBlock as Administrator to enable complete security adjustments.");
            headerButtons.Controls.Add(btnElevate);
        }

        int headerFlowWidth = headerButtons.Padding.Horizontal;
        foreach (Control c in headerButtons.Controls) headerFlowWidth += c.Width + c.Margin.Horizontal;
        headerButtons.Width = headerFlowWidth + 2;

        headerPanel.Controls.Add(lblAppTitle);
        headerPanel.Controls.Add(lblTarget);
        headerPanel.Controls.Add(headerButtons);

        // ---------- Toolbar (search + status + admin badge) ----------
        Panel toolbarPanel = new Panel() {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Color.White
        };
        Panel toolbarBorder = new Panel() { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(227, 231, 234) };

        Panel filterHost = new Panel() { Dock = DockStyle.Left, Width = 276, BackColor = Color.White };
        txtFilter = new TextBox() {
            Location = new Point(16, 12),
            Size = new Size(250, 25),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };
        txtFilter.TextChanged += TxtFilter_TextChanged;
        toolTip.SetToolTip(txtFilter, "Filter results by process name, PID, or path.");
        filterHost.Controls.Add(txtFilter);

        lblStatus = new Label() {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = Color.DimGray,
            Text = "Ready."
        };
        Panel statusHost = new Panel() { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 8, 0) };
        statusHost.Controls.Add(lblStatus);

        lblAdminState = new Label() {
            Text = isAdmin ? "\uD83D\uDEE1 Administrator" : "\u26A0 Standard user",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = isAdmin ? Color.FromArgb(39, 174, 96) : Color.FromArgb(211, 84, 0),
            TextAlign = ContentAlignment.MiddleRight
        };
        FlowLayoutPanel adminHost = new FlowLayoutPanel() {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Color.White,
            Padding = new Padding(0, 14, 16, 0)
        };
        adminHost.Controls.Add(lblAdminState);

        toolbarPanel.Controls.Add(statusHost);
        toolbarPanel.Controls.Add(adminHost);
        toolbarPanel.Controls.Add(filterHost);
        toolbarPanel.Controls.Add(toolbarBorder);

        // ---------- Progress strip ----------
        progressBar = new ProgressBar() {
            Dock = DockStyle.Top,
            Height = 4,
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };

        // ---------- Action bar ----------
        Panel actionBar = new Panel() {
            Dock = DockStyle.Bottom,
            Height = 62,
            BackColor = Color.White
        };
        Panel actionBarBorder = new Panel() { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(227, 231, 234) };

        FlowLayoutPanel actionFlow = new FlowLayoutPanel() {
            Dock = DockStyle.Fill,
            WrapContents = false,
            BackColor = Color.White,
            Padding = new Padding(12, 12, 0, 0)
        };

        btnUnlock = MakeActionButton("Unlock Selected", Color.FromArgb(39, 174, 96), Color.White);
        btnUnlock.Click += BtnUnlock_Click;
        toolTip.SetToolTip(btnUnlock, "Close the file handle(s) held by the selected process.");

        btnUnlockAll = MakeActionButton("Unlock All", Color.FromArgb(30, 132, 73), Color.White);
        btnUnlockAll.Click += BtnUnlockAll_Click;
        toolTip.SetToolTip(btnUnlockAll, "Close every locking handle found on the target(s).");

        btnKill = MakeActionButton("Kill Process", Color.FromArgb(192, 57, 43), Color.White);
        btnKill.Click += BtnKill_Click;
        toolTip.SetToolTip(btnKill, "Forcibly terminate the selected locking program.");

        btnKillAll = MakeActionButton("Kill All", Color.FromArgb(146, 43, 33), Color.White);
        btnKillAll.Click += BtnKillAll_Click;
        toolTip.SetToolTip(btnKillAll, "Forcibly terminate ALL processes shown in the list.");

        btnForceDelete = MakeActionButton("Force Delete", Color.FromArgb(202, 111, 30), Color.White);
        btnForceDelete.Click += BtnForceDelete_Click;
        toolTip.SetToolTip(btnForceDelete, "Delete the target(s): kills locks, resets ACLs, falls back to delete-on-reboot.");

        actionFlow.Controls.Add(btnUnlock);
        actionFlow.Controls.Add(btnUnlockAll);
        actionFlow.Controls.Add(MakeActionSeparator());
        actionFlow.Controls.Add(btnKill);
        actionFlow.Controls.Add(btnKillAll);
        actionFlow.Controls.Add(MakeActionSeparator());
        actionFlow.Controls.Add(btnForceDelete);

        Panel closeHost = new Panel() {
            Dock = DockStyle.Right,
            Width = 96,
            BackColor = Color.White,
            Padding = new Padding(0, 12, 10, 12)
        };
        btnClose = MakeActionButton("Close", Color.FromArgb(149, 165, 166), Color.Black);
        btnClose.Dock = DockStyle.Fill;
        btnClose.Click += delegate { this.Close(); };
        closeHost.Controls.Add(btnClose);

        actionBar.Controls.Add(actionFlow);
        actionBar.Controls.Add(closeHost);
        actionBar.Controls.Add(actionBarBorder);

        // ---------- Results list ----------
        Panel listHost = new Panel() {
            Dock = DockStyle.Fill,
            BackColor = this.BackColor,
            Padding = new Padding(12, 10, 12, 10)
        };

        imageList = new ImageList();
        imageList.ImageSize = new Size(16, 16);
        imageList.ColorDepth = ColorDepth.Depth32Bit;

        listView = new ListView() {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            GridLines = false,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            BorderStyle = BorderStyle.FixedSingle,
            SmallImageList = imageList
        };
        try {
            typeof(ListView).InvokeMember("DoubleBuffered",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                null, listView, new object[] { true });
        } catch { }

        listView.Columns.Add("Process", 175);
        listView.Columns.Add("PID", 55);
        listView.Columns.Add("Severity", 135);
        listView.Columns.Add("Process Path", 300);
        listView.SelectedIndexChanged += delegate { UpdateButtonStates(); };
        listView.ClientSizeChanged += delegate { FillLastColumn(); };
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

        listHost.Controls.Add(listView);

        // ---------- Assemble (reverse docking order: last added is laid out first) ----------
        this.Controls.Add(listHost);
        this.Controls.Add(progressBar);
        this.Controls.Add(toolbarPanel);
        this.Controls.Add(actionBar);
        this.Controls.Add(headerPanel);

        this.CancelButton = btnClose;

        try {
            SendMessage(txtFilter.Handle, EM_SETCUEBANNER, 1, "Search process, PID or path...");
        } catch { }

        UpdateButtonStates();
    }

    private Size MeasureButtonText(Button btn, string text, int paddingX, int height) {
        int textWidth = 60;
        try {
            using (Graphics g = btn.CreateGraphics()) {
                SizeF ts = g.MeasureString(text, btn.Font);
                textWidth = (int)Math.Ceiling(ts.Width);
            }
        } catch { }
        return new Size(textWidth + paddingX, height);
    }

    private Button MakeHeaderButton(string text, Color backColor) {
        Button btn = new Button() {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0)
        };
        btn.Size = MeasureButtonText(btn, text, 26, 34);
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    private Button MakeActionButton(string text, Color backColor, Color foreColor) {
        Button btn = new Button() {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = foreColor,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 6, 0)
        };
        btn.Size = MeasureButtonText(btn, text, 30, 40);
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(backColor, 0.05f);
        btn.EnabledChanged += delegate {
            btn.BackColor = btn.Enabled ? backColor : Color.FromArgb(189, 195, 199);
            btn.ForeColor = btn.Enabled ? foreColor : Color.FromArgb(120, 125, 130);
        };
        return btn;
    }

    private Panel MakeActionSeparator() {
        return new Panel() {
            Width = 1,
            Height = 28,
            BackColor = Color.FromArgb(208, 213, 217),
            Margin = new Padding(7, 6, 7, 0)
        };
    }

    private void FillLastColumn() {
        if (listView.Columns.Count < 4 || listView.Width == 0) return;
        int others = 0;
        for (int i = 0; i < listView.Columns.Count - 1; i++) others += listView.Columns[i].Width;
        int remaining = listView.ClientSize.Width - others - SystemInformation.VerticalScrollBarWidth;
        if (remaining > 80) listView.Columns[listView.Columns.Count - 1].Width = remaining;
    }

    private void SetStatus(string text) {
        SetStatus(text, Color.DimGray);
    }

    private void SetStatus(string text, Color color) {
        if (lblStatus == null) return;
        lblStatus.Text = text;
        lblStatus.ForeColor = color;
    }

    private void ShowListMessage(string message) {
        listView.BeginUpdate();
        listView.Items.Clear();
        ListViewItem emptyItem = new ListViewItem(new string[] { "", "", "", message });
        emptyItem.ForeColor = Color.Gray;
        listView.Items.Add(emptyItem);
        listView.EndUpdate();
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

    private static bool MatchesDosPath(string candidatePath, TargetMatchInfo info) {
        if (info.IsDir) {
            return candidatePath.StartsWith(info.NormalizedPath, StringComparison.OrdinalIgnoreCase) ||
                   candidatePath.Equals(info.OriginalPath, StringComparison.OrdinalIgnoreCase);
        }
        return candidatePath.Equals(info.OriginalPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDeviceOrDosPath(string candidatePath, TargetMatchInfo info) {
        if (candidatePath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) {
            return candidatePath.StartsWith(info.DevicePathWithSlash, StringComparison.OrdinalIgnoreCase) ||
                   candidatePath.Equals(info.TargetDevicePath, StringComparison.OrdinalIgnoreCase);
        }
        return MatchesDosPath(candidatePath, info);
    }

    private static int FindNetworkTailIndex(string normalizedObjName) {
        int idx = normalizedObjName.IndexOf("\\mup\\", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return idx + 5;

        idx = normalizedObjName.IndexOf("\\lanmanredirector\\", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) {
            idx += 18;
            if (idx < normalizedObjName.Length && normalizedObjName[idx] == ';') {
                int slash = normalizedObjName.IndexOf('\\', idx);
                if (slash < 0) return -1;
                idx = slash + 1;
            }
            return idx;
        }
        return -1;
    }

    private static bool IsPathStrictlyLocked(string path) {
        uint shareMode = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
        IntPtr handle = CreateFile(path, DELETE_ACCESS | GENERIC_WRITE, shareMode, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle != INVALID_HANDLE_VALUE) {
            CloseHandle(handle);
            return false;
        }

        int err = Marshal.GetLastWin32Error();
        if (err == 32 || err == 33) return true; 

        if (err == 5) { 
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

    private static List<string> GetProcessModules(int pid) {
        var modules = new List<string>();
        IntPtr hSnap = INVALID_HANDLE_VALUE;
        
        // 1. Toolhelp Module Resolver
        for (int i = 0; i < 3; i++) {
            hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
            if (hSnap != INVALID_HANDLE_VALUE) break;
            
            int err = Marshal.GetLastWin32Error();
            if (err != 0x1A8) // ERROR_BAD_LENGTH
                break;
            
            Thread.Sleep(5);
        }

        if (hSnap != INVALID_HANDLE_VALUE) {
            try {
                MODULEENTRY32 modEntry = new MODULEENTRY32();
                modEntry.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32));
                if (Module32First(hSnap, ref modEntry)) {
                    do {
                        if (!string.IsNullOrEmpty(modEntry.szExePath)) {
                            modules.Add(modEntry.szExePath);
                        }
                    } while (Module32Next(hSnap, ref modEntry));
                }
            } catch {
            } finally {
                CloseHandle(hSnap);
            }

            if (modules.Count > 0) return modules;
        }

        // Direct Address Space Walk + GetMappedFileName (only when the Toolhelp snapshot failed,
        // e.g. protected processes; walking every region of every process is far too slow otherwise)
        IntPtr hProcess = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (hProcess == IntPtr.Zero) {
            hProcess = OpenProcess(0x0400, false, pid); // Fallback to PROCESS_QUERY_INFORMATION
        }

        if (hProcess != IntPtr.Zero) {
            try {
                long address = 0;
                long maxAddress = IntPtr.Size == 8 ? 0x7FFFFFFFFFFFFFFF : 0x7FFFFFFF;
                MEMORY_BASIC_INFORMATION mbi = new MEMORY_BASIC_INFORMATION();
                IntPtr mbiSize = (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
                StringBuilder pathBuilder = new StringBuilder(1024);

                while (address < maxAddress) {
                    IntPtr result = VirtualQueryEx(hProcess, (IntPtr)address, out mbi, mbiSize);
                    if (result == IntPtr.Zero || (long)result == 0) {
                        break;
                    }

                    if (mbi.State == 0x1000 && (mbi.Type == 0x1000000 || mbi.Type == 0x40000)) {
                        int len = GetMappedFileName(hProcess, mbi.BaseAddress, pathBuilder, pathBuilder.Capacity);
                        if (len > 0) {
                            string mappedPath = pathBuilder.ToString();
                            if (!string.IsNullOrEmpty(mappedPath)) {
                                modules.Add(mappedPath);
                            }
                        }
                    }

                    long nextAddress = (long)mbi.BaseAddress + (long)mbi.RegionSize;
                    if (nextAddress <= address) break; 
                    address = nextAddress;
                }
            } catch {
            } finally {
                CloseHandle(hProcess);
            }
        }

        return modules;
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
                    TargetDevicePath = targetDevicePath.TrimEnd('\\', '/'),
                    DevicePathWithSlash = devicePathWithSlash
                });
            } catch { }
        }

        if (targetList.Count == 0) return new List<ProcessItem>();

        var pathLockCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool allTargetsAreFiles = true;

        foreach (var info in targetList) {
            if (!info.IsDir) {
                string probeKey = info.OriginalPath.TrimEnd('\\', '/');
                pathLockCache[probeKey] = IsPathStrictlyLocked(probeKey);
            } else {
                allTargetsAreFiles = false;
            }
        }

        // Tier 1: Process Executable Paths
        lock (CacheLock) {
            foreach (KeyValuePair<int, string> kvp in ProcessPathMap) {
                int pid = kvp.Key;
                string procPath = kvp.Value;
                if (procPath != null) {
                    foreach (var info in targetList) {
                        if (MatchesDosPath(procPath, info) && addedPids.Add(pid)) {
                            ProcessItem pItem = new ProcessItem {
                                Pid = pid,
                                Name = GetProcessName(pid),
                                Path = procPath,
                                GrantedAccess = 0x0012019f, 
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

        // Tier 2: Process Loaded Modules (DLLs & Mapped Sections)
        List<int> activePids;
        lock (CacheLock) {
            activePids = new List<int>(ProcessNameMap.Keys);
        }

        int currentPid = Process.GetCurrentProcess().Id;
        object lockObj = new object();

        Parallel.ForEach(activePids, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, delegate(int pid) {
            if (pid <= 4 || pid == currentPid) return;

            List<string> modules = GetProcessModules(pid);
            if (modules.Count > 0) {
                foreach (string modPath in modules) {
                    if (string.IsNullOrEmpty(modPath)) continue;

                    bool matched = false;
                    foreach (var info in targetList) {
                        if (MatchesDeviceOrDosPath(modPath, info)) {
                            matched = true;
                            lock (lockObj) {
                                ProcessItem pItem;
                                if (!finalLockingProcesses.TryGetValue(pid, out pItem)) {
                                    pItem = new ProcessItem {
                                        Pid = pid,
                                        Name = GetProcessName(pid),
                                        Path = GetProcessPath(pid) ?? "Unknown System Component",
                                        GrantedAccess = 0,
                                        IsDir = info.IsDir,
                                        IsModuleLock = true
                                    };
                                    finalLockingProcesses[pid] = pItem;
                                } else {
                                    pItem.IsModuleLock = true;
                                }
                            }
                            break;
                        }
                    }
                    if (matched) break;
                }
            }
        });
        progressCallback(45);

        // Tier 3: System Handles Map
        bool anyTargetStrictlyLocked = false;
        foreach (KeyValuePair<string, bool> probe in pathLockCache) {
            if (probe.Value) {
                anyTargetStrictlyLocked = true;
                break;
            }
        }

        if (allTargetsAreFiles && !anyTargetStrictlyLocked) {
            Log("Fast-skip: no target file is strictly locked; skipping system handle table scan.");
            progressCallback(100);
            return new List<ProcessItem>(finalLockingProcesses.Values);
        }

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

        progressCallback(55);

        bool is64Bit = Marshal.SizeOf(typeof(IntPtr)) == 8;
        long handleCount = is64Bit ? Marshal.ReadInt64(buffer) : Marshal.ReadInt32(buffer);
        IntPtr ptr = new IntPtr(buffer.ToInt64() + (is64Bit ? 16 : 8));
        int entrySize = is64Bit ? 40 : 28;

        HashSet<int> livePids;
        lock (CacheLock) {
            livePids = new HashSet<int>(ProcessNameMap.Keys);
        }

        var handlesByPid = new Dictionary<int, List<HandleInfo>>();

        for (long i = 0; i < handleCount; i++) {
            int pid = is64Bit ? (int)Marshal.ReadInt64(ptr, 8) : Marshal.ReadInt32(ptr, 4);
            ushort objTypeIndex = (ushort)Marshal.ReadInt16(ptr, is64Bit ? 30 : 18);
            uint grantedAccess = (uint)Marshal.ReadInt32(ptr, is64Bit ? 24 : 12);
            
            if (pid != currentPid && pid > 0 && livePids.Contains(pid) && (CachedFileTypeIndex == 0 || objTypeIndex == CachedFileTypeIndex)) {
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
        progressCallback(65);

        var scanQueue = new ConcurrentQueue<KeyValuePair<int, HandleInfo>>();
        foreach (KeyValuePair<int, List<HandleInfo>> kvp in handlesByPid) {
            foreach (HandleInfo hInfo in kvp.Value) {
                scanQueue.Enqueue(new KeyValuePair<int, HandleInfo>(kvp.Key, hInfo));
            }
        }

        int total = scanQueue.Count;
        int processed = 0;
        IntPtr currentProcessHandle = GetCurrentProcess();
        bool timeUp = false;

        Action<KeyValuePair<int, HandleInfo>> processHandle = delegate(KeyValuePair<int, HandleInfo> pair) {
            int pid = pair.Key;
            HandleInfo hInfo = pair.Value;

            IntPtr hProcess = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
            if (hProcess == IntPtr.Zero) return;

            try {
                IntPtr dupHandle = IntPtr.Zero;
                if (DuplicateHandle(hProcess, hInfo.HandleValue, currentProcessHandle, out dupHandle, 0, false, DUPLICATE_SAME_ACCESS)) {
                    try {
                        if (GetFileType(dupHandle) == FILE_TYPE_DISK) {
                            string objName = GetObjectNameInternal(dupHandle); 
                            if (!string.IsNullOrEmpty(objName)) {
                                
                                foreach (var info in targetList) {
                                    bool match = false;
                                    string relSuffix = null;

                                    if (info.IsNetwork) {
                                        string normalizedObj = objName.Replace('/', '\\');
                                        int tailIdx = FindNetworkTailIndex(normalizedObj);
                                        if (tailIdx >= 0) {
                                            string tail = normalizedObj.Substring(tailIdx).TrimEnd('\\', '/');
                                            if (info.IsDir) {
                                                match = tail.Equals(info.networkSearchPath, StringComparison.OrdinalIgnoreCase) ||
                                                        tail.StartsWith(info.networkSearchPath + "\\", StringComparison.OrdinalIgnoreCase);
                                            } else {
                                                match = tail.Equals(info.networkSearchPath, StringComparison.OrdinalIgnoreCase);
                                            }
                                            if (match && tail.Length > info.networkSearchPath.Length) {
                                                relSuffix = tail.Substring(info.networkSearchPath.Length).TrimStart('\\', '/');
                                            }
                                        }
                                    } else if (objName.StartsWith(info.DevicePathWithSlash, StringComparison.OrdinalIgnoreCase)) {
                                        match = true;
                                        relSuffix = objName.Substring(info.DevicePathWithSlash.Length).TrimStart('\\', '/');
                                    } else if (objName.Equals(info.TargetDevicePath, StringComparison.OrdinalIgnoreCase)) {
                                        match = true;
                                        relSuffix = null;
                                    }

                                    if (!match) continue;

                                    string baseDosPath = info.OriginalPath.TrimEnd('\\', '/');
                                    string dosPath = string.IsNullOrEmpty(relSuffix) ? baseDosPath : baseDosPath + "\\" + relSuffix;

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
                    } finally {
                        CloseHandle(dupHandle);
                    }
                }
            } catch {
            } finally {
                CloseHandle(hProcess);
            }
        };

        int workerCount = Math.Max(16, Math.Min(64, Environment.ProcessorCount * 4));
        var workers = new List<Thread>();
        for (int w = 0; w < workerCount; w++) {
            var worker = new Thread(delegate() {
                KeyValuePair<int, HandleInfo> pair;
                while (!timeUp && scanQueue.TryDequeue(out pair)) {
                    try { processHandle(pair); } catch { }
                    int done = Interlocked.Increment(ref processed);
                    if (done % 25 == 0) progressCallback(total > 0 ? 65 + (int)((done / (float)total) * 30) : 65);
                }
            });
            worker.IsBackground = true;
            workers.Add(worker);
            worker.Start();
        }

        DateTime handleScanStart = DateTime.UtcNow;
        foreach (Thread worker in workers) {
            int remainMs = 20000 - (int)(DateTime.UtcNow - handleScanStart).TotalMilliseconds;
            if (remainMs <= 0 || !worker.Join(remainMs)) { timeUp = true; break; }
        }
        timeUp = true;

        progressCallback(100);
        return new List<ProcessItem>(finalLockingProcesses.Values);
    }

    private static string GetObjectNameInternal(IntPtr handle) {
        int bufferSize = 2048;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try {
            int length = bufferSize;
            int status = NtQueryObject(handle, 1, buffer, bufferSize, ref length); 
            if (status == unchecked((int)0xC0000004) || status == unchecked((int)0x80000005)) { 
                Marshal.FreeHGlobal(buffer);
                bufferSize = length > 0 ? length : bufferSize * 2;
                buffer = Marshal.AllocHGlobal(bufferSize);
                length = bufferSize;
                status = NtQueryObject(handle, 1, buffer, bufferSize, ref length);
            }
            if (status >= 0) {
                bool is64 = Marshal.SizeOf(typeof(IntPtr)) == 8;
                int headerSize = is64 ? 16 : 8;
                int nameLength = Marshal.ReadInt16(buffer, 0);
                if (nameLength > 0 && nameLength <= bufferSize - headerSize) {
                    return Marshal.PtrToStringUni(new IntPtr(buffer.ToInt64() + headerSize), nameLength / 2);
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
            sbConfirm.AppendLine("• " + p);
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

    private static void RunWatcherMode(string targetDir) {
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

    private static void RunInteractiveUninstall(bool silent) {
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
                "•  Right-click context menu entries\n" +
                "•  Background maintenance task\n" +
                "•  All leftover files and folders\n\n" +
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
        try {
            string localAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnBlock");
            DeleteDirectoryWithRetry(localAppDataDir);
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

    private static void RunCleanupHelper(string[] args) {
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

        // --- NATIVE UNINSTALLER (NO CONSOLE) ---
        bool silentUninstall = false;
        if (args.Length >= 1 && args[0] == "[UNINSTALL]") {
            foreach (string a in args) {
                if (string.Equals(a, "/SILENT", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase)) silentUninstall = true;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            RunInteractiveUninstall(silentUninstall);
            return;
        }

        // --- INTERNAL POST-EXIT CLEANUP HELPER ---
        if (args.Length >= 2 && args[0] == "[CLEANUP]") {
            RunCleanupHelper(args);
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
                RunInteractiveUninstall(silent);
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

// ============================================================================
// Top-Level Helper Classes, Structs, and Enums (File Scope)
// Placed outside the Form class to ensure complete compiler type visibility.
// ============================================================================

public enum Severity {
    Low,      // Benign / Green
    Medium,   // Active Read / Orange
    High      // Severe Write/Delete Lockout / Red
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct PROCESSENTRY32 {
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

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MODULEENTRY32 {
    public uint dwSize;
    public uint th32ModuleID;
    public uint th32ProcessID;
    public uint GlblcntUsage;
    public uint ProccntUsage;
    public IntPtr modBaseAddr;
    public uint modBaseSize;
    public IntPtr hModule;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szModule;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szExePath;
}

[StructLayout(LayoutKind.Sequential)]
public struct MEMORY_BASIC_INFORMATION {
    public IntPtr BaseAddress;
    public IntPtr AllocationBase;
    public uint AllocationProtect;
    public IntPtr RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}

public struct HandleInfo {
    public IntPtr HandleValue;
    public ushort ObjectTypeIndex;
    public uint GrantedAccess;
}

public class TargetMatchInfo {
    public string OriginalPath { get; set; }
    public string NormalizedPath { get; set; }
    public bool IsDir { get; set; }
    public bool IsNetwork { get; set; }
    public string networkSearchPath { get; set; }
    public string TargetDevicePath { get; set; }
    public string DevicePathWithSlash { get; set; }
}

public class ProcessItem {
    public int Pid { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public uint GrantedAccess { get; set; }
    public bool IsDir { get; set; }
    public List<IntPtr> Handles { get; set; }
    public bool IsModuleLock { get; set; }

    public ProcessItem() {
        Handles = new List<IntPtr>();
    }
}