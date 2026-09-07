using System;
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

// Watcher maintenance mode, native uninstaller UI, active-tab File-in-Use handler, and cleanup helper.
internal static class Uninstaller {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [ComImport]
    [Guid("6d5140c1-7436-11ce-8034-00aa006009fa")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeServiceProvider {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport]
    [Guid("00000114-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint GW_OWNER = 4;
    private const uint GA_ROOTOWNER = 3;
    private const uint GA_ROOT = 2;
    private const int SW_HIDE = 0;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    private static readonly Dictionary<IntPtr, DateTime> handledDialogs = new Dictionary<IntPtr, DateTime>();
    private static readonly object handledLock = new object();
    private static volatile bool isPromptShowing = false;

    private class ExplorerTabCandidate {
        public object Window;
        public string FolderPath;
        public string LocationName;
        public List<string> SelectedItems;
        public int Score;

        public ExplorerTabCandidate() {
            SelectedItems = new List<string>();
            Score = 0;
        }
    }

    internal static void RunWatcherMode(string targetDir) {
        string targetExe = Path.Combine(targetDir, "Unlocker.exe");

        Thread dialogMonitorThread = new Thread(RunExplorerDialogMonitor);
        dialogMonitorThread.IsBackground = true;
        dialogMonitorThread.SetApartmentState(ApartmentState.STA);
        dialogMonitorThread.Start();

        while (true) {
            Thread.Sleep(1500);

            bool isAppAvailable = File.Exists(targetExe);
            bool keysCurrentlyRegistered = AreContextKeysRegistered();

            if (isAppAvailable && !keysCurrentlyRegistered) {
                RestoreRegistryKeys(targetExe);
            }
            else if (!isAppAvailable && keysCurrentlyRegistered) {
                PerformUninstallSteps(false);
                SpawnCleanupHelper(Process.GetCurrentProcess().Id,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnBlock"));
                Environment.Exit(0);
            }
        }
    }

    private static void RunExplorerDialogMonitor() {
        while (true) {
            try {
                EnumWindows(CheckWindowForFileInUse, IntPtr.Zero);
            } catch { }
            Thread.Sleep(50);
        }
    }

    private static bool CheckWindowForFileInUse(IntPtr hWnd, IntPtr lParam) {
        if (!IsWindow(hWnd) || !IsWindowVisible(hWnd)) return true;

        StringBuilder sbClass = new StringBuilder(64);
        GetClassName(hWnd, sbClass, sbClass.Capacity);
        string className = sbClass.ToString();
        if (!className.Equals("#32770") && !className.Equals("OperationStatusWindow")) return true;

        uint pid;
        GetWindowThreadProcessId(hWnd, out pid);
        if (pid == 0) return true;

        string procName = "";
        try {
            using (var p = Process.GetProcessById((int)pid)) {
                procName = p.ProcessName;
            }
        } catch { return true; }

        if (!procName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return true;

        int titleLen = GetWindowTextLength(hWnd);
        StringBuilder sbTitle = new StringBuilder(titleLen + 1);
        if (titleLen > 0) GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
        string title = sbTitle.ToString();

        bool isFileInUseTitle = title.IndexOf("File in Use", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                title.IndexOf("Folder in Use", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                title.IndexOf("Item in Use", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                title.IndexOf("File Access Denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                title.IndexOf("Folder Access Denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                title.IndexOf("Error Deleting", StringComparison.OrdinalIgnoreCase) >= 0;

        List<string> childTexts = new List<string>();
        EnumChildWindows(hWnd, (childHwnd, l) => {
            int len = GetWindowTextLength(childHwnd);
            if (len > 0) {
                StringBuilder sbChild = new StringBuilder(len + 1);
                GetWindowText(childHwnd, sbChild, sbChild.Capacity);
                string t = sbChild.ToString().Trim();
                if (!string.IsNullOrEmpty(t) && !childTexts.Contains(t)) childTexts.Add(t);
            }
            return true;
        }, IntPtr.Zero);

        bool hasInUseText = false;
        foreach (string text in childTexts) {
            if (text.IndexOf("The action can't be completed because", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("is open in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("being used by another", StringComparison.OrdinalIgnoreCase) >= 0) {
                hasInUseText = true;
                break;
            }
        }

        if (!isFileInUseTitle && !hasInUseText) return true;

        lock (handledLock) {
            DateTime now = DateTime.UtcNow;
            List<IntPtr> deadHandles = new List<IntPtr>();
            foreach (var kvp in handledDialogs) {
                if (!IsWindow(kvp.Key) || (now - kvp.Value).TotalSeconds > 2) deadHandles.Add(kvp.Key);
            }
            foreach (var dead in deadHandles) handledDialogs.Remove(dead);

            if (isPromptShowing) return true;
            if (handledDialogs.ContainsKey(hWnd)) return true;
            handledDialogs[hWnd] = now;
        }

        // Do not hide the native dialog yet: only hide once candidate paths are confirmed
        Thread uiThread = new Thread(() => ShowIntegratedPrompt(hWnd, childTexts));
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.IsBackground = true;
        uiThread.Start();

        return true;
    }

    private static void ShowIntegratedPrompt(IntPtr explorerDialogHwnd, List<string> childTexts) {
        try {
            isPromptShowing = true;

            List<string> candidatePaths = GetCandidatePaths(explorerDialogHwnd, childTexts);
            if (candidatePaths.Count == 0) {
                // If no candidates could be matched, leave native dialog visible so it never vanishes
                return;
            }

            // Candidates confirmed: hide native Explorer dialog
            ShowWindow(explorerDialogHwnd, SW_HIDE);

            IntegratedPromptForm.UserChoice rememberedChoice = IntegratedPromptForm.UserChoice.None;

            for (int i = 0; i < candidatePaths.Count; i++) {
                string targetPath = candidatePaths[i];
                if (!File.Exists(targetPath) && !Directory.Exists(targetPath)) continue;

                IntegratedPromptForm.UserChoice choice = rememberedChoice;
                List<ProcessItem> lockingProcesses = new List<ProcessItem>();

                if (choice == IntegratedPromptForm.UserChoice.None) {
                    using (var form = new IntegratedPromptForm(targetPath, i + 1, candidatePaths.Count)) {
                        ThreadPool.QueueUserWorkItem(s => {
                            try {
                                UnlockerForm.InitFileTypeIndex();
                                var targetSet = new HashSet<string>(new[] { targetPath }, StringComparer.OrdinalIgnoreCase);
                                lockingProcesses = UnlockerForm.RunFastHandleScanDirect(targetSet);
                                form.UpdateLockDetails(lockingProcesses);
                            } catch { }
                        });

                        form.ShowDialog();
                        choice = form.SelectedChoice;
                        if (form.ApplyToAll) {
                            rememberedChoice = choice;
                        }
                    }
                }

                if (choice == IntegratedPromptForm.UserChoice.Cancel) {
                    break;
                }

                if (choice == IntegratedPromptForm.UserChoice.Skip) {
                    continue;
                }

                if (choice == IntegratedPromptForm.UserChoice.KillAndDelete) {
                    if (lockingProcesses.Count == 0) {
                        lockingProcesses = UnlockerForm.RunFastHandleScanDirect(new HashSet<string>(new[] { targetPath }, StringComparer.OrdinalIgnoreCase));
                    }

                    foreach (var proc in lockingProcesses) {
                        if (proc != null && proc.Pid != 4) UnlockerForm.KillProcessDirect(proc.Pid, proc.Name);
                    }

                    try { File.SetAttributes(targetPath, FileAttributes.Normal); } catch { }
                    UnlockerForm.ResetFilePermissionsDirect(targetPath);

                    int win32Err;
                    string errorMsg;
                    if (!UnlockerForm.AttemptDeleteDirect(targetPath, out win32Err, out errorMsg)) {
                        MoveFileEx(targetPath, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    }
                } else if (choice == IntegratedPromptForm.UserChoice.UnlockAndDelete) {
                    if (lockingProcesses.Count == 0) {
                        lockingProcesses = UnlockerForm.RunFastHandleScanDirect(new HashSet<string>(new[] { targetPath }, StringComparer.OrdinalIgnoreCase));
                    }

                    foreach (var proc in lockingProcesses) {
                        if (proc != null) UnlockerForm.UnlockSafelyDirect(proc.Pid, proc.Handles, proc.Name);
                    }

                    try { File.SetAttributes(targetPath, FileAttributes.Normal); } catch { }
                    UnlockerForm.ResetFilePermissionsDirect(targetPath);

                    int win32Err;
                    string errorMsg;
                    if (!UnlockerForm.AttemptDeleteDirect(targetPath, out win32Err, out errorMsg)) {
                        MoveFileEx(targetPath, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    }
                }
            }

            SendMessage(explorerDialogHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            PostMessage(explorerDialogHwnd, WM_COMMAND, (IntPtr)2, IntPtr.Zero);
        } finally {
            isPromptShowing = false;
        }
    }

    private static IntPtr GetExplorerWindowForDialog(IntPtr dialogHwnd) {
        IntPtr current = dialogHwnd;
        for (int i = 0; i < 6 && current != IntPtr.Zero; i++) {
            StringBuilder sb = new StringBuilder(64);
            GetClassName(current, sb, sb.Capacity);
            if (sb.ToString().Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
                sb.ToString().Equals("ExploreWClass", StringComparison.OrdinalIgnoreCase)) {
                return current;
            }
            IntPtr next = GetWindow(current, GW_OWNER);
            if (next == IntPtr.Zero) next = GetAncestor(current, GA_ROOTOWNER);
            if (next == IntPtr.Zero) next = GetAncestor(current, GA_ROOT);
            if (next == current) break;
            current = next;
        }

        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) {
            StringBuilder sb = new StringBuilder(64);
            GetClassName(fg, sb, sb.Capacity);
            if (sb.ToString().Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase)) {
                return fg;
            }
        }

        uint dialogPid = 0;
        GetWindowThreadProcessId(dialogHwnd, out dialogPid);

        IntPtr bestCabinet = IntPtr.Zero;
        EnumWindows((w, l) => {
            if (!IsWindowVisible(w)) return true;
            StringBuilder sb = new StringBuilder(64);
            GetClassName(w, sb, sb.Capacity);
            if (sb.ToString().Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase)) {
                uint p;
                GetWindowThreadProcessId(w, out p);
                if (dialogPid == 0 || p == dialogPid) {
                    bestCabinet = w;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        return bestCabinet;
    }

    private static string GetExplorerActiveFolderPath(IntPtr cabinetHwnd) {
        if (cabinetHwnd == IntPtr.Zero) return null;
        string activePath = null;

        // Reads the address bar directly from the active tab in Windows Explorer
        EnumChildWindows(cabinetHwnd, (child, l) => {
            StringBuilder sbClass = new StringBuilder(64);
            GetClassName(child, sbClass, sbClass.Capacity);
            string cls = sbClass.ToString();

            if (cls.Equals("ToolbarWindow32", StringComparison.OrdinalIgnoreCase)) {
                int len = GetWindowTextLength(child);
                if (len > 0) {
                    StringBuilder sbText = new StringBuilder(len + 1);
                    GetWindowText(child, sbText, sbText.Capacity);
                    string text = sbText.ToString().Trim();
                    int colon = text.IndexOf(':');
                    if (colon > 0 && text.StartsWith("Address", StringComparison.OrdinalIgnoreCase)) {
                        string candidate = text.Substring(colon + 1).Trim();
                        if (Directory.Exists(candidate)) {
                            activePath = candidate;
                            return false;
                        }
                    }
                }
            } else if (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase)) {
                int len = GetWindowTextLength(child);
                if (len > 0) {
                    StringBuilder sbText = new StringBuilder(len + 1);
                    GetWindowText(child, sbText, sbText.Capacity);
                    string candidate = sbText.ToString().Trim();
                    if (Directory.Exists(candidate)) {
                        activePath = candidate;
                        return false;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        return activePath;
    }

    private static bool IsFileLockedDirect(string filePath) {
        if (!File.Exists(filePath)) return false;
        try {
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                return false;
            }
        } catch (IOException) {
            return true;
        } catch (UnauthorizedAccessException) {
            return true;
        } catch {
            return false;
        }
    }

    private static List<string> GetCandidatePaths(IntPtr explorerDialogHwnd, List<string> childTexts) {
        List<string> candidatePaths = new List<string>();

        IntPtr ownerCabinet = GetExplorerWindowForDialog(explorerDialogHwnd);
        long ownerHwndVal = ownerCabinet != IntPtr.Zero ? ownerCabinet.ToInt64() : 0;
        string activeAddressPath = GetExplorerActiveFolderPath(ownerCabinet);

        string ownerTitle = "";
        if (ownerCabinet != IntPtr.Zero) {
            int len = GetWindowTextLength(ownerCabinet);
            if (len > 0) {
                StringBuilder sb = new StringBuilder(len + 1);
                GetWindowText(ownerCabinet, sb, sb.Capacity);
                ownerTitle = sb.ToString().Trim();
            }
        }

        IntPtr activeTabHwnd = IntPtr.Zero;
        if (ownerCabinet != IntPtr.Zero) {
            activeTabHwnd = FindWindowEx(ownerCabinet, IntPtr.Zero, "ShellTabWindowClass", null);
        }

        Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        Guid IID_IOleWindow = new Guid("00000114-0000-0000-C000-000000000046");

        List<ExplorerTabCandidate> tabs = new List<ExplorerTabCandidate>();

        try {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null) {
                object shell = Activator.CreateInstance(shellType);
                object windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
                int count = (int)windows.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, windows, null);

                for (int i = 0; i < count; i++) {
                    object window = windows.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    if (window == null) continue;

                    try {
                        long wHwnd = 0;
                        try {
                            object h = window.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, window, null);
                            if (h != null) wHwnd = Convert.ToInt64(h);
                        } catch { }

                        object doc = window.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, window, null);
                        if (doc == null) continue;

                        string folderPath = null;
                        try {
                            object folder = doc.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, doc, null);
                            if (folder != null) {
                                object self = folder.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
                                if (self != null) {
                                    folderPath = (string)self.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null);
                                }
                            }
                        } catch { }

                        string locName = null;
                        try {
                            locName = (string)window.GetType().InvokeMember("LocationName", BindingFlags.GetProperty, null, window, null);
                        } catch { }

                        ExplorerTabCandidate tab = new ExplorerTabCandidate();
                        tab.Window = window;
                        tab.FolderPath = folderPath;
                        tab.LocationName = locName;

                        try {
                            object selectedItems = doc.GetType().InvokeMember("SelectedItems", BindingFlags.InvokeMethod, null, doc, null);
                            if (selectedItems != null) {
                                int selCount = (int)selectedItems.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, selectedItems, null);
                                for (int j = 0; j < selCount; j++) {
                                    object item = selectedItems.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, selectedItems, new object[] { j });
                                    string p = (string)item.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, item, null);
                                    if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))) {
                                        tab.SelectedItems.Add(p);
                                    }
                                }
                            }
                        } catch { }

                        // 1. Matches active address bar path
                        if (!string.IsNullOrEmpty(activeAddressPath) && !string.IsNullOrEmpty(folderPath)) {
                            if (string.Equals(activeAddressPath.TrimEnd('\\', '/'), folderPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) {
                                tab.Score += 1000;
                            }
                        }

                        // 2. Matches active Explorer window handle
                        if (ownerHwndVal != 0 && wHwnd == ownerHwndVal) {
                            tab.Score += 50;
                        }

                        // 3. Matches active ShellTabWindowClass via IOleWindow
                        if (activeTabHwnd != IntPtr.Zero) {
                            try {
                                INativeServiceProvider sp = window as INativeServiceProvider;
                                if (sp != null) {
                                    IntPtr pBrowser = IntPtr.Zero;
                                    if (sp.QueryService(ref SID_STopLevelBrowser, ref IID_IOleWindow, out pBrowser) == 0 && pBrowser != IntPtr.Zero) {
                                        IOleWindow oleWin = Marshal.GetObjectForIUnknown(pBrowser) as IOleWindow;
                                        IntPtr thisTabHwnd = IntPtr.Zero;
                                        if (oleWin != null) oleWin.GetWindow(out thisTabHwnd);
                                        Marshal.Release(pBrowser);

                                        if (thisTabHwnd == activeTabHwnd) {
                                            tab.Score += 300;
                                        }
                                    }
                                }
                            } catch { }
                        }

                        // 4. Windows 11 title displays active tab name
                        if (!string.IsNullOrEmpty(locName) && !string.IsNullOrEmpty(ownerTitle)) {
                            if (ownerTitle.StartsWith(locName, StringComparison.OrdinalIgnoreCase) ||
                                ownerTitle.Equals(locName, StringComparison.OrdinalIgnoreCase)) {
                                tab.Score += 400;
                            } else if (ownerTitle.IndexOf(locName, StringComparison.OrdinalIgnoreCase) >= 0) {
                                tab.Score += 200;
                            }
                        }

                        // 5. Prioritize tab whose selected items are actually in use / locked
                        foreach (string sel in tab.SelectedItems) {
                            if (IsFileLockedDirect(sel)) {
                                tab.Score += 500;
                                break;
                            }
                        }

                        // 6. Match dialog text against filenames
                        if (childTexts != null) {
                            foreach (string ct in childTexts) {
                                if (string.IsNullOrEmpty(ct)) continue;
                                foreach (string sel in tab.SelectedItems) {
                                    string selName = Path.GetFileName(sel);
                                    if (ct.IndexOf(selName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        selName.IndexOf(ct, StringComparison.OrdinalIgnoreCase) >= 0) {
                                        tab.Score += 300;
                                        break;
                                    }
                                }
                            }
                        }

                        tabs.Add(tab);
                    } catch { }
                }
            }
        } catch { }

        // Sort tabs by score descending to isolate the active tab
        tabs.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (tabs.Count > 0 && tabs[0].Score > 0 && tabs[0].SelectedItems.Count > 0) {
            foreach (string p in tabs[0].SelectedItems) {
                if (!candidatePaths.Contains(p)) candidatePaths.Add(p);
            }
        }

        // Direct rooted path fallback from dialog text
        if (candidatePaths.Count == 0 && childTexts != null) {
            foreach (string text in childTexts) {
                if (!string.IsNullOrEmpty(text)) {
                    try {
                        if (Path.IsPathRooted(text) && (File.Exists(text) || Directory.Exists(text))) {
                            if (!candidatePaths.Contains(text)) candidatePaths.Add(text);
                        }
                    } catch { }
                }
            }
        }

        // Safety fallback: ensure dialog shows if selection could not be uniquely scored
        if (candidatePaths.Count == 0) {
            foreach (ExplorerTabCandidate t in tabs) {
                foreach (string p in t.SelectedItems) {
                    if (!candidatePaths.Contains(p)) candidatePaths.Add(p);
                }
            }
        }

        return candidatePaths;
    }

    private static bool AreContextKeysRegistered() {
        try {
            using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)) {
                using (var k = baseKey.OpenSubKey(@"SOFTWARE\Classes\Directory\shell\UnBlock")) {
                    return k != null;
                }
            }
        } catch { return true; }
    }

    internal static void RunInteractiveUninstall(bool silent) {
        try { Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System); } catch { }

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

        string installDir = "";
        try { installDir = Path.GetDirectoryName(Application.ExecutablePath); } catch { }
        string localAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnBlock");

        if (silent) {
            PerformUninstallSteps(true);
            SpawnCleanupHelper(Process.GetCurrentProcess().Id, installDir, localAppDataDir);
            return;
        }

        using (UninstallWizardForm wizard = new UninstallWizardForm(installDir, localAppDataDir)) {
            Application.Run(wizard);
        }
    }

    private static bool IsCurrentUserElevated() {
        WindowsIdentity id = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(id);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static void PerformUninstallSteps(bool killInstances) {
        if (killInstances) KillRunningInstances();
        DeleteScheduledTask();
        CleanRegistryOnly();
        RemoveClassicMenuPreference();
        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) {
                if (k != null) k.DeleteValue("UnBlockWatcher", false);
            }
        } catch { }
        try {
            string localAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnBlock");
            DeleteDirectoryWithRetry(localAppDataDir);
        } catch { }
    }

    private static void RemoveClassicMenuPreference() {
        try {
            using (Microsoft.Win32.RegistryKey marker = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\UnBlock")) {
                if (marker == null || marker.GetValue("ClassicMenu") == null) return;
            }
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\UnBlock", false);
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
                finally { try { p.Dispose(); } catch { } }
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

    private static void NavigateExplorerToParent(string dir) {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try {
            string parentDir = Path.GetDirectoryName(Path.GetFullPath(dir).TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir)) return;

            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;

            object shell = Activator.CreateInstance(shellType);
            object windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
            int count = (int)windows.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, windows, null);
            string cleanTarget = Path.GetFullPath(dir).TrimEnd('\\', '/');

            for (int i = 0; i < count; i++) {
                try {
                    object window = windows.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    if (window == null) continue;

                    string currentPath = null;
                    try {
                        object doc = window.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, window, null);
                        if (doc != null) {
                            object folder = doc.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, doc, null);
                            if (folder != null) {
                                object self = folder.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
                                if (self != null) {
                                    currentPath = (string)self.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null);
                                }
                            }
                        }
                    } catch { }

                    if (string.IsNullOrEmpty(currentPath)) {
                        try {
                            string url = (string)window.GetType().InvokeMember("LocationURL", BindingFlags.GetProperty, null, window, null);
                            if (!string.IsNullOrEmpty(url) && url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) {
                                currentPath = new Uri(url).LocalPath;
                            }
                        } catch { }
                    }

                    if (!string.IsNullOrEmpty(currentPath)) {
                        string cleanCurrent = Path.GetFullPath(currentPath).TrimEnd('\\', '/');
                        if (string.Equals(cleanCurrent, cleanTarget, StringComparison.OrdinalIgnoreCase) ||
                            cleanCurrent.StartsWith(cleanTarget + "\\", StringComparison.OrdinalIgnoreCase)) {
                            window.GetType().InvokeMember("Navigate", BindingFlags.InvokeMethod, null, window, new object[] { parentDir });
                        }
                    }
                } catch { }
            }
        } catch { }
    }

    private static void DeleteDirectoryWithRetry(string path) {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        NavigateExplorerToParent(path);
        for (int i = 0; i < 6; i++) {
            try {
                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
                if (!Directory.Exists(path)) return;
            } catch { }
            Thread.Sleep(400);
        }
    }

    internal static void SpawnCleanupHelper(int pidToWait, params string[] directories) {
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
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Process.Start(psi);
        } catch { }
    }

