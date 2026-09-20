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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr hWnd, uint minFilter, uint maxFilter);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

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
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG {
        public IntPtr hWnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private const uint GW_OWNER = 4;
    private const uint GA_ROOTOWNER = 3;
    private const uint GA_ROOT = 2;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;

    private static WinEventProc windowEventProc;

    private static readonly Dictionary<IntPtr, DateTime> handledDialogs = new Dictionary<IntPtr, DateTime>();
    private static readonly object handledLock = new object();
    private static volatile bool isPromptShowing = false;
    // After a prompt closes, ignore new "File in Use" dialogs for a short window. Explorer often
    // re-shows the same dialog for a retried operation, which otherwise produced a second modal.
    private static DateTime suppressUntilUtc = DateTime.MinValue;
    private static readonly TimeSpan PostPromptCooldown = TimeSpan.FromMilliseconds(1500);
    // The single prompt window that may exist at any time; a second creation is refused.
    private static IntegratedPromptForm activePrompt;

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
        bool mutexCreated;
        using (Mutex watcherMutex = new Mutex(true, @"Global\UnBlock_Watcher_Mutex", out mutexCreated)) {
            if (!mutexCreated) return;

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
    }

    private static void RunExplorerDialogMonitor() {
        windowEventProc = delegate(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint eventThread, uint eventTime) {
            if (eventType == EVENT_OBJECT_SHOW && idObject == OBJID_WINDOW && hWnd != IntPtr.Zero) {
                try { CheckWindowForFileInUse(hWnd, IntPtr.Zero); } catch { }
            }
        };
        IntPtr eventHook = IntPtr.Zero;
        try { eventHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, windowEventProc, 0, 0, WINEVENT_OUTOFCONTEXT); } catch { }
        if (eventHook != IntPtr.Zero) {
            try {
                MSG message;
                while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0) {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            } catch { }
            try { UnhookWinEvent(eventHook); } catch { }
            return;
        }
        while (true) {
            try { EnumWindows(CheckWindowForFileInUse, IntPtr.Zero); } catch { }
            Thread.Sleep(250);
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

        if (!TryClaimPromptSlot(hWnd)) return true;

        // Do not hide the native dialog yet: only hide once candidate paths are confirmed
        Thread uiThread = new Thread(() => ShowIntegratedPrompt(hWnd, childTexts));
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.IsBackground = true;
        uiThread.Start();

        return true;
    }

    // Single-flight gate: exactly one prompt may run at a time, and a short cooldown after a
    // prompt closes swallows Explorer's re-shown dialog for the same retried operation. Returns
    // true when the caller may proceed to show the prompt. Kept internal so it can be tested
    // without a real window handle.
    internal static bool TryClaimPromptSlot(IntPtr hWnd) {
        lock (handledLock) {
            DateTime now = DateTime.UtcNow;
            List<IntPtr> deadHandles = new List<IntPtr>();
            foreach (var kvp in handledDialogs) {
                if (!IsWindow(kvp.Key) || (now - kvp.Value).TotalSeconds > 2) deadHandles.Add(kvp.Key);
            }
            foreach (var dead in deadHandles) handledDialogs.Remove(dead);

            if (isPromptShowing) return false;
            if (now < suppressUntilUtc) return false;
            if (activePrompt != null && !activePrompt.IsDisposed) return false;
            if (handledDialogs.ContainsKey(hWnd)) return false;
            handledDialogs[hWnd] = now;
            isPromptShowing = true;
            return true;
        }
    }

    // Test seam: reports whether a prompt currently owns the single-flight slot.
    internal static bool IsPromptActive { get { return isPromptShowing; } }

    // Test seam: clears the prompt gate and cooldown so a test can exercise it from a clean state.
    internal static void ResetPromptGateForTest() {
        lock (handledLock) {
            handledDialogs.Clear();
            isPromptShowing = false;
            suppressUntilUtc = DateTime.MinValue;
            activePrompt = null;
        }
    }

    // Test seam: marks the current prompt as finished, which arms the cooldown like the real
    // ShowIntegratedPrompt finally block does.
    internal static void FinishPromptForTest() {
        activePrompt = null;
        lock (handledLock) {
            suppressUntilUtc = DateTime.UtcNow + PostPromptCooldown;
            isPromptShowing = false;
        }
    }

    private static void ShowIntegratedPrompt(IntPtr explorerDialogHwnd, List<string> childTexts) {
        try {
            // The single-flight flag is already claimed by the caller.
            List<string> candidatePaths = GetCandidatePaths(explorerDialogHwnd, childTexts);
            if (candidatePaths.Count == 0) {
                // If no candidates could be matched, leave native dialog visible so it never vanishes
                return;
            }

            // Candidates confirmed: hide native Explorer dialog
            ShowWindow(explorerDialogHwnd, SW_HIDE);

            IntegratedPromptForm.UserChoice rememberedChoice = IntegratedPromptForm.UserChoice.None;
            bool restoreNativeDialog = false;

            for (int i = 0; i < candidatePaths.Count; i++) {
                string targetPath = candidatePaths[i];
                if (!File.Exists(targetPath) && !Directory.Exists(targetPath)) continue;

                IntegratedPromptForm.UserChoice choice = rememberedChoice;
                List<ProcessItem> lockingProcesses = new List<ProcessItem>();
                if (choice == IntegratedPromptForm.UserChoice.None) {
                    using (var form = new IntegratedPromptForm(targetPath, i + 1, candidatePaths.Count)) {
                        activePrompt = form;
                        ThreadPool.QueueUserWorkItem(s => {
                            try {
                                UnlockerForm.InitFileTypeIndex();
                                var targetSet = new HashSet<string>(new[] { targetPath }, StringComparer.OrdinalIgnoreCase);
                                lockingProcesses = UnlockerForm.RunFastHandleScanDirect(targetSet);
                                form.UpdateLockDetails(lockingProcesses);
                            } catch { }
                        });

                        form.ShowDialog();
                        activePrompt = null;
                        choice = form.SelectedChoice;
                        if (form.ApplyToAll) {
                            rememberedChoice = choice;
                        }
                    }
                }

                while (true) {
                    if (choice == IntegratedPromptForm.UserChoice.Cancel) {
                        restoreNativeDialog = true;
                        break;
                    }
                    if (choice == IntegratedPromptForm.UserChoice.Skip) break;

                    if (choice == IntegratedPromptForm.UserChoice.KillAndDelete ||
                        choice == IntegratedPromptForm.UserChoice.KillAndRename ||
                        choice == IntegratedPromptForm.UserChoice.KillAndMove) {
                        FileInUseActionFlowOutcome outcome;
                        FileActionResult result = RunFileInUseKillActionFlow(targetPath, choice, lockingProcesses, out outcome);
                        if (outcome == FileInUseActionFlowOutcome.Back) {
                            choice = IntegratedPromptForm.UserChoice.None;
                            rememberedChoice = IntegratedPromptForm.UserChoice.None;
                            using (var form = new IntegratedPromptForm(targetPath, i + 1, candidatePaths.Count)) {
                                activePrompt = form;
                                ThreadPool.QueueUserWorkItem(s => {
                                    try {
                                        UnlockerForm.InitFileTypeIndex();
                                        var targetSet = new HashSet<string>(new[] { targetPath }, StringComparer.OrdinalIgnoreCase);
                                        lockingProcesses = UnlockerForm.RunFastHandleScanDirect(targetSet);
                                        form.UpdateLockDetails(lockingProcesses);
                                    } catch { }
                                });
                                form.ShowDialog();
                                activePrompt = null;
                                choice = form.SelectedChoice;
                                if (form.ApplyToAll) rememberedChoice = choice;
                            }
                            continue;
                        }
                        if (result.Status == FileActionStatus.Cancelled) {
                            restoreNativeDialog = true;
                            break;
                        }
                        if (result.Status != FileActionStatus.Completed) {
                            ShowFileInUseActionFailure(targetPath, result);
                            restoreNativeDialog = true;
                        }
                        break;
                    }

                    if (choice == IntegratedPromptForm.UserChoice.UnlockAndDelete) {
                        if (lockingProcesses.Count == 0) {
                            lockingProcesses = UnlockerForm.RunFastHandleScanDirect(new HashSet<string>(new[] { targetPath }, StringComparer.OrdinalIgnoreCase));
                        }
                        foreach (var proc in lockingProcesses) {
                            if (proc != null) UnlockerForm.UnlockSafelyDirect(proc.Pid, proc.Handles, proc.Name);
                        }
                        try { File.SetAttributes(targetPath, FileAttributes.Normal); } catch { }
                        FileOperationResult recycleResult = FileOperations.Delete(targetPath, DeleteMode.RecycleBin);
                        if (!recycleResult.Success) {
                            ShowFileInUseActionFailure(targetPath, new FileActionResult { Status = FileActionStatus.Failed, ErrorMessage = recycleResult.ErrorMessage });
                            restoreNativeDialog = true;
                        }
                    }
                    break;
                }
                if (restoreNativeDialog) break;
            }

            if (restoreNativeDialog) {
                ShowWindow(explorerDialogHwnd, SW_SHOW);
            } else {
                SendMessage(explorerDialogHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                PostMessage(explorerDialogHwnd, WM_COMMAND, (IntPtr)2, IntPtr.Zero);
            }
        } finally {
            activePrompt = null;
            lock (handledLock) {
                // Briefly ignore new dialogs so Explorer's re-shown dialog for the same retried
                // operation does not open a second modal right after this one closes.
                suppressUntilUtc = DateTime.UtcNow + PostPromptCooldown;
                isPromptShowing = false;
            }
        }
    }

    internal static FileActionRequest CreateFileInUseKillRequest(string targetPath, IntegratedPromptForm.UserChoice choice, string newName, string destinationPath) {
        FileOperationRequest operation = new FileOperationRequest { SourcePath = targetPath };
        string title;
        if (choice == IntegratedPromptForm.UserChoice.KillAndDelete) {
            operation.Kind = FileOperationKind.Delete;
            operation.DeleteMode = DeleteMode.RecycleBin;
            title = "Kill & Recycle";
        } else if (choice == IntegratedPromptForm.UserChoice.KillAndRename) {
            operation.Kind = FileOperationKind.Rename;
            operation.DestinationPath = newName;
            title = "Kill & Rename";
        } else if (choice == IntegratedPromptForm.UserChoice.KillAndMove) {
            operation.Kind = FileOperationKind.Move;
            operation.DestinationPath = destinationPath;
            title = "Kill & Move";
        } else {
            return null;
        }
        FileActionRequest request = new FileActionRequest {
            Title = title,
            LockActionMode = LockActionMode.DetectedLockersOnly,
            AllowProtectedTargets = false
        };
        request.Operations.Add(operation);
        return request;
    }

    private enum FileInUseActionFlowOutcome { Completed, Back, Cancelled, Failed }

    private static FileActionResult RunFileInUseKillActionFlow(string targetPath, IntegratedPromptForm.UserChoice choice, List<ProcessItem> observedLockers, out FileInUseActionFlowOutcome outcome) {
        outcome = FileInUseActionFlowOutcome.Failed;
        string newName = null;
        string destinationPath = null;
        string observedSignature = FileActionCoordinator.BuildLockerSignature(observedLockers ?? new List<ProcessItem>());
        string refreshNotice = null;

        while (true) {
            using (var review = new FileInUseActionReviewForm(targetPath, choice, observedLockers, newName, destinationPath, refreshNotice)) {
                review.ShowDialog();
                if (review.Outcome == FileInUseActionReviewOutcome.Back) {
                    outcome = FileInUseActionFlowOutcome.Back;
                    return new FileActionResult { Status = FileActionStatus.Cancelled, ErrorMessage = "Action returned to the File in Use menu." };
                }
                if (review.Outcome != FileInUseActionReviewOutcome.Confirmed) {
                    outcome = FileInUseActionFlowOutcome.Cancelled;
                    return new FileActionResult { Status = FileActionStatus.Cancelled, ErrorMessage = "Action cancelled by the user." };
                }
                newName = review.RequestedNewName;
                destinationPath = review.RequestedDestination;
            }

            FileActionRequest request = CreateFileInUseKillRequest(targetPath, choice, newName, destinationPath);
            if (request == null) {
                outcome = FileInUseActionFlowOutcome.Failed;
                return new FileActionResult { Status = FileActionStatus.Failed, ErrorMessage = "Unsupported File in Use action." };
            }

            FileActionPlan plan = FileActionCoordinator.Prepare(request, CancellationToken.None, null);
            if (!string.IsNullOrEmpty(plan.ErrorMessage)) {
                outcome = FileInUseActionFlowOutcome.Failed;
                return new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Failed, ErrorMessage = plan.ErrorMessage };
            }

            if (!string.Equals(observedSignature, plan.LockerSignature, StringComparison.Ordinal)) {
                observedLockers = plan.Lockers;
                observedSignature = plan.LockerSignature;
                refreshNotice = "Lock details were refreshed. Review the updated process list before continuing.";
                continue;
            }

            FileActionResult result = ExecuteFileInUseKillAction(request, plan);
            outcome = result.Status == FileActionStatus.Completed ? FileInUseActionFlowOutcome.Completed :
                result.Status == FileActionStatus.Cancelled ? FileInUseActionFlowOutcome.Cancelled : FileInUseActionFlowOutcome.Failed;
            return result;
        }
    }

    private static FileActionResult ExecuteFileInUseKillAction(FileActionRequest request, FileActionPlan plan) {
        FileActionResult result = FileActionCoordinator.ExecuteConfirmed(request, plan, IsCurrentUserElevated(), CancellationToken.None, null);
        if (result.Status != FileActionStatus.NeedsElevation || !result.CanRetryElevated) return result;

        request.PreviouslyTerminatedPids = result.TerminatedPids;
        string error;
        if (!FileActionCoordinator.LaunchElevated(request, out error)) {
            return new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Failed, ErrorMessage = "Elevation was not started: " + error };
        }
        return WaitForElevatedFileAction(request.ActionId);
    }

    private static FileActionResult WaitForElevatedFileAction(string actionId) {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline) {
            FileActionResult result = FileActionCoordinator.ConsumeResult(actionId);
            if (result != null) return result;
            Thread.Sleep(200);
        }
        return new FileActionResult { ActionId = actionId, Status = FileActionStatus.Failed, ErrorMessage = "The elevated action did not return a result in time. The target was left unchanged unless the elevated action already completed." };
    }

    private static void ShowFileInUseActionFailure(string targetPath, FileActionResult result) {
        string error = result == null ? "The action did not return a result." : result.ErrorMessage;
        if (string.IsNullOrEmpty(error) && result != null && result.UnresolvablePids.Count > 0) error = "Unresolved locking PID(s): " + string.Join(", ", result.UnresolvablePids.ToArray());
        MessageBox.Show("UnBlock could not complete the action for:\n" + targetPath + "\n\n" + (error ?? "The target was left unchanged."), "File in Use", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            foreach (Microsoft.Win32.RegistryView view in GetRegistryViews()) {
                using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view)) {
                    using (var k = baseKey.OpenSubKey(@"SOFTWARE\Classes\Directory\shell\UnBlock")) {
                        if (k != null) return true;
                    }
                }
            }
            return false;
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
        foreach (Microsoft.Win32.RegistryView view in GetRegistryViews()) {
            try {
                using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view)) {
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\*\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\Directory\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\Directory\Background\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Classes\Drive\shell\UnBlock", false);
                baseKey.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UnBlock", false);
                }
            } catch { }
        }
    }

    private static Microsoft.Win32.RegistryView[] GetRegistryViews() {
        if (Environment.Is64BitOperatingSystem) return new Microsoft.Win32.RegistryView[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 };
        return new Microsoft.Win32.RegistryView[] { Microsoft.Win32.RegistryView.Default };
    }

    private static void RestoreRegistryKeys(string exePath) {
        foreach (Microsoft.Win32.RegistryView view in GetRegistryViews()) {
            try {
                using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view)) {
                    RestoreRegistryKey(baseKey, @"SOFTWARE\Classes\*\shell\UnBlock", "UnBlock", exePath, "%1");
                    RestoreRegistryKey(baseKey, @"SOFTWARE\Classes\Directory\shell\UnBlock", "UnBlock", exePath, "%1");
                    RestoreRegistryKey(baseKey, @"SOFTWARE\Classes\Directory\Background\shell\UnBlock", "UnBlock This Folder", exePath, "%V");
                    RestoreRegistryKey(baseKey, @"SOFTWARE\Classes\Drive\shell\UnBlock", "UnBlock", exePath, "%1");
                }
            } catch { }
        }
    }

    private static void RestoreRegistryKey(Microsoft.Win32.RegistryKey baseKey, string path, string label, string exePath, string placeholder) {
        using (var k = baseKey.CreateSubKey(path)) {
            k.SetValue("", label);
            k.SetValue("Icon", "shell32.dll,239");
            k.SetValue("MultiSelectModel", "Player");
            using (var cmd = k.CreateSubKey("command")) { cmd.SetValue("", string.Format("\"{0}\" \"{1}\"", exePath, placeholder)); }
        }
    }
}

