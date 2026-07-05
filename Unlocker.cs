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
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint DUPLICATE_SAME_ACCESS = 2;
    private const uint FILE_TYPE_DISK = 1;
    private const int SystemExtendedHandleInformation = 0x40;
    
    // CreateFile Constants
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

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logDir = Path.Combine(appData, "UnBlock");
        Directory.CreateDirectory(logDir);
        logFile = Path.Combine(logDir, "UnBlock.log");

        Log("======================================");
        Log("UnBlock Started (Strict Lock Mode)");
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
            Text = "Scanning Valid Resource Locks...",
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
            lblTitle.Text = "Scanning Valid Resource Locks (Instant Mode)...";
        };

        if (this.InvokeRequired) {
            this.BeginInvoke(initUI);
        } else {
            initUI();
        }

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

        Log("Populated UI with " + items.Count + " true locking processes.");
        if (listView.Items.Count == 0) {
            ListViewItem emptyItem = new ListViewItem(new string[] { "N/A", "N/A", "No locking processes found." });
            listView.Items.Add(emptyItem);
            btnKill.Enabled = false;
            btnKillAll.Enabled = false;
        } else {
            btnKillAll.Enabled = true;
        }
    }

    // --- Strict Validity Checker ---
    // Tests if the discovered handle actually prevents Deleting or Modifying the file/folder.
    // Skips false positives like 'explorer.exe' which often hold non-blocking passive handles.
    private static bool IsPathStrictlyLocked(string path) {
        uint shareMode = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
        
        // Test 1: Try acquiring Write and Delete permissions. If it succeeds, the handle isn't locking us out.
        IntPtr handle = CreateFile(path, DELETE_ACCESS | GENERIC_WRITE, shareMode, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle != INVALID_HANDLE_VALUE) {
            CloseHandle(handle);
            return false;
        }

        int err = Marshal.GetLastWin32Error();
        if (err == 32 || err == 33) return true; // 32 = ERROR_SHARING_VIOLATION. It's truly locked!

        // Test 2: If Access Denied (Read-Only file), retry with just Delete access.
        if (err == 5) {
            handle = CreateFile(path, DELETE_ACCESS, shareMode, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle != INVALID_HANDLE_VALUE) {
                CloseHandle(handle);
                return false; 
            }
            err = Marshal.GetLastWin32Error();
            if (err == 32 || err == 33) return true;
        }

        // If we can't confirm a sharing violation, assume it's safe to ignore (false positive)
        return false;
    }

    // High-speed Win32 Memory Lock Scanner 
    private List<ProcessItem> RunFastHandleScan(string targetPath, bool forceRefresh, Action<int> progressCallback) {
        var finalLockingProcesses = new List<ProcessItem>();
        var addedPids = new HashSet<int>();

        progressCallback(5);
        RefreshProcessSnapshot(forceRefresh);
        progressCallback(10);

        string normalizedPath = targetPath;
        bool isDir = Directory.Exists(targetPath);
        if (isDir && !normalizedPath.EndsWith(Path.DirectorySeparatorChar.ToString()) && !normalizedPath.EndsWith(Path.AltDirectorySeparatorChar.ToString())) {
            normalizedPath += Path.DirectorySeparatorChar;
        }

        // Tier 1: Process Executable Paths (If a program inside the folder is running, it natively blocks deletion)
        lock (CacheLock) {
            foreach (KeyValuePair<int, string> kvp in ProcessPathMap) {
                int pid = kvp.Key;
                string procPath = kvp.Value;
                if (procPath != null) {
                    bool match = isDir ? 
                        (procPath.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) || procPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase)) :
                        procPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase);

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

        // Tier 2: Instant Global Handle Scan
        string driveLetter = Path.GetPathRoot(normalizedPath).TrimEnd('\\', '/');
        string targetDevicePath = normalizedPath;

        if (!string.IsNullOrEmpty(driveLetter)) {
            StringBuilder sb = new StringBuilder(512);
            if (QueryDosDevice(driveLetter, sb, sb.Capacity) != 0) {
                string devicePathRoot = sb.ToString();
                targetDevicePath = normalizedPath.Replace(driveLetter, devicePathRoot);
            }
        }
        
        string devicePathWithSlash = targetDevicePath;
        if (!devicePathWithSlash.EndsWith("\\")) devicePathWithSlash += "\\";
        
        progressCallback(25);

        // 1. Snapshot all system handles
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
            return finalLockingProcesses;
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
            IntPtr handleValue = is64Bit ? Marshal.ReadIntPtr(ptr, 16) : Marshal.ReadIntPtr(ptr, 8);
            ushort objTypeIndex = (ushort)Marshal.ReadInt16(ptr, is64Bit ? 30 : 18);

            if (pid != currentPid && pid > 4) { // Filter self & SYSTEM
                if (!handlesByPid.ContainsKey(pid)) handlesByPid[pid] = new List<HandleInfo>();
                handlesByPid[pid].Add(new HandleInfo { HandleValue = handleValue, ObjectTypeIndex = objTypeIndex });
            }
            ptr = new IntPtr(ptr.ToInt64() + entrySize);
        }

        Marshal.FreeHGlobal(buffer);
        progressCallback(50);

        // 2. Discover and Validate handles in parallel
        int processed = 0;
        int total = handlesByPid.Count;
        IntPtr currentProcessHandle = GetCurrentProcess();
        
        ushort expectedFileTypeIndex = 0;
        object lockObj = new object();
        
        // Cache validations to prevent re-testing the same file for 50 open handles
        ConcurrentDictionary<string, bool> pathLockCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(handlesByPid, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, kvp => {
            int pid = kvp.Key;
            Interlocked.Increment(ref processed);
            
            if (processed % 10 == 0) {
                progressCallback(50 + (int)((processed / (float)total) * 45));
            }

            bool skip = false;
            lock (lockObj) {
                skip = addedPids.Contains(pid);
            }
            if (skip) return;

            IntPtr hProcess = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
            if (hProcess == IntPtr.Zero) return;

            try {
                foreach (var hInfo in kvp.Value) {
                    ushort currentFileIndex;
                    lock (lockObj) { currentFileIndex = expectedFileTypeIndex; }

                    if (currentFileIndex != 0 && hInfo.ObjectTypeIndex != currentFileIndex) continue;

                    IntPtr dupHandle = IntPtr.Zero;
                    if (DuplicateHandle(hProcess, hInfo.HandleValue, currentProcessHandle, out dupHandle, 0, false, DUPLICATE_SAME_ACCESS)) {
                        try {
                            if (currentFileIndex == 0) {
                                if (GetObjectTypeName(dupHandle) == "File") {
                                    lock (lockObj) {
                                        if (expectedFileTypeIndex == 0) expectedFileTypeIndex = hInfo.ObjectTypeIndex;
                                    }
                                } else {
                                    continue;
                                }
                            }

                            if (GetFileType(dupHandle) == FILE_TYPE_DISK) {
                                string objName = GetObjectName(dupHandle);
                                if (!string.IsNullOrEmpty(objName)) {
                                    
                                    bool match = isDir ? 
                                        (objName.StartsWith(devicePathWithSlash, StringComparison.OrdinalIgnoreCase) || objName.Equals(targetDevicePath, StringComparison.OrdinalIgnoreCase)) :
                                        objName.Equals(targetDevicePath, StringComparison.OrdinalIgnoreCase);

                                    if (match) {
                                        // 3. Resolve exact DOS path for the match
                                        string dosPath = targetPath;
                                        if (objName.StartsWith(devicePathWithSlash, StringComparison.OrdinalIgnoreCase)) {
                                            dosPath = targetPath.TrimEnd('\\', '/') + "\\" + objName.Substring(devicePathWithSlash.Length);
                                        } else if (objName.Equals(targetDevicePath, StringComparison.OrdinalIgnoreCase)) {
                                            dosPath = targetPath.TrimEnd('\\', '/');
                                        }

                                        // 4. STRICT VALIDATION: Make sure it's a real lock!
                                        bool isStrictlyLocked = pathLockCache.GetOrAdd(dosPath, p => IsPathStrictlyLocked(p));

                                        if (isStrictlyLocked) {
                                            lock (lockObj) {
                                                if (addedPids.Add(pid)) {
                                                    finalLockingProcesses.Add(new ProcessItem {
                                                        Pid = pid,
                                                        Name = GetProcessName(pid),
                                                        Path = GetProcessPath(pid) ?? "Unknown (Protected/Elevated)"
                                                    });
                                                }
                                            }
                                            break; // Found one valid lock for this PID, skip the rest of its handles
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
        return finalLockingProcesses;
    }

    private static string GetObjectName(IntPtr handle) {
        int length = 1024;
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
                    if (namePtr != IntPtr.Zero) {
                        return Marshal.PtrToStringUni(namePtr, nameLength / 2);
                    }
                }
            }
        } catch {
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
        return null;
    }

    private static string GetObjectTypeName(IntPtr handle) {
        int length = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try {
            int status = NtQueryObject(handle, 2, buffer, length, ref length); // 2 = ObjectTypeInformation
            if (status == unchecked((int)0xC0000004) || status == unchecked((int)0x80000005)) {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(length);
                status = NtQueryObject(handle, 2, buffer, length, ref length);
            }
            if (status >= 0) {
                int nameLength = Marshal.ReadInt16(buffer);
                if (nameLength > 0) {
                    IntPtr namePtr = Marshal.SizeOf(typeof(IntPtr)) == 8 ? Marshal.ReadIntPtr(buffer, 8) : Marshal.ReadIntPtr(buffer, 4);
                    if (namePtr != IntPtr.Zero) {
                        return Marshal.PtrToStringUni(namePtr, nameLength / 2);
                    }
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
                RefreshProcessSnapshot(true);
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