    internal static void RunCleanupHelper(string[] args) {
        try {
            try { Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System); } catch { }
            int pidToWait = int.Parse(args[1]);
            try {
                using (Process p = Process.GetProcessById(pidToWait)) { p.WaitForExit(20000); }
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
                using (var k = baseKey.CreateSubKey(@"SOFTWARE\Classes\*\shell\UnBlock")) {
                    k.SetValue("", "UnBlock");
                    k.SetValue("Icon", "shell32.dll,239");
                    using (var cmd = k.CreateSubKey("command")) { cmd.SetValue("", string.Format("\"{0}\" \"%1\"", exePath)); }
                }
                using (var k = baseKey.CreateSubKey(@"SOFTWARE\Classes\Directory\shell\UnBlock")) {
                    k.SetValue("", "UnBlock");
                    k.SetValue("Icon", "shell32.dll,239");
                    using (var cmd = k.CreateSubKey("command")) { cmd.SetValue("", string.Format("\"{0}\" \"%1\"", exePath)); }
                }
                using (var k = baseKey.CreateSubKey(@"SOFTWARE\Classes\Directory\Background\shell\UnBlock")) {
                    k.SetValue("", "UnBlock This Folder");
                    k.SetValue("Icon", "shell32.dll,239");
                    using (var cmd = k.CreateSubKey("command")) { cmd.SetValue("", string.Format("\"{0}\" \"%V\"", exePath)); }
                }
                using (var k = baseKey.CreateSubKey(@"SOFTWARE\Classes\Drive\shell\UnBlock")) {
                    k.SetValue("", "UnBlock");
                    k.SetValue("Icon", "shell32.dll,239");
                    using (var cmd = k.CreateSubKey("command")) { cmd.SetValue("", string.Format("\"{0}\" \"%1\"", exePath)); }
                }
            }
        } catch { }
    }
}