// Modern Fluent Uninstaller Form
internal class UninstallWizardForm : Form {
    private readonly string installDir;
    private readonly string localAppDataDir;
    private Button btnUninstall;
    private Button btnFinish;
    private Panel wizardBottomBar;
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
        wizardBottomBar = bottomBar;
        Panel topBorder = new Panel() { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(222, 226, 230) };
        bottomBar.Controls.Add(topBorder);

        btnUninstall = new Button() {
            Text = "Uninstall Now",
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
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(225, 229, 233),
            ForeColor = Color.FromArgb(40, 40, 40),
            Font = new Font("Segoe UI", 9F),
            Cursor = Cursors.Hand
        };
        btnFinish.FlatAppearance.BorderSize = 0;
        btnFinish.Click += (s, e) => this.Close();

        // Sized from the label so the button text can never clip, including when it changes to
        // "Close" after a successful uninstall.
        UiLayout.LayoutButtonRow(bottomBar, 54, 11, 24, 8, new Button[] { btnUninstall, btnFinish });

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
        btnFinish.BackColor = Color.FromArgb(39, 174, 96);
        btnFinish.ForeColor = Color.White;
        btnFinish.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        // Re-run the label-driven layout now that the text changed to "Close".
        UiLayout.LayoutButtonRow(wizardBottomBar, 54, 11, 24, 8, new Button[] { btnUninstall, btnFinish });
        btnFinish.Focus();
        this.AcceptButton = btnFinish;
    }
}

