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
    private Button btnUnlock;
    private Button btnUnlockAll;
    private Button btnKill;
    private Button btnKillAll;
    private Button btnClose;
    private Label lblTarget;
    private Label lblTitle;
    private Label lblAdminState;
    private ProgressBar progressBar;
    
    private bool isAdmin;
    private string logFile;
    private static ushort CachedFileTypeIndex = 0;

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
    }

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

        if (isAdmin) EnableDebugPrivilege();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logDir = Path.Combine(appData, "UnBlock");
        Directory.CreateDirectory(logDir);
        logFile = Path.Combine(logDir, "UnBlock.log");

        Log("======================================");
        Log("UnBlock Started (Turbo Scanner Mode)");
        Log("Target: " + targetPath);
        Log("Running as Administrator: " + isAdmin);

        InitFileTypeIndex(); // 100x Performance Boost by pre-caching handle index type

        InitializeComponent();
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
        this.Size = new Size(700, 480);
        this.MinimumSize = new Size(500, 350);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.FromArgb(245, 246, 250);
        
        try {
            IntPtr hIcon = ExtractIcon(IntPtr.Zero, "shell32.dll", 239);
            if (hIcon != IntPtr.Zero) { this.Icon = Icon.FromHandle(hIcon); }
        } catch { }

        Panel headerPanel = new Panel() {
            Dock = DockStyle.Top,
            Height = 70,
            BackColor = Color.FromArgb(30, 39, 46)
        };

        lblTarget = new Label() {
            Text = "Target: " + targetPath,
            Location = new Point(20, 15),
            Size = new Size(450, 20),
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.White,
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        lblTitle = new Label() {
            Text = "Preparing Deep Scan...",
            Location = new Point(20, 40),
            Size = new Size(450, 20),
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            ForeColor = Color.FromArgb(189, 195, 199),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        lblAdminState = new Label() {
            Text = isAdmin ? "Administrator" : "Standard User",
            Location = new Point(510, 25),
            Size = new Size(150, 20),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = isAdmin ? Color.FromArgb(46, 204, 113) : Color.FromArgb(243, 156, 18),
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        headerPanel.Controls.Add(lblTarget);
        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblAdminState);

        progressBar = new ProgressBar() {
            Location = new Point(20, 85),
            Size = new Size(640, 6),
            Style = ProgressBarStyle.Continuous,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        listView = new ListView() {
            Location = new Point(20, 100),
            Size = new Size(640, 260),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.None
        };
        listView.Columns.Add("Process Name", 180);
        listView.Columns.Add("PID", 70);
        listView.Columns.Add("Locked Path", 370);
        listView.SelectedIndexChanged += delegate { UpdateButtonStates(); };
        
        ContextMenu contextMenu = new ContextMenu();
        MenuItem openLocationItem = new MenuItem("Open Process File Location");
        openLocationItem.Click += delegate {
            if (listView.SelectedItems.Count > 0) {
                var pItem = listView.SelectedItems[0].Tag as ProcessItem;
                if (pItem != null && File.Exists(pItem.Path)) {
                    Process.Start("explorer.exe", "/select,\"" + pItem.Path + "\"");
                }
            }
        };
        contextMenu.MenuItems.Add(openLocationItem);
        listView.ContextMenu = contextMenu;

        btnUnlock = CreateStyledButton("Unlock", 20, 375, 100, Color.FromArgb(46, 204, 113), Color.White);
        btnUnlock.Click += BtnUnlock_Click;

        btnUnlockAll = CreateStyledButton("Unlock All", 130, 375, 110, Color.FromArgb(39, 174, 96), Color.White);
        btnUnlockAll.Click += BtnUnlockAll_Click;

        btnKill = CreateStyledButton("Kill Process", 250, 375, 110, Color.FromArgb(231, 76, 60), Color.White);
        btnKill.Click += BtnKill_Click;

        btnKillAll = CreateStyledButton("Kill All", 370, 375, 100, Color.FromArgb(192, 57, 43), Color.White);
        btnKillAll.Click += BtnKillAll_Click;

        btnClose = CreateStyledButton("Close", 560, 375, 100, Color.FromArgb(189, 195, 199), Color.Black);
        btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnClose.Click += delegate { this.Close(); };

        this.Controls.Add(headerPanel);
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
        bool hasItems = listView.Items.Count > 0 && (listView.Items[0].Tag is ProcessItem);
        
        btnUnlock.Enabled = hasSelection;
        btnKill.Enabled = hasSelection;
        btnUnlockAll.Enabled = hasItems;
        btnKillAll.Enabled = hasItems;
    }

    private class ProcessItem {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public List<IntPtr> Handles { get; set; }

        public ProcessItem() {
            Handles = new List<IntPtr>();
        }
    }

    private void StartAsyncScan(bool forceRefresh = false) {
        MethodInvoker initUI = delegate {
            progressBar.Value = 0;
            progressBar.Visible = true;
            lblTitle.Text = "Scanning Deep Resource Locks (Turbo Mode)...";
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
                List<ProcessItem> results = RunFastHandleScan(targetPath, forceRefresh, delegate(int val) {
                    this.BeginInvoke(new MethodInvoker(delegate {
                        if (progressBar.Value != val) progressBar.Value = Math.Min(100, Math.Max(0, val));
                    }));
                });

                this.BeginInvoke(new MethodInvoker(delegate {
                    progressBar.Visible = false;
                    lblTitle.Text = string.Format("Found {0} locked resource(s).", results.Count);
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

        if (listView.Items.Count == 0) {
            ListViewItem emptyItem = new ListViewItem(new string[] { "N/A", "N/A", "No processes are currently locking this target." });
            emptyItem.ForeColor = Color.Gray;
            listView.Items.Add(emptyItem);
        }
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

    private List<ProcessItem> RunFastHandleScan(string targetPath, bool forceRefresh, Action<int> progressCallback) {
        var finalLockingProcesses = new Dictionary<int, ProcessItem>();
        var addedPids = new HashSet<int>();

        progressCallback(5);
        RefreshProcessSnapshot(forceRefresh);
        progressCallback(10);

        string normalizedPath = targetPath;
        bool isDir = Directory.Exists(targetPath);
        if (isDir && !normalizedPath.EndsWith(Path.DirectorySeparatorChar.ToString()) && !normalizedPath.EndsWith(Path.AltDirectorySeparatorChar.ToString())) {
            normalizedPath += Path.DirectorySeparatorChar;
        }

        // Tier 1: Process Executable Paths
        lock (CacheLock) {
            foreach (KeyValuePair<int, string> kvp in ProcessPathMap) {
                int pid = kvp.Key;
                string procPath = kvp.Value;
                if (procPath != null) {
                    bool match = isDir ? 
                        (procPath.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) || procPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase)) :
                        procPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase);

                    if (match && addedPids.Add(pid)) {
                        ProcessItem pItem = new ProcessItem();
                        pItem.Pid = pid;
                        pItem.Name = GetProcessName(pid);
                        pItem.Path = procPath;
                        finalLockingProcesses[pid] = pItem;
                    }
                }
            }
        }
        progressCallback(20);

        bool isNetwork = normalizedPath.StartsWith(@"\\");
        string networkSearchPath = isNetwork ? normalizedPath.Substring(2).TrimEnd('\\', '/') : null;
        
        string driveLetter = Path.GetPathRoot(normalizedPath).TrimEnd('\\', '/');
        string targetDevicePath = normalizedPath;

        if (!isNetwork && !string.IsNullOrEmpty(driveLetter)) {
            StringBuilder sb = new StringBuilder(512);
            if (QueryDosDevice(driveLetter, sb, sb.Capacity) != 0) {
                string devicePathRoot = sb.ToString();
                targetDevicePath = normalizedPath.Replace(driveLetter, devicePathRoot);
            }
        }
        
        string devicePathWithSlash = targetDevicePath;
        if (!devicePathWithSlash.EndsWith("\\")) devicePathWithSlash += "\\";
        
        progressCallback(25);

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
            
            // Critical optimization: Only register if we don't know the file type index, or if it exactly matches our cached File Type.
            if (pid != currentPid && pid > 0 && (CachedFileTypeIndex == 0 || objTypeIndex == CachedFileTypeIndex)) {
                IntPtr handleValue = is64Bit ? Marshal.ReadIntPtr(ptr, 16) : Marshal.ReadIntPtr(ptr, 8);
                if (!handlesByPid.ContainsKey(pid)) handlesByPid[pid] = new List<HandleInfo>();
                
                HandleInfo hInfo = new HandleInfo();
                hInfo.HandleValue = handleValue;
                hInfo.ObjectTypeIndex = objTypeIndex;
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
                                string objName = GetObjectNameSafe(dupHandle); // 100ms Timeout limit
                                if (!string.IsNullOrEmpty(objName)) {
                                    
                                    bool match = false;
                                    if (isNetwork) {
                                        string normalizedObj = objName.Replace('/', '\\');
                                        if (isDir) {
                                            match = normalizedObj.EndsWith("\\" + networkSearchPath, StringComparison.OrdinalIgnoreCase) || 
                                                    normalizedObj.IndexOf("\\" + networkSearchPath + "\\", StringComparison.OrdinalIgnoreCase) >= 0;
                                        } else {
                                            match = normalizedObj.EndsWith("\\" + networkSearchPath, StringComparison.OrdinalIgnoreCase);
                                        }
                                    } else {
                                        match = isDir ? 
                                            (objName.StartsWith(devicePathWithSlash, StringComparison.OrdinalIgnoreCase) || objName.Equals(targetDevicePath, StringComparison.OrdinalIgnoreCase)) :
                                            objName.Equals(targetDevicePath, StringComparison.OrdinalIgnoreCase);
                                    }

                                    if (match) {
                                        string dosPath = targetPath;
                                        if (!isNetwork) {
                                            if (objName.StartsWith(devicePathWithSlash, StringComparison.OrdinalIgnoreCase)) {
                                                dosPath = targetPath.TrimEnd('\\', '/') + "\\" + objName.Substring(devicePathWithSlash.Length);
                                            } else if (objName.Equals(targetDevicePath, StringComparison.OrdinalIgnoreCase)) {
                                                dosPath = targetPath.TrimEnd('\\', '/');
                                            }
                                        }

                                        bool isStrictlyLocked = pathLockCache.GetOrAdd(dosPath, delegate(string p) { return IsPathStrictlyLocked(p); });
                                        if (isStrictlyLocked) {
                                            lock (lockObj) {
                                                ProcessItem item;
                                                if (!finalLockingProcesses.TryGetValue(pid, out item)) {
                                                    item = new ProcessItem();
                                                    item.Pid = pid;
                                                    item.Name = GetProcessName(pid);
                                                    item.Path = GetProcessPath(pid) ?? "Unknown System Component";
                                                    finalLockingProcesses[pid] = item;
                                                }
                                                item.Handles.Add(hInfo.HandleValue);
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
        if (task.Wait(100)) return task.Result; // Absolute protection against blocked kernel/network handles
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
            MessageBox.Show("This process is executing directly from the target folder. It cannot be unlocked, it must be terminated.", "Cannot Unlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        
        foreach (ListViewItem lvi in listView.Items) {
            var pi = lvi.Tag as ProcessItem;
            if (pi != null) {
                if (pi.Handles.Count == 0) hasProcessExecs = true;
                else if (!UnlockSafely(pi.Pid, pi.Handles, pi.Name)) failedAny = true;
            }
        }

        if (!failedAny && !hasProcessExecs) {
            MessageBox.Show("All compatible handles successfully closed.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            StartAsyncScan(true); 
        } else if (hasProcessExecs && !failedAny) {
            MessageBox.Show("Closed active handles, but some processes are executing directly from the folder and must be terminated manually.", "Partial Success", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        foreach (ListViewItem lvi in listView.Items) {
            var pi = lvi.Tag as ProcessItem;
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
                psi.Arguments = "\"" + targetPath + "\"";
                psi.Verb = "runas";
                Process.Start(psi);
                this.Close();
            } catch { }
        }
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

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 0) {
            MessageBox.Show("Please select a file or folder by right-clicking it, and selecting 'UnBlock'.", "UnBlock Unlocker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        
        Application.Run(new UnlockerForm(string.Join(" ", args)));
    }
}