// Modern Fluent Uninstaller Form
internal class UninstallWizardForm : Form {
    private readonly string installDir;
    private readonly string localAppDataDir;
    private Button btnUninstall;
    private Button btnFinish;
    private ProgressBar progressBar;
    private Label lblStatus;
    private Panel contentPanel;

    public UninstallWizardForm(string installDir, string localAppDataDir) {
        this.installDir = installDir;
        this.localAppDataDir = localAppDataDir;

        this.Text = "Uninstall UnBlock";
        this.Size = new Size(500, 350);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(248, 249, 250);
        this.Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        BuildUI();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        this.Activate();
        this.BringToFront();
        Uninstaller.SetForegroundWindow(this.Handle);
    }

    private void BuildUI() {
        Panel headerPanel = new Panel() {
            Dock = DockStyle.Top,
            Height = 68,
            BackColor = Color.FromArgb(44, 53, 64)
        };

        Label lblTitle = new Label() {
            Text = "Uninstall UnBlock",
            Location = new Point(20, 12),
            Size = new Size(300, 22),
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.White
        };

        Label lblSubtitle = new Label() {
            Text = "Remove UnBlock File & Folder Unlocker and context menus from this system.",
            Location = new Point(21, 37),
            Size = new Size(450, 18),
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(180, 192, 204)
        };

        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblSubtitle);