// Modern, ultra-fast Fluent File-in-Use Modal Dialog
internal class IntegratedPromptForm : Form {
    public enum UserChoice { None, KillAndDelete, KillAndRename, KillAndMove, UnlockAndDelete, Skip, Cancel }
    public UserChoice SelectedChoice { get; private set; }
    public bool ApplyToAll { get; private set; }

    private Label lblLockProcess;
    private PictureBox picTargetIcon;
    private PictureBox picProcessIcon;
    private Panel lockCard;
    private Button btnKill;
    private Button btnRename;
    private Button btnMove;
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
        this.ClientSize = new Size(600, 340);
        this.BackColor = Color.FromArgb(249, 250, 252);
        this.TopMost = true;
        this.Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        // Body region: a docked stack (header row, lock card, safety, checkbox) above the action
        // bar. Every element is docked or table-positioned, so nothing can overlap.
        Panel body = new Panel() {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(249, 250, 252),
            Padding = new Padding(20, 16, 20, 12)
        };

        // --- Header row: icon | name+path | badge (table cells cannot overlap) ---
        TableLayoutPanel headerRow = new TableLayoutPanel() {
            Dock = DockStyle.Top,
            Height = 56,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = body.BackColor,
            Margin = new Padding(0)
        };
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48F));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        picTargetIcon = new PictureBox() {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.CenterImage,
            Margin = new Padding(0, 0, 8, 0)
        };
        try {
            if (File.Exists(targetPath)) picTargetIcon.Image = Icon.ExtractAssociatedIcon(targetPath).ToBitmap();
            else picTargetIcon.Image = SystemIcons.Warning.ToBitmap();
        } catch {
            picTargetIcon.Image = SystemIcons.Application.ToBitmap();
        }

        Panel nameHost = new Panel() { Dock = DockStyle.Fill, BackColor = body.BackColor, Margin = new Padding(0, 2, 8, 0) };
        Label lblTargetName = new Label() {
            Text = fileName,
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 28, 32),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        Label lblTargetDir = new Label() {
            Text = targetPath,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(108, 117, 125),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0)
        };
        nameHost.Controls.Add(lblTargetDir);
        nameHost.Controls.Add(lblTargetName);

        // --- Showing ... of ... files Pill Badge (auto-sized, aligned right, never overlaps) ---
        Panel pnlBadge = new Panel() {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.FromArgb(235, 243, 254),
            Margin = new Padding(8, 4, 0, 0),
            Padding = new Padding(10, 3, 10, 3),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        pnlBadge.Paint += (s, pe) => {
            using (Pen p = new Pen(Color.FromArgb(186, 214, 250), 1)) {
                pe.Graphics.DrawRectangle(p, 0, 0, pnlBadge.Width - 1, pnlBadge.Height - 1);
            }
        };
        Label lblCounter = new Label() {
            Text = totalCount > 1 ? string.Format("Showing {0} of {1} files", currentIndex, totalCount) : "Showing 1 of 1 file",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(21, 101, 192),
            TextAlign = ContentAlignment.MiddleCenter
        };
        pnlBadge.Controls.Add(lblCounter);

        headerRow.Controls.Add(picTargetIcon, 0, 0);
        headerRow.Controls.Add(nameHost, 1, 0);
        headerRow.Controls.Add(pnlBadge, 2, 0);

        // --- Lock Information Alert Card (docked below the header) ---
        lockCard = new Panel() {
            Dock = DockStyle.Top,
            Height = 92,
            BackColor = Color.White,
            Margin = new Padding(0),
            Padding = new Padding(14, 10, 14, 10)
        };
        lockCard.Paint += (s, pe) => {
            using (Pen p = new Pen(Color.FromArgb(226, 230, 236), 1)) {
                pe.Graphics.DrawRectangle(p, 0, 0, lockCard.Width - 1, lockCard.Height - 1);
            }
        };

        picProcessIcon = new PictureBox() {
            Dock = DockStyle.Left,
            Width = 34,
            SizeMode = PictureBoxSizeMode.CenterImage,
            Image = SystemIcons.Information.ToBitmap(),
            Margin = new Padding(0)
        };

        Label lblLockHeader = new Label() {
            Text = "Active Lock Detected",
            Dock = DockStyle.Top,
            Height = 18,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            ForeColor = Color.FromArgb(210, 45, 35),
            AutoEllipsis = true
        };

        lblLockProcess = new Label() {
            Text = "Analyzing background locking processes...",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(55, 65, 75),
            AutoEllipsis = true
        };

        Panel lockText = new Panel() { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6, 0, 0, 0) };
        lockText.Controls.Add(lblLockProcess);
        lockText.Controls.Add(lblLockHeader);
        lockCard.Controls.Add(lockText);
        lockCard.Controls.Add(picProcessIcon);

        Panel lockCardHost = new Panel() { Dock = DockStyle.Top, Height = 104, BackColor = body.BackColor, Padding = new Padding(0, 12, 0, 0) };
        lockCardHost.Controls.Add(lockCard);

        Label lblSafety = new Label() {
            Text = "Rename and move use the destination you choose. Killing an app can lose unsaved work.",
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(75, 85, 99),
            Padding = new Padding(0, 10, 0, 0)
        };

        chkApplyAll = new CheckBox() {
            Text = "Apply this recycle action to remaining items",
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = Color.FromArgb(40, 45, 50),
            Cursor = Cursors.Hand,
            AutoEllipsis = false
        };

        // Add in reverse dock order so they stack top-to-bottom: header, card, safety, checkbox.
        body.Controls.Add(chkApplyAll);
        body.Controls.Add(lblSafety);
        body.Controls.Add(lockCardHost);
        body.Controls.Add(headerRow);

        // --- Action Buttons Bar ---
        Panel bottomBar = new Panel() {
            Dock = DockStyle.Bottom,
            Height = 86,
            BackColor = Color.FromArgb(242, 244, 248)
        };
        Panel borderTop = new Panel() { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(226, 230, 236) };
        bottomBar.Controls.Add(borderTop);

        // One coherent palette for the modal, matching the main window: danger for destructive,
        // the single accent for the safe file actions, neutral for the rest.
        UiTheme modalTheme = UiTheme.Light;

        btnKill = new Button() {
            Text = "Kill && Recycle",
            FlatStyle = FlatStyle.Flat,
            BackColor = modalTheme.DangerFill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnKill.FlatAppearance.BorderSize = 0;
        btnKill.FlatAppearance.MouseOverBackColor = modalTheme.DangerFillHover;
        ConfigureFocus(btnKill);
        btnKill.AccessibleName = "Kill locking processes and move this item to the Recycle Bin; if the drive has no Recycle Bin the item is left in place";
        btnKill.Click += (s, e) => {
            this.SelectedChoice = UserChoice.KillAndDelete;
            this.ApplyToAll = chkApplyAll.Checked;
            this.Close();
        };

        btnRename = new Button() {
            Text = "Kill && Rename...",
            FlatStyle = FlatStyle.Flat,
            BackColor = modalTheme.AccentFill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnRename.FlatAppearance.BorderSize = 0;
        btnRename.FlatAppearance.MouseOverBackColor = modalTheme.AccentFillHover;
        ConfigureFocus(btnRename);
        btnRename.AccessibleName = "Kill locking processes and rename this item";
        btnRename.Click += (s, e) => {
            this.SelectedChoice = UserChoice.KillAndRename;
            this.ApplyToAll = false;
            this.Close();
        };

        btnMove = new Button() {
            Text = "Kill && Move...",
            FlatStyle = FlatStyle.Flat,
            BackColor = modalTheme.AccentFill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnMove.FlatAppearance.BorderSize = 0;
        btnMove.FlatAppearance.MouseOverBackColor = modalTheme.AccentFillHover;
        ConfigureFocus(btnMove);
        btnMove.AccessibleName = "Kill locking processes and move this item";
        btnMove.Click += (s, e) => {
            this.SelectedChoice = UserChoice.KillAndMove;
            this.ApplyToAll = false;
            this.Close();
        };

        btnUnlock = new Button() {
            Text = "Unlock && Recycle",
            FlatStyle = FlatStyle.Flat,
            BackColor = modalTheme.SuccessFill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnUnlock.FlatAppearance.BorderSize = 0;
        btnUnlock.FlatAppearance.MouseOverBackColor = modalTheme.SuccessFillHover;
        ConfigureFocus(btnUnlock);
        btnUnlock.AccessibleName = "Close compatible file handles and move this item to the Recycle Bin; if the drive has no Recycle Bin the item is left in place";
        btnUnlock.Click += (s, e) => {
            this.SelectedChoice = UserChoice.UnlockAndDelete;
            this.ApplyToAll = chkApplyAll.Checked;
            this.Close();
        };

        btnSkip = new Button() {
            Text = "Skip",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(226, 230, 236),
            ForeColor = Color.FromArgb(40, 45, 50),
            Font = new Font("Segoe UI", 8.8F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnSkip.FlatAppearance.BorderSize = 0;
        ConfigureFocus(btnSkip);
        btnSkip.Click += (s, e) => {
            this.SelectedChoice = UserChoice.Skip;
            this.ApplyToAll = chkApplyAll.Checked;
            this.Close();
        };

        btnCancel = new Button() {
            Text = "Cancel",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(226, 230, 236),
            ForeColor = Color.FromArgb(40, 45, 50),
            Font = new Font("Segoe UI", 8.8F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        ConfigureFocus(btnCancel);
        btnCancel.Click += (s, e) => {
            this.SelectedChoice = UserChoice.Cancel;
            this.ApplyToAll = chkApplyAll.Checked;
            this.Close();
        };

        // Size and place every action button from its own label so none can clip; the row wraps
        // to a second line if the dialog is ever too narrow for one line.
        UiLayout.LayoutButtonRow(bottomBar, 74, 12, 20, 8, new Button[] { btnKill, btnRename, btnMove, btnUnlock, btnSkip, btnCancel });

        // Body fills the space above the action bar; both are docked so they cannot overlap.
        this.Controls.Add(body);
        this.Controls.Add(bottomBar);
        this.CancelButton = btnCancel;
        this.AcceptButton = btnSkip;
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

    private static void ConfigureFocus(Button button) {
        button.GotFocus += (s, e) => {
            button.FlatAppearance.BorderSize = 2;
            button.FlatAppearance.BorderColor = Color.Black;
        };
        button.LostFocus += (s, e) => { button.FlatAppearance.BorderSize = 0; };
    }
}

internal enum FileInUseActionReviewOutcome { None, Confirmed, Back, Cancelled }

internal sealed class FileInUseActionReviewForm : Form {
    private readonly string targetPath;
    private readonly IntegratedPromptForm.UserChoice action;
    private TextBox input;
    private Label validationMessage;
    private Button confirmButton;

    public FileInUseActionReviewOutcome Outcome { get; private set; }
    public string RequestedNewName { get; private set; }
    public string RequestedDestination { get; private set; }

    public FileInUseActionReviewForm(string targetPath, IntegratedPromptForm.UserChoice action, List<ProcessItem> lockers, string initialNewName, string initialDestination, string refreshNotice) {
        this.targetPath = targetPath;
        this.action = action;
        this.Outcome = FileInUseActionReviewOutcome.None;

        string actionTitle = GetActionTitle(action);
        string fileName = Path.GetFileName(targetPath);
        if (string.IsNullOrEmpty(fileName)) fileName = targetPath;

        Text = actionTitle + " | File in Use | UnBlock";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 470);
        BackColor = Color.FromArgb(249, 250, 252);
        TopMost = true;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        // Everything stacks with docking in a padded body, so rows compute their own positions
        // and can never overlap regardless of DPI, notice text, or target length.
        Panel body = new Panel {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(249, 250, 252),
            Padding = new Padding(22, 18, 22, 12)
        };

        Label title = new Label {
            Text = actionTitle,
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 28, 32),
            AutoEllipsis = true
        };
        Label target = new Label {
            Text = fileName,
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(42, 48, 55),
            AutoEllipsis = true
        };
        Label path = new Label {
            Text = targetPath,
            Dock = DockStyle.Top,
            Height = 18,
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(87, 96, 106),
            AutoEllipsis = true
        };

        Panel titleBlock = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = body.BackColor };
        titleBlock.Controls.Add(path);
        titleBlock.Controls.Add(target);
        titleBlock.Controls.Add(title);

        Panel lockCard = new Panel {
            Dock = DockStyle.Top,
            Height = 112,
            BackColor = Color.White,
            Padding = new Padding(14, 12, 14, 12)
        };
        lockCard.Paint += (s, e) => {
            using (Pen pen = new Pen(Color.FromArgb(210, 216, 224))) {
                e.Graphics.DrawRectangle(pen, 0, 0, lockCard.Width - 1, lockCard.Height - 1);
            }
        };
        Label lockHeading = new Label {
            Text = lockers != null && lockers.Count > 0 ? "Processes that will be closed" : "No active locker detected",
            Dock = DockStyle.Top,
            Height = 18,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = lockers != null && lockers.Count > 0 ? Color.FromArgb(183, 58, 47) : Color.FromArgb(41, 128, 85),
            AutoEllipsis = true
        };
        Label lockDetails = new Label {
            Text = BuildLockDetails(lockers),
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(55, 65, 75),
            AutoEllipsis = true
        };
        lockCard.Controls.Add(lockDetails);
        lockCard.Controls.Add(lockHeading);

        Panel lockCardHost = new Panel { Dock = DockStyle.Top, Height = 124, BackColor = body.BackColor, Padding = new Padding(0, 12, 0, 0) };
        lockCardHost.Controls.Add(lockCard);

        // Optional refresh notice sits in its own docked row, so it pushes the input down rather
        // than colliding with it.
        Panel noticeHost = new Panel { Dock = DockStyle.Top, Height = 0, BackColor = body.BackColor };
        if (!string.IsNullOrEmpty(refreshNotice)) {
            Label refreshed = new Label {
                Text = refreshNotice,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(155, 89, 24)
            };
            noticeHost.Padding = new Padding(0, 8, 0, 4);
            noticeHost.Height = 48;
            noticeHost.Controls.Add(refreshed);
        }

        // Input area: label + field (and Browse for move), all docked so they cannot overlap.
        Panel inputHost = new Panel { Dock = DockStyle.Top, Height = 0, BackColor = body.BackColor };
        if (action == IntegratedPromptForm.UserChoice.KillAndRename) {
            Label inputLabel = new Label {
                Text = "New name",
                Dock = DockStyle.Top,
                Height = 18,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(42, 48, 55)
            };
            input = new TextBox {
                Text = string.IsNullOrEmpty(initialNewName) ? fileName : initialNewName,
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 9F),
                AccessibleName = "New file or folder name"
            };
            input.TextChanged += (s, e) => ValidateInput();
            Panel field = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = body.BackColor, Padding = new Padding(0, 4, 0, 0) };
            field.Controls.Add(input);
            inputHost.Controls.Add(field);
            inputHost.Controls.Add(inputLabel);
            inputHost.Height = 52;
        } else if (action == IntegratedPromptForm.UserChoice.KillAndMove) {
            Label inputLabel = new Label {
                Text = "Destination folder",
                Dock = DockStyle.Top,
                Height = 18,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(42, 48, 55)
            };
            input = new TextBox {
                Text = initialDestination ?? "",
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Segoe UI", 9F),
                AccessibleName = "Selected destination folder"
            };
            Button browse = new Button {
                Text = "Browse...",
                Dock = DockStyle.Right,
                Width = 96,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(226, 230, 236),
                ForeColor = Color.FromArgb(40, 45, 50),
                Cursor = Cursors.Hand,
                AccessibleName = "Choose destination folder"
            };
            browse.FlatAppearance.BorderSize = 0;
            UiLayout.EnforceNoClip(browse);
            ConfigureFocus(browse);
            browse.Click += (s, e) => {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog()) {
                    dialog.Description = "Choose where UnBlock should move this item after closing its lock.";
                    if (!string.IsNullOrEmpty(input.Text) && Directory.Exists(input.Text)) dialog.SelectedPath = input.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.SelectedPath;
                }
            };
            Panel field = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = body.BackColor, Padding = new Padding(0, 4, 0, 5) };
            Panel inputPad = new Panel { Dock = DockStyle.Fill, BackColor = body.BackColor, Padding = new Padding(0, 0, 8, 0) };
            inputPad.Controls.Add(input);
            field.Controls.Add(inputPad);
            field.Controls.Add(browse);
            inputHost.Controls.Add(field);
            inputHost.Controls.Add(inputLabel);
            inputHost.Height = 54;
        }

        validationMessage = new Label {
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(183, 58, 47),
            TextAlign = ContentAlignment.MiddleLeft
        };
        Panel validationHost = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = body.BackColor };
        validationHost.Controls.Add(validationMessage);

        Label warning = new Label {
            Text = lockers != null && lockers.Count > 0 ? "Closing an application can lose unsaved work. Only the processes listed above will be closed." : "No process will be closed unless a fresh scan finds a locker.",
            Dock = DockStyle.Top,
            Height = 36,
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(75, 85, 99),
            Padding = new Padding(0, 4, 0, 0)
        };

        // Reverse dock order: bottom-most added first so the stack reads top-to-bottom.
        body.Controls.Add(warning);
        body.Controls.Add(validationHost);
        body.Controls.Add(inputHost);
        body.Controls.Add(noticeHost);
        body.Controls.Add(lockCardHost);
        body.Controls.Add(titleBlock);

        Panel bottomBar = new Panel {
            Dock = DockStyle.Bottom,
            Height = 54,
            BackColor = Color.FromArgb(242, 244, 248)
        };
        Panel divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(210, 216, 224) };
        bottomBar.Controls.Add(divider);
        Button back = new Button {
            Text = "Back",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(226, 230, 236),
            ForeColor = Color.FromArgb(40, 45, 50),
            Cursor = Cursors.Hand,
            AccessibleName = "Return to file action choices"
        };
        back.FlatAppearance.BorderSize = 0;
        ConfigureFocus(back);
        back.Click += (s, e) => { Outcome = FileInUseActionReviewOutcome.Back; Close(); };
        Button cancel = new Button {
            Text = "Cancel",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(226, 230, 236),
            ForeColor = Color.FromArgb(40, 45, 50),
            Cursor = Cursors.Hand,
            AccessibleName = "Cancel and return to the Explorer dialog"
        };
        cancel.FlatAppearance.BorderSize = 0;
        ConfigureFocus(cancel);
        cancel.Click += (s, e) => { Outcome = FileInUseActionReviewOutcome.Cancelled; Close(); };
        confirmButton = new Button {
            Text = actionTitle,
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTheme.Light.DangerFill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            AccessibleName = actionTitle
        };
        confirmButton.FlatAppearance.MouseOverBackColor = UiTheme.Light.DangerFillHover;
        confirmButton.FlatAppearance.BorderSize = 0;
        ConfigureFocus(confirmButton);
        confirmButton.Click += (s, e) => {
            if (!ValidateInput()) return;
            RequestedNewName = action == IntegratedPromptForm.UserChoice.KillAndRename ? input.Text.Trim() : null;
            RequestedDestination = action == IntegratedPromptForm.UserChoice.KillAndMove ? input.Text.Trim() : null;
            Outcome = FileInUseActionReviewOutcome.Confirmed;
            Close();
        };
        // Sized from the label so the confirm button can never clip its action name.
        UiLayout.LayoutButtonRow(bottomBar, 54, 11, 22, 8, new Button[] { back, cancel, confirmButton });

        // Body fills above the action bar; both are docked so they cannot overlap.
        Controls.Add(body);
        Controls.Add(bottomBar);
        CancelButton = cancel;
        AcceptButton = confirmButton;
        ValidateInput();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        Activate();
        BringToFront();
        Uninstaller.SetForegroundWindow(Handle);
        if (input != null && action == IntegratedPromptForm.UserChoice.KillAndRename) {
            input.SelectAll();
            input.Focus();
        } else if (input != null && action == IntegratedPromptForm.UserChoice.KillAndMove) {
            input.Focus();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (Outcome == FileInUseActionReviewOutcome.None) Outcome = FileInUseActionReviewOutcome.Cancelled;
        base.OnFormClosing(e);
    }

    private bool ValidateInput() {
        FileOperationRequest operation = new FileOperationRequest { SourcePath = targetPath };
        if (action == IntegratedPromptForm.UserChoice.KillAndRename) {
            operation.Kind = FileOperationKind.Rename;
            operation.DestinationPath = input == null ? null : input.Text.Trim();
        } else if (action == IntegratedPromptForm.UserChoice.KillAndMove) {
            operation.Kind = FileOperationKind.Move;
            operation.DestinationPath = input == null ? null : input.Text.Trim();
            if (string.IsNullOrEmpty(operation.DestinationPath) || !Directory.Exists(operation.DestinationPath)) {
                ShowValidation("Choose a destination folder that exists.");
                return false;
            }
        } else {
            operation.Kind = FileOperationKind.Delete;
            operation.DeleteMode = DeleteMode.RecycleBin;
        }

        FileOperationResult validation = FileOperations.Validate(operation);
        if (!validation.Success) {
            ShowValidation(validation.ErrorMessage);
            return false;
        }
        ShowValidation(null);
        return true;
    }

    private void ShowValidation(string message) {
        if (validationMessage != null) validationMessage.Text = message ?? "";
        if (confirmButton != null) confirmButton.Enabled = string.IsNullOrEmpty(message);
    }

    private static string GetActionTitle(IntegratedPromptForm.UserChoice choice) {
        if (choice == IntegratedPromptForm.UserChoice.KillAndRename) return "Kill & Rename";
        if (choice == IntegratedPromptForm.UserChoice.KillAndMove) return "Kill & Move";
        return "Kill & Recycle";
    }

    private static string BuildLockDetails(List<ProcessItem> lockers) {
        if (lockers == null || lockers.Count == 0) return "UnBlock will refresh the scan before continuing. No unrelated process will be closed.";
        StringBuilder text = new StringBuilder();
        int shown = Math.Min(lockers.Count, 2);
        for (int i = 0; i < shown; i++) {
            ProcessItem item = lockers[i];
            if (item == null) continue;
            if (text.Length > 0) text.AppendLine();
            text.Append(item.Name ?? "Unknown process").Append(" (PID ").Append(item.Pid).Append(")");
            if (!string.IsNullOrEmpty(item.Path)) text.AppendLine().Append("  ").Append(item.Path);
        }
        if (lockers.Count > shown) text.AppendLine().Append("and ").Append(lockers.Count - shown).Append(" more process(es).");
        return text.ToString();
    }

    private static void ConfigureFocus(Button button) {
        button.GotFocus += (s, e) => {
            button.FlatAppearance.BorderSize = 2;
            button.FlatAppearance.BorderColor = Color.Black;
        };
        button.LostFocus += (s, e) => { button.FlatAppearance.BorderSize = 0; };
    }
}