        Panel bottomBar = new Panel() {
            Dock = DockStyle.Bottom,
            Height = 54,
            BackColor = Color.FromArgb(241, 243, 245)
        };
        Panel topBorder = new Panel() { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(222, 226, 230) };
        bottomBar.Controls.Add(topBorder);

        btnUninstall = new Button() {
            Text = "Uninstall Now",
            Size = new Size(115, 32),
            Location = new Point(265, 11),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(192, 57, 43),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnUninstall.FlatAppearance.BorderSize = 0;
        btnUninstall.Click += BtnUninstall_Click;

        btnFinish = new Button() {
            Text = "Cancel",
            Size = new Size(85, 32),
            Location = new Point(390, 11),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(225, 229, 233),
            ForeColor = Color.FromArgb(40, 40, 40),
            Font = new Font("Segoe UI", 9F),
            Cursor = Cursors.Hand
        };
        btnFinish.FlatAppearance.BorderSize = 0;
        btnFinish.Click += (s, e) => this.Close();

        bottomBar.Controls.Add(btnUninstall);
        bottomBar.Controls.Add(btnFinish);

        contentPanel = new Panel() {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 18, 24, 10),
            BackColor = Color.FromArgb(248, 249, 250)
        };

        Label lblPrompt = new Label() {
            Text = "The following components will be removed from your PC:",
            Location = new Point(24, 16),
            Size = new Size(440, 20),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 45, 50)
        };

        Label lblItems = new Label() {
            Text = "• Explorer Right-Click Context Menu Verbs\n" +
                   "• Background Maintenance & Self-Cleaning Watcher Task\n" +
                   "• Installed Executables & Registry Registrations",
            Location = new Point(32, 42),
            Size = new Size(430, 60),
            Font = new Font("Segoe UI", 8.8F),
            ForeColor = Color.FromArgb(70, 75, 80)
        };

        progressBar = new ProgressBar() {
            Location = new Point(24, 115),
            Size = new Size(436, 8),
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };

        lblStatus = new Label() {
            Text = "",
            Location = new Point(24, 128),
            Size = new Size(436, 20),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = Color.FromArgb(100, 105, 110),
            Visible = false
        };

        contentPanel.Controls.Add(lblPrompt);
        contentPanel.Controls.Add(lblItems);
        contentPanel.Controls.Add(progressBar);
        contentPanel.Controls.Add(lblStatus);

        this.Controls.Add(contentPanel);
        this.Controls.Add(bottomBar);
        this.Controls.Add(headerPanel);
        this.AcceptButton = btnUninstall;
        this.CancelButton = btnFinish;
    }

    private void BtnUninstall_Click(object sender, EventArgs e) {
        btnUninstall.Enabled = false;
        progressBar.Visible = true;
        progressBar.Value = 25;
        lblStatus.Visible = true;
        lblStatus.Text = "Removing context menu registrations...";

        ThreadPool.QueueUserWorkItem(s => {
            try {
                Uninstaller.PerformUninstallSteps(true);

                if (this.IsHandleCreated && !this.IsDisposed) {
                    this.BeginInvoke(new MethodInvoker(() => {
                        progressBar.Value = 80;
                        lblStatus.Text = "Scheduling final directory cleanup...";
                    }));
                }

                Uninstaller.SpawnCleanupHelper(Process.GetCurrentProcess().Id, installDir, localAppDataDir);

                if (this.IsHandleCreated && !this.IsDisposed) {
                    this.BeginInvoke(new MethodInvoker(() => {
                        ShowCompletionView();
                    }));
                }
            } catch (Exception ex) {
                if (this.IsHandleCreated && !this.IsDisposed) {
                    this.BeginInvoke(new MethodInvoker(() => {
                        progressBar.Visible = false;
                        lblStatus.ForeColor = Color.FromArgb(192, 57, 43);
                        lblStatus.Text = "Uninstall encountered an error: " + ex.Message;
                        btnUninstall.Enabled = true;
                    }));
                }
            }
        });
    }

    private void ShowCompletionView() {
        contentPanel.Controls.Clear();

        Panel successCard = new Panel() {
            Location = new Point(24, 16),
            Size = new Size(436, 140),
            BackColor = Color.White
        };
        successCard.Paint += (s, pe) => {
            using (Pen p = new Pen(Color.FromArgb(220, 226, 230), 1)) {
                pe.Graphics.DrawRectangle(p, 0, 0, successCard.Width - 1, successCard.Height - 1);
            }
        };

        Label lblSuccessTitle = new Label() {
            Text = "Uninstallation Complete",
            Location = new Point(18, 16),
            Size = new Size(400, 24),
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(39, 174, 96)
        };

        Label lblSuccessDetails = new Label() {
            Text = "UnBlock has been completely removed from your computer.\n\n" +
                   "All context menu entries and background maintenance tasks have been deleted.",
            Location = new Point(20, 48),
            Size = new Size(396, 75),
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(50, 55, 60)
        };

        successCard.Controls.Add(lblSuccessTitle);
        successCard.Controls.Add(lblSuccessDetails);
        contentPanel.Controls.Add(successCard);

        btnUninstall.Visible = false;
        btnFinish.Text = "Close";
        btnFinish.Size = new Size(95, 32);
        btnFinish.Location = new Point(380, 11);
        btnFinish.BackColor = Color.FromArgb(39, 174, 96);
        btnFinish.ForeColor = Color.White;
        btnFinish.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        btnFinish.Focus();
        this.AcceptButton = btnFinish;
    }
}

// Modern, ultra-fast Fluent File-in-Use Modal Dialog
internal class IntegratedPromptForm : Form {
    public enum UserChoice { None, KillAndDelete, UnlockAndDelete, Skip, Cancel }
    public UserChoice SelectedChoice { get; private set; }
    public bool ApplyToAll { get; private set; }

    private Label lblLockProcess;
    private PictureBox picTargetIcon;
    private PictureBox picProcessIcon;
    private Panel lockCard;
    private Button btnKill;
    private Button btnUnlock;
    private Button btnSkip;
    private Button btnCancel;
    private CheckBox chkApplyAll;

    public IntegratedPromptForm(string targetPath, int currentIndex, int totalCount) {
        this.SelectedChoice = UserChoice.Cancel;
        this.ApplyToAll = false;

        string fileName = Path.GetFileName(targetPath);
        if (string.IsNullOrEmpty(fileName)) fileName = targetPath;

        if (totalCount > 1) {
            this.Text = string.Format("File in Use ({0} of {1}) — UnBlock", currentIndex, totalCount);
        } else {
            this.Text = "File in Use — UnBlock";
        }

        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.ClientSize = new Size(550, 255);
        this.BackColor = Color.FromArgb(249, 250, 252);
        this.TopMost = true;
        this.Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        // --- Target File Icon ---
        picTargetIcon = new PictureBox() {
            Location = new Point(20, 15),
            Size = new Size(36, 36),
            SizeMode = PictureBoxSizeMode.CenterImage
        };
        try {
            if (File.Exists(targetPath)) picTargetIcon.Image = Icon.ExtractAssociatedIcon(targetPath).ToBitmap();
            else picTargetIcon.Image = SystemIcons.Warning.ToBitmap();
        } catch {
            picTargetIcon.Image = SystemIcons.Application.ToBitmap();
        }

        Label lblTargetName = new Label() {
            Text = fileName,
            Location = new Point(66, 12),
            Size = new Size(295, 22),
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 28, 32),
            AutoEllipsis = true
        };

        // --- Showing ... of ... files Pill Badge ---
        Panel pnlBadge = new Panel() {
            Location = new Point(370, 13),
            Size = new Size(160, 22),
            BackColor = Color.FromArgb(235, 243, 254)
        };
        pnlBadge.Paint += (s, pe) => {
            using (Pen p = new Pen(Color.FromArgb(186, 214, 250), 1)) {
                pe.Graphics.DrawRectangle(p, 0, 0, pnlBadge.Width - 1, pnlBadge.Height - 1);
            }
        };
        Label lblCounter = new Label() {
            Text = totalCount > 1 ? string.Format("Showing {0} of {1} files", currentIndex, totalCount) : "Showing 1 of 1 file",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(21, 101, 192),
            TextAlign = ContentAlignment.MiddleCenter
        };
        pnlBadge.Controls.Add(lblCounter);

        Label lblTargetDir = new Label() {
            Text = targetPath,
            Location = new Point(67, 35),
            Size = new Size(463, 16),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(108, 117, 125),
            AutoEllipsis = true
        };

        // --- Lock Information Alert Card ---
        lockCard = new Panel() {
            Location = new Point(20, 58),
            Size = new Size(510, 80),
            BackColor = Color.White
        };
        lockCard.Paint += (s, pe) => {
            using (Pen p = new Pen(Color.FromArgb(226, 230, 236), 1)) {
                pe.Graphics.DrawRectangle(p, 0, 0, lockCard.Width - 1, lockCard.Height - 1);
            }
        };

        picProcessIcon = new PictureBox() {
            Location = new Point(14, 14),
            Size = new Size(26, 26),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Image = SystemIcons.Information.ToBitmap()
        };

        Label lblLockHeader = new Label() {
            Text = "Active Lock Detected",
            Location = new Point(48, 11),
            Size = new Size(446, 18),
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            ForeColor = Color.FromArgb(210, 45, 35)
        };

        lblLockProcess = new Label() {
            Text = "Analyzing background locking processes...",
            Location = new Point(48, 31),
            Size = new Size(448, 42),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(55, 65, 75),
            AutoEllipsis = true
        };

        lockCard.Controls.Add(picProcessIcon);
        lockCard.Controls.Add(lblLockHeader);
        lockCard.Controls.Add(lblLockProcess);

        // --- Checkbox: Do this to all current files ---
        chkApplyAll = new CheckBox() {
            Text = "Do this to all current files",
            Location = new Point(22, 146),
            Size = new Size(350, 22),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = Color.FromArgb(40, 45, 50),
            Cursor = Cursors.Hand
        };

        // --- Action Buttons Bar ---
        Panel bottomBar = new Panel() {
            Dock = DockStyle.Bottom,
            Height = 52,
            BackColor = Color.FromArgb(242, 244, 248)
        };
        Panel borderTop = new Panel() { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(226, 230, 236) };
        bottomBar.Controls.Add(borderTop);

        btnKill = new Button() {
            Text = "Kill && Delete",
            Size = new Size(120, 32),
            Location = new Point(116, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(215, 45, 35),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnKill.FlatAppearance.BorderSize = 0;
        btnKill.Click += (s, e) => {
            this.SelectedChoice = UserChoice.KillAndDelete;
            this.ApplyToAll = chkApplyAll.Checked;
            this.Close();
        };

        btnUnlock = new Button() {
            Text = "Unlock && Delete",
            Size = new Size(132, 32),
            Location = new Point(244, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(32, 140, 75),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnUnlock.FlatAppearance.BorderSize = 0;
        btnUnlock.Click += (s, e) => {
            this.SelectedChoice = UserChoice.UnlockAndDelete;
            this.ApplyToAll = chkApplyAll.Checked;
            this.Close();
        };

        btnSkip = new Button() {
            Text = "Skip",
            Size = new Size(72, 32),
            Location = new Point(384, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(226, 230, 236),
            ForeColor = Color.FromArgb(40, 45, 50),
            Font = new Font("Segoe UI", 8.8F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnSkip.FlatAppearance.BorderSize = 0;
        btnSkip.Click += (s, e) => {
            this.SelectedChoice = UserChoice.Skip;
            this.ApplyToAll = chkApplyAll.Checked;
            this.Close();
        };

        btnCancel = new Button() {
            Text = "Cancel",
            Size = new Size(72, 32),
            Location = new Point(462, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(226, 230, 236),
            ForeColor = Color.FromArgb(40, 45, 50),
            Font = new Font("Segoe UI", 8.8F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (s, e) => {
            this.SelectedChoice = UserChoice.Cancel;
            this.ApplyToAll = chkApplyAll.Checked;
            this.Close();
        };

        bottomBar.Controls.Add(btnKill);
        bottomBar.Controls.Add(btnUnlock);
        bottomBar.Controls.Add(btnSkip);
        bottomBar.Controls.Add(btnCancel);

        this.Controls.Add(picTargetIcon);
        this.Controls.Add(lblTargetName);
        this.Controls.Add(pnlBadge);
        this.Controls.Add(lblTargetDir);
        this.Controls.Add(lockCard);
        this.Controls.Add(chkApplyAll);
        this.Controls.Add(bottomBar);
        this.CancelButton = btnCancel;
        this.AcceptButton = btnKill;
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        this.Activate();
        this.BringToFront();
        Uninstaller.SetForegroundWindow(this.Handle);
    }

    public void UpdateLockDetails(List<ProcessItem> lockingProcesses) {
        if (this.IsDisposed || !this.IsHandleCreated) return;
        try {
            this.BeginInvoke(new MethodInvoker(() => {
                if (lockingProcesses != null && lockingProcesses.Count > 0) {
                    List<string> names = new List<string>();
                    string firstProcPath = null;
                    foreach (var p in lockingProcesses) {
                        if (p != null) {
                            names.Add(string.Format("{0} (PID: {1})", p.Name, p.Pid));
                            if (string.IsNullOrEmpty(firstProcPath) && File.Exists(p.Path)) firstProcPath = p.Path;
                        }
                    }
                    lblLockProcess.Text = "Locked by:\n" + string.Join(", ", names.ToArray());

                    if (!string.IsNullOrEmpty(firstProcPath)) {
                        try {
                            picProcessIcon.Image = Icon.ExtractAssociatedIcon(firstProcPath).ToBitmap();
                        } catch { }
                    }
                } else {
                    lblLockProcess.Text = "Exclusive lock detected by an active background system process.";
                }
            }));
        } catch { }
    }
}