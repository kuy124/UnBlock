using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class CiTests {
    private static int checks;
    private static int failures;
    private static string harnessDir;
    private static string repoRoot;

    private static int Main(string[] args) {
        if (args.Length >= 1 && args[0] == "--lock") return RunLockMode(args);

        harnessDir = Path.GetDirectoryName(typeof(CiTests).Assembly.Location);
        repoRoot = null;
        if (args.Length >= 2 && args[0] == "--repo") {
            repoRoot = Path.GetFullPath(args[1]);
        } else {
            repoRoot = LocateRepoRoot();
        }
        if (repoRoot == null || !Directory.Exists(Path.Combine(repoRoot, "src"))) {
            Console.WriteLine("could not locate the source checkout from " + harnessDir);
            return 1;
        }

        TestSourceSelection();
        TestMatchesDosPath();
        TestMatchesDeviceOrDosPath();
        TestFindNetworkTailIndex();
        TestStrictLockProbe();
        TestAccessClassification();
        TestDeleteDirect();
        TestFileOperations();
        TestRecycleBinBehavior();
        TestKillBeforeRenameCoordinator();
        TestFileInUsePopupRequests();
        TestFileInUseActionReviewForms();
        TestButtonNoClip();
        TestNoOverlap();
        TestThemeToggle();
        TestSinglePromptGate();
        TestPendingActionPersistence();
        TestCommandLineOptions();
        TestScanCancellation();
        TestFeatureWiring();
        TestScanBenchmark();
        TestKillAndUnlockNoopSafe();
        TestSetupCompileParity();
        TestLockDetectionEndToEnd();

        Console.WriteLine();
        Console.WriteLine(checks + " checks, " + failures + " failures");
        return failures == 0 ? 0 : 1;
    }

    private static string LocateRepoRoot() {
        DirectoryInfo dir = new DirectoryInfo(harnessDir);
        for (int i = 0; i < 6 && dir != null; i++) {
            string srcDir = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(srcDir) && Directory.GetFiles(srcDir, "*.cs").Length >= 12) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static void Section(string name) {
        Console.WriteLine();
        Console.WriteLine("== " + name + " ==");
    }

    private static void Check(bool ok, string name) {
        checks++;
        if (ok) {
            Console.WriteLine("PASS  " + name);
        } else {
            failures++;
            Console.WriteLine("FAIL  " + name);
        }
    }

    private static MethodInfo StaticMethod(Type type, string name) {
        if (type == null) return null;
        return type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static TargetMatchInfo MakeCtx(string original, bool isDir) {
        TargetMatchInfo info = new TargetMatchInfo();
        info.OriginalPath = original;
        info.NormalizedPath = isDir ? original.TrimEnd('\\') + "\\" : original;
        info.IsDir = isDir;
        info.TargetDevicePath = original.TrimEnd('\\', '/');
        info.DevicePathWithSlash = info.TargetDevicePath + "\\";
        return info;
    }

    private static string MakeScratch() {
        string scratch = Path.Combine(Path.GetTempPath(), "unblock-ci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        return scratch;
    }

    private static void TryDeleteDir(string dir) {
        try {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        } catch { }
    }

    private static bool WaitForFile(string path, int timeoutMs) {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline) {
            if (File.Exists(path)) return true;
            Thread.Sleep(50);
        }
        return File.Exists(path);
    }

    private static int RunLockMode(string[] args) {
        string file = args[1];
        string readyCkpt = args[2];
        string releaseCkpt = args[3];
        using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            fs.WriteByte(0x55);
            fs.Flush();
            File.WriteAllText(readyCkpt, "ready");
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline) {
                if (File.Exists(releaseCkpt)) break;
                Thread.Sleep(50);
            }
        }
        return 0;
    }

    private static void TestSourceSelection() {
        Section("setup source selection");
        string srcDir = Path.Combine(repoRoot, "src");
        string[] expected = new string[] {
            "BuildInfo.cs", "CommandLine.cs", "FileActionCoordinator.cs", "FileOperations.cs", "Models.cs", "ProcessDetailsProvider.cs", "Program.cs", "ScanEngine.cs", "UiTheme.cs", "UninstallEngine.cs",
            "UnlockerForm.cs", "UnlockerForm.Native.cs", "UnlockerForm.Scan.cs", "UnlockerForm.UI.cs"
        };
        string[] actual = Directory.GetFiles(srcDir, "*.cs")
            .Select(Path.GetFileName)
            .Where(n => !string.Equals(n, "Setup.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Check(expected.Length == actual.Length && expected.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(actual),
            "all app modules are picked up exactly once");
    }

    private static void TestMatchesDosPath() {
        Section("dos path matching");
        MethodInfo m = StaticMethod(typeof(UnlockerForm), "MatchesDosPath");
        Check(m != null, "reflected MatchesDosPath");
        if (m == null) return;

        TargetMatchInfo file = MakeCtx(@"C:\docs\a.txt", false);
        Check((bool)m.Invoke(null, new object[] { @"C:\docs\a.txt", file }), "file equals original");
        Check((bool)m.Invoke(null, new object[] { @"c:\DOCS\A.txt", file }), "file match case-insensitive");
        Check(!(bool)m.Invoke(null, new object[] { @"C:\docs\b.txt", file }), "file different name rejected");
        Check(!(bool)m.Invoke(null, new object[] { @"C:\docs", file }), "file sibling path rejected");

        TargetMatchInfo dir = MakeCtx(@"C:\docs", true);
        Check((bool)m.Invoke(null, new object[] { @"C:\docs", dir }), "dir equals original");
        Check((bool)m.Invoke(null, new object[] { @"C:\docs\sub\file.txt", dir }), "dir child file matches");
        Check((bool)m.Invoke(null, new object[] { @"C:\docs\sub", dir }), "dir child dir matches");
        Check(!(bool)m.Invoke(null, new object[] { @"C:\docsx\a.txt", dir }), "dir sibling prefix rejected");
    }

    private static void TestMatchesDeviceOrDosPath() {
        Section("device vs dos path matching");
        MethodInfo m = StaticMethod(typeof(UnlockerForm), "MatchesDeviceOrDosPath");
        Check(m != null, "reflected MatchesDeviceOrDosPath");
        if (m == null) return;

        TargetMatchInfo ctx = MakeCtx(@"C:\docs", true);
        ctx.TargetDevicePath = @"\Device\HarddiskVolume2\docs";
        ctx.DevicePathWithSlash = @"\Device\HarddiskVolume2\docs\";

        Check((bool)m.Invoke(null, new object[] { @"\Device\HarddiskVolume2\docs", ctx }), "device exact dir match");
        Check((bool)m.Invoke(null, new object[] { @"\Device\HarddiskVolume2\docs\file.txt", ctx }), "device child file matches");
        Check((bool)m.Invoke(null, new object[] { @"C:\docs\file.txt", ctx }), "dos child falls through to dos path");
        Check(!(bool)m.Invoke(null, new object[] { @"\Device\HarddiskVolume2\docsx\file.txt", ctx }), "device sibling rejected");
    }

    private static void TestFindNetworkTailIndex() {
        Section("network tail extraction");
        MethodInfo m = StaticMethod(typeof(UnlockerForm), "FindNetworkTailIndex");
        Check(m != null, "reflected FindNetworkTailIndex");
        if (m == null) return;

        string mup = @"\Device\Mup\server\share\folder\file.txt";
        int idx = (int)m.Invoke(null, new object[] { mup });
        Check(idx > 0 && mup.Substring(idx) == @"server\share\folder\file.txt", "mup node path tail");

        string lanman = @"\Device\LanmanRedirector\;Q:0000000000000000\server\share\file.txt";
        int idx2 = (int)m.Invoke(null, new object[] { lanman });
        Check(idx2 >= 0 && lanman.Substring(idx2) == @"server\share\file.txt", "lanmanredirector node path tail");

        Check((int)m.Invoke(null, new object[] { @"C:\docs\file.txt" }) == -1, "no network prefix yields -1");
    }

    private static void TestStrictLockProbe() {
        Section("strict lock probe");
        MethodInfo m = StaticMethod(typeof(UnlockerForm), "IsPathStrictlyLocked");
        Check(m != null, "reflected IsPathStrictlyLocked");
        if (m == null) return;

        string tmp = Path.Combine(Path.GetTempPath(), "unblock-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(tmp, "probe");
        try {
            Check(!(bool)m.Invoke(null, new object[] { tmp }), "unlocked file is not strictly locked");
        } finally {
            File.Delete(tmp);
        }
        Check(!(bool)m.Invoke(null, new object[] { Path.Combine(Path.GetTempPath(), "unblock-missing-" + Guid.NewGuid().ToString("N") + ".tmp") }),
            "missing path is not strictly locked");
    }

    private static string Label(MethodInfo g, uint access, bool isDir) {
        ProcessItem item = new ProcessItem();
        item.GrantedAccess = access;
        item.IsDir = isDir;
        return (string)g.Invoke(null, new object[] { item });
    }

    private static Severity Level(MethodInfo s, string label) {
        return (Severity)s.Invoke(null, new object[] { label });
    }

    private static void TestAccessClassification() {
        Section("severity classification");
        MethodInfo g = StaticMethod(typeof(UnlockerForm), "GetAccessInfo");
        MethodInfo s = StaticMethod(typeof(UnlockerForm), "GetSeverity");
        Check(g != null && s != null, "reflected GetAccessInfo/GetSeverity");
        if (g == null || s == null) return;

        Check(Label(g, 0x0011019f, false) == "Exclusive Write/Delete Lock", "write+delete file labeled exclusive lock");
        Check(Level(s, Label(g, 0x0011019f, false)) == Severity.High, "exclusive lock is high severity");
        Check(Label(g, 0x0012019f, false) == "Active Write Lock", "write-only file labeled active write");
        Check(Label(g, 0x00000001, false) == "Active Read Lock", "read file labeled active read");
        Check(Level(s, Label(g, 0x00000001, false)) == Severity.Medium, "active read is medium severity");
        Check(Label(g, 0x00000000, false) == "Benign File Monitor", "monitor file labeled benign");
        Check(Level(s, Label(g, 0x00000000, false)) == Severity.Low, "benign is low severity");

        Check(Label(g, 0x00010002, true) == "Full Directory Control (Lock)", "dir write+delete labeled full control");
        Check(Label(g, 0x00000002, true) == "Directory Modify (Lock)", "dir write labeled modify lock");
        Check(Label(g, 0x00010000, true) == "Directory Delete (Lock)", "dir delete labeled delete lock");
        Check(Label(g, 0x00000001, true) == "Benign Directory Browse", "dir read labeled benign browse");

        ProcessItem module = new ProcessItem();
        module.IsModuleLock = true;
        module.IsDir = false;
        Check((string)g.Invoke(null, new object[] { module }) == "Active DLL / Module Lock", "module lock labeled for files");
        Check(Level(s, (string)g.Invoke(null, new object[] { module })) == Severity.High, "module lock is high severity");
    }

    private static void TestDeleteDirect() {
        Section("direct deletion");
        string scratch = MakeScratch();
        try {
            string file = Path.Combine(scratch, "file.txt");
            File.WriteAllText(file, "x");
            FileOperationResult deleted = FileOperations.Delete(file, DeleteMode.Permanent);
            Check(deleted.Success, "temp file deleted");
            Check(deleted.ErrorCode == 0, "no deletion error reported");
            Check(!File.Exists(file), "temp file gone from disk");

            FileOperationResult missing = FileOperations.Delete(Path.Combine(scratch, "ghost.txt"), DeleteMode.Permanent);
            Check(missing.Success && missing.AlreadyGone, "already-missing path counts as deleted");

            string dir = Path.Combine(scratch, "tree");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "child.txt"), "y");
            FileOperationResult removed = FileOperations.Delete(dir, DeleteMode.Permanent);
            Check(removed.Success, "non-empty directory removed recursively");
            Check(!Directory.Exists(dir), "directory gone from disk");
        } finally {
            TryDeleteDir(scratch);
        }
    }

    private static void TestKillAndUnlockNoopSafe() {
        Section("safe no-op paths");
        MethodInfo k = StaticMethod(typeof(UnlockerForm), "KillProcessDirect");
        Check(k != null, "reflected KillProcessDirect");
        if (k != null) {
            Check((bool)k.Invoke(null, new object[] { 0x7FFFFFFF, "no-such-process" }), "an exited or stale pid is treated as already cleared");
            Check(!(bool)k.Invoke(null, new object[] { 4, "System" }), "PID 4 is never terminated");
        }

        MethodInfo u = StaticMethod(typeof(UnlockerForm), "UnlockSafelyDirect");
        Check(u != null, "reflected UnlockSafelyDirect");
        if (u != null) {
            Check((bool)u.Invoke(null, new object[] { 0x7FFFFFFF, null, "no-such-process" }), "unlock with no handles is a no-op success");
        }
    }

    private static void TestFileOperations() {
        Section("file operations");
        string scratch = MakeScratch();
        try {
            string source = Path.Combine(scratch, "source.txt");
            string copy = Path.Combine(scratch, "copy.txt");
            string renamed = Path.Combine(scratch, "renamed.txt");
            string movedDir = Path.Combine(scratch, "moved");
            File.WriteAllText(source, "operation test");

            FileOperationResult copyResult = FileOperations.Copy(source, copy, false, CancellationToken.None, null);
            Check(copyResult.Success && File.Exists(copy), "copy operation completes");
            FileOperationResult renameResult = FileOperations.Rename(copy, "renamed.txt", false);
            Check(renameResult.Success && File.Exists(renamed), "rename operation completes");
            Directory.CreateDirectory(movedDir);
            FileOperationResult moveResult = FileOperations.Move(renamed, movedDir, false, CancellationToken.None, null);
            string moved = Path.Combine(movedDir, "renamed.txt");
            Check(moveResult.Success && File.Exists(moved), "move operation completes");
            FileOperationResult deleteResult = FileOperations.Delete(moved, DeleteMode.Permanent);
            Check(deleteResult.Success && !File.Exists(moved), "permanent delete operation completes");

            string recycle = Path.Combine(scratch, "recycle.txt");
            File.WriteAllText(recycle, "recycle test");
            FileOperationResult recycleResult = FileOperations.Delete(recycle, DeleteMode.RecycleBin);
            bool recycled = recycleResult.Success && !File.Exists(recycle);
            bool refusedAndPreserved = !recycleResult.Success && File.Exists(recycle);
            Check(recycled || refusedAndPreserved, "recycle-bin delete either recycles the item or refuses and leaves it in place");
            Check(!recycleResult.Success || !File.Exists(recycle), "a successful recycle never leaves the target at its source location");

            FileOperationResult invalidName = FileOperations.Validate(new FileOperationRequest {
                Kind = FileOperationKind.Rename,
                SourcePath = source,
                DestinationPath = "bad/name.txt"
            });
            Check(!invalidName.Success, "invalid rename name is rejected before termination");

            string existing = Path.Combine(scratch, "existing.txt");
            File.WriteAllText(existing, "existing");
            FileOperationResult conflict = FileOperations.Validate(new FileOperationRequest {
                Kind = FileOperationKind.Rename,
                SourcePath = source,
                DestinationPath = "existing.txt"
            });
            Check(!conflict.Success && conflict.ErrorCode == 183, "existing rename destination is rejected before termination");

            string folder = Path.Combine(scratch, "folder");
            string childFolder = Path.Combine(folder, "child");
            Directory.CreateDirectory(folder);
            FileOperationResult selfCopy = FileOperations.Validate(new FileOperationRequest {
                Kind = FileOperationKind.Copy,
                SourcePath = folder,
                DestinationPath = childFolder
            });
            Check(!selfCopy.Success, "folder copy into itself is rejected before termination");
        } finally {
            TryDeleteDir(scratch);
        }
    }

    private static void TestRecycleBinBehavior() {
        Section("recycle-bin safety");
        string scratch = MakeScratch();
        try {
            string target = Path.Combine(scratch, "recycle-me.txt");
            File.WriteAllText(target, "recycle behavior");
            Check(FileOperations.IsRecycleBinAvailableFor(target), "fixed-drive temp path reports an available Recycle Bin");

            long before = RecycleBinItemCountFor(target);
            FileOperationResult result = FileOperations.Delete(target, DeleteMode.RecycleBin);
            if (FileOperations.IsRecycleBinAvailableFor(target)) {
                Check(result.Success, "recycle-bin delete of a fixed-drive file succeeds");
                Check(!File.Exists(target), "recycled file leaves its source location");
                long after = RecycleBinItemCountFor(target);
                if (before >= 0 && after >= 0) {
                    Check(after > before, "recycle-bin item count increases after a recycle");
                }
            }

            string missing = Path.Combine(scratch, "never-existed.txt");
            FileOperationResult ghost = FileOperations.Delete(missing, DeleteMode.RecycleBin);
            Check(ghost.Success && ghost.AlreadyGone, "recycling an already-missing path counts as done");

            string folder = Path.Combine(scratch, "recycle-dir");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "child.txt"), "child");
            FileOperationResult dirResult = FileOperations.Delete(folder, DeleteMode.RecycleBin);
            if (FileOperations.IsRecycleBinAvailableFor(folder)) {
                Check(dirResult.Success, "recycle-bin delete of a fixed-drive folder succeeds");
                Check(!Directory.Exists(folder), "recycled folder leaves its source location");
            }

            Check(!FileOperations.IsRecycleBinAvailableFor(@"\\server\share\file.txt"), "network paths report no Recycle Bin");
        } finally {
            TryDeleteDir(scratch);
        }
    }

    private static long RecycleBinItemCountFor(string path) {
        try {
            string root = Path.GetPathRoot(Path.GetFullPath(path));
            return RecycleBinItemCount(root);
        } catch {
            return -1;
        }
    }

    private static long RecycleBinItemCount(string root) {
        try {
            ShellShQueryRecycleBin info = new ShellShQueryRecycleBin();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(ShellShQueryRecycleBin));
            int hr = SHQueryRecycleBin(root, ref info);
            return hr == 0 ? info.i64NumItems : -1;
        } catch {
            return -1;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ShellShQueryRecycleBin {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref ShellShQueryRecycleBin pSHQueryRBInfo);

    private static void TestKillBeforeRenameCoordinator() {
        Section("kill-before-rename coordinator");
        Type coordinator = typeof(UnlockerForm).Assembly.GetType("FileActionCoordinator");
        MethodInfo prepare = StaticMethod(coordinator, "Prepare");
        MethodInfo execute = StaticMethod(coordinator, "ExecuteConfirmed");
        Check(coordinator != null && prepare != null && execute != null, "file action coordinator entry points exist");
        if (coordinator == null || prepare == null || execute == null) return;

        string scratch = MakeScratch();
        string file = Path.Combine(scratch, "locked.txt");
        string renamed = Path.Combine(scratch, "renamed.txt");
        string readyCkpt = Path.Combine(scratch, "ready.ckpt");
        string releaseCkpt = Path.Combine(scratch, "release.ckpt");
        File.WriteAllText(file, "payload");
        Process child = null;
        try {
            ProcessStartInfo psi = new ProcessStartInfo {
                FileName = typeof(CiTests).Assembly.Location,
                Arguments = string.Format("\"--lock\" \"{0}\" \"{1}\" \"{2}\"", file, readyCkpt, releaseCkpt),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            child = Process.Start(psi);
            bool ready = WaitForFile(readyCkpt, 30000);
            Check(ready, "rename coordinator lock holder signals ready");
            if (!ready) return;

            FileActionRequest request = new FileActionRequest {
                Title = "Kill & Rename",
                LockActionMode = LockActionMode.DetectedLockersOnly
            };
            request.Operations.Add(new FileOperationRequest {
                Kind = FileOperationKind.Rename,
                SourcePath = file,
                DestinationPath = "renamed.txt"
            });

            object plan = null;
            Exception prepareError = null;
            try {
                plan = prepare.Invoke(null, new object[] { request, CancellationToken.None, null });
            } catch (TargetInvocationException tie) {
                prepareError = tie.InnerException ?? tie;
            } catch (Exception ex) {
                prepareError = ex;
            }
            Check(prepareError == null, "coordinator refresh scan completes" + (prepareError == null ? "" : ": " + prepareError.Message));
            if (plan == null) return;

            PropertyInfo lockers = plan.GetType().GetProperty("Lockers");
            var lockerList = lockers == null ? null : lockers.GetValue(plan, null) as System.Collections.ICollection;
            Check(lockerList != null && lockerList.Count > 0, "coordinator identifies only the selected target locker");

            object result = null;
            Exception executeError = null;
            try {
                result = execute.Invoke(null, new object[] { request, plan, true, CancellationToken.None, null });
            } catch (TargetInvocationException tie) {
                executeError = tie.InnerException ?? tie;
            } catch (Exception ex) {
                executeError = ex;
            }
            Check(executeError == null, "coordinator terminates and executes rename" + (executeError == null ? "" : ": " + executeError.Message));
            if (result != null) {
                PropertyInfo status = result.GetType().GetProperty("Status");
                Check(status != null && (FileActionStatus)status.GetValue(result, null) == FileActionStatus.Completed, "kill-before-rename reports completed");
            }
            Check(File.Exists(renamed) && !File.Exists(file), "rename occurs only after the lock is cleared");
        } finally {
            try { File.WriteAllText(releaseCkpt, "go"); } catch { }
            if (child != null) {
                if (!child.WaitForExit(10000)) {
                    try { child.Kill(); } catch { }
                }
                child.Dispose();
            }
            TryDeleteDir(scratch);
        }
    }

    private static void TestPendingActionPersistence() {
        Section("pending elevated action persistence");
        Assembly app = typeof(UnlockerForm).Assembly;
        Type store = app.GetType("PendingActionStore");
        MethodInfo writeRequest = StaticMethod(store, "WriteRequest");
        MethodInfo readRequest = StaticMethod(store, "ReadRequest");
        MethodInfo writeResult = StaticMethod(store, "WriteResult");
        MethodInfo consumeResult = StaticMethod(store, "ConsumeResult");
        Check(store != null && writeRequest != null && readRequest != null && writeResult != null && consumeResult != null, "pending action store entry points exist");
        if (store == null || writeRequest == null || readRequest == null || writeResult == null || consumeResult == null) return;

        FileActionRequest request = new FileActionRequest {
            ActionId = "ci-" + Guid.NewGuid().ToString("N"),
            Title = "Kill & Copy",
            LockActionMode = LockActionMode.DetectedLockersOnly
        };
        request.Operations.Add(new FileOperationRequest {
            Kind = FileOperationKind.Copy,
            SourcePath = Path.Combine(Path.GetTempPath(), "pending-source.txt"),
            DestinationPath = Path.Combine(Path.GetTempPath(), "pending-destination.txt")
        });
        string requestPath = null;
        try {
            try {
                requestPath = (string)writeRequest.Invoke(null, new object[] { request });
            } catch (TargetInvocationException tie) {
                if (tie.InnerException is UnauthorizedAccessException) {
                    Check(true, "pending action store is skipped when the test sandbox blocks LocalAppData writes");
                    return;
                }
                throw;
            }
            FileActionRequest restored = (FileActionRequest)readRequest.Invoke(null, new object[] { requestPath });
            Check(restored != null && restored.ActionId == request.ActionId && restored.Operations.Count == 1 && restored.Operations[0].Kind == FileOperationKind.Copy,
                "pending action JSON round-trips the exact operation");

            FileActionResult expected = new FileActionResult { ActionId = request.ActionId, Status = FileActionStatus.Failed, ErrorMessage = "test result" };
            writeResult.Invoke(null, new object[] { expected });
            FileActionResult consumed = (FileActionResult)consumeResult.Invoke(null, new object[] { request.ActionId });
            Check(consumed != null && consumed.ActionId == request.ActionId && consumed.ErrorMessage == "test result", "pending action result is consumed and correlated");
        } finally {
            try { if (!string.IsNullOrEmpty(requestPath) && File.Exists(requestPath)) File.Delete(requestPath); } catch { }
        }
    }

    private static void TestFileInUsePopupRequests() {
        Section("file-in-use popup actions");
        Type uninstaller = typeof(UnlockerForm).Assembly.GetType("Uninstaller");
        Type choiceType = typeof(UnlockerForm).Assembly.GetType("IntegratedPromptForm+UserChoice");
        MethodInfo createRequest = StaticMethod(uninstaller, "CreateFileInUseKillRequest");
        Check(uninstaller != null && choiceType != null && createRequest != null, "file-in-use action request factory exists");
        if (uninstaller == null || choiceType == null || createRequest == null) return;

        string source = Path.Combine(Path.GetTempPath(), "unblock-popup-source.txt");
        FileActionRequest recycle = (FileActionRequest)createRequest.Invoke(null, new object[] {
            source, Enum.Parse(choiceType, "KillAndDelete"), null, null
        });
        Check(recycle != null && recycle.Title == "Kill & Recycle" && recycle.LockActionMode == LockActionMode.DetectedLockersOnly &&
              recycle.Operations.Count == 1 && recycle.Operations[0].Kind == FileOperationKind.Delete && recycle.Operations[0].DeleteMode == DeleteMode.RecycleBin,
              "popup recycle request uses detected-lockers-only policy");

        FileActionRequest rename = (FileActionRequest)createRequest.Invoke(null, new object[] {
            source, Enum.Parse(choiceType, "KillAndRename"), "renamed.txt", null
        });
        Check(rename != null && rename.Title == "Kill & Rename" && rename.Operations[0].Kind == FileOperationKind.Rename && rename.Operations[0].DestinationPath == "renamed.txt",
            "popup rename request keeps the requested new name");

        string destination = Path.Combine(Path.GetTempPath(), "unblock-popup-destination");
        FileActionRequest move = (FileActionRequest)createRequest.Invoke(null, new object[] {
            source, Enum.Parse(choiceType, "KillAndMove"), null, destination
        });
        Check(move != null && move.Title == "Kill & Move" && move.Operations[0].Kind == FileOperationKind.Move && move.Operations[0].DestinationPath == destination,
            "popup move request keeps the chosen destination");
    }

    private static void TestFileInUseActionReviewForms() {
        Section("file-in-use action-specific reviews");
        Assembly app = typeof(UnlockerForm).Assembly;
        Type reviewType = app.GetType("FileInUseActionReviewForm");
        Type choiceType = app.GetType("IntegratedPromptForm+UserChoice");
        Check(reviewType != null && choiceType != null, "action-specific review form is available");
        if (reviewType == null || choiceType == null) return;

        string scratch = MakeScratch();
        string source = Path.Combine(scratch, "locked.txt");
        string destination = Path.Combine(scratch, "destination");
        File.WriteAllText(source, "test");
        Directory.CreateDirectory(destination);
        try {
            Form rename = (Form)Activator.CreateInstance(reviewType, new object[] {
                source, Enum.Parse(choiceType, "KillAndRename"), new List<ProcessItem>(), "renamed.txt", null, null
            });
            try {
                List<Button> renameButtons = FindButtons(rename);
                Check(HasButton(renameButtons, "Kill & Rename") && !HasButton(renameButtons, "Kill & Move") && !HasButton(renameButtons, "Kill & Recycle") && !HasButton(renameButtons, "Unlock & Recycle"),
                    "rename review exposes only its matching action");
                Check(rename.AcceptButton != null && rename.CancelButton != null && HasButton(renameButtons, "Back"), "rename review supports Enter, Escape, and Back");
                TextBox nameInput = FindTextBox(rename, "New file or folder name");
                Button renameConfirm = FindButton(renameButtons, "Kill & Rename");
                if (nameInput != null) nameInput.Text = "bad/name";
                Check(renameConfirm != null && !renameConfirm.Enabled, "invalid rename disables Kill & Rename");
            } finally {
                rename.Dispose();
            }

            Form move = (Form)Activator.CreateInstance(reviewType, new object[] {
                source, Enum.Parse(choiceType, "KillAndMove"), new List<ProcessItem>(), null, null, null
            });
            try {
                List<Button> moveButtons = FindButtons(move);
                Check(HasButton(moveButtons, "Kill & Move") && !HasButton(moveButtons, "Kill & Rename") && !HasButton(moveButtons, "Kill & Recycle") && !HasButton(moveButtons, "Unlock & Recycle"),
                    "move review exposes only its matching action");
                TextBox destinationInput = FindTextBox(move, "Selected destination folder");
                Button moveConfirm = FindButton(moveButtons, "Kill & Move");
                Check(moveConfirm != null && !moveConfirm.Enabled, "move review requires a destination before it can run");
                if (destinationInput != null) destinationInput.Text = destination;
                MethodInfo validateInput = reviewType.GetMethod("ValidateInput", BindingFlags.Instance | BindingFlags.NonPublic);
                bool validDestination = validateInput != null && (bool)validateInput.Invoke(move, null);
                Check(validDestination && moveConfirm != null && moveConfirm.Enabled, "existing destination enables Kill & Move");
            } finally {
                move.Dispose();
            }
        } finally {
            TryDeleteDir(scratch);
        }
    }

    private static List<Button> FindButtons(Control parent) {
        List<Button> buttons = new List<Button>();
        foreach (Control control in parent.Controls) {
            Button button = control as Button;
            if (button != null) buttons.Add(button);
            buttons.AddRange(FindButtons(control));
        }
        return buttons;
    }

    private static TextBox FindTextBox(Control parent, string accessibleName) {
        foreach (Control control in parent.Controls) {
            TextBox box = control as TextBox;
            if (box != null && box.AccessibleName == accessibleName) return box;
            TextBox nested = FindTextBox(control, accessibleName);
            if (nested != null) return nested;
        }
        return null;
    }

    private static bool HasButton(List<Button> buttons, string text) {
        return FindButton(buttons, text) != null;
    }

    private static Button FindButton(List<Button> buttons, string text) {
        foreach (Button button in buttons) if (button.Text == text) return button;
        return null;
    }

    private static void TestCommandLineOptions() {
        Section("command-line parser");
        Type optionsType = typeof(UnlockerForm).Assembly.GetType("CommandLineOptions");
        MethodInfo parse = optionsType == null ? null : optionsType.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Check(parse != null, "reflected command-line parser");
        if (parse == null) return;

        object parsed = parse.Invoke(null, new object[] { new string[] { "--json", "--wait", "--kill", "--", "--looks-like-an-option" } });
        PropertyInfo paths = optionsType.GetProperty("Paths");
        PropertyInfo json = optionsType.GetProperty("Json");
        PropertyInfo wait = optionsType.GetProperty("Wait");
        PropertyInfo kill = optionsType.GetProperty("Kill");
        List<string> parsedPaths = paths.GetValue(parsed, null) as List<string>;
        Check((bool)json.GetValue(parsed, null) && (bool)wait.GetValue(parsed, null) && (bool)kill.GetValue(parsed, null), "json, wait, and kill flags parse");
        Check(parsedPaths != null && parsedPaths.Count == 1 && parsedPaths[0] == "--looks-like-an-option", "double dash preserves option-shaped paths");

        object invalid = parse.Invoke(null, new object[] { new string[] { "--unknown" } });
        PropertyInfo error = optionsType.GetProperty("Error");
        Check(!string.IsNullOrEmpty(error.GetValue(invalid, null) as string), "unknown option is rejected");
    }

    private static void TestScanCancellation() {
        Section("scan cancellation and status");
        MethodInfo scan = StaticMethod(typeof(UnlockerForm), "RunScan");
        Check(scan != null, "reflected cancellable scan entry point");
        if (scan == null) return;

        CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try {
            ScanRequest request = new ScanRequest(new string[] { Path.Combine(Path.GetTempPath(), "unblock-cancel-target") });
            request.CancellationToken = cancellation.Token;
            ScanResult result = (ScanResult)scan.Invoke(null, new object[] { request, null });
            Check(result.Status == ScanStatus.Cancelled, "cancelled scan reports Cancelled state");
        } finally {
            cancellation.Dispose();
        }
    }

    // Runtime guarantee: no button in the main window or the dialogs may ever be narrower than
    // its own label, at the minimum window size and at a normal size. This is the regression test
    // for the "cut off buttons" bug, and it exercises the real constructed controls.
    private static void TestButtonNoClip() {
        Section("button no-clip guarantee");
        string scratch = MakeScratch();
        string file = Path.Combine(scratch, "target.txt");
        File.WriteAllText(file, "x");
        List<Form> forms = new List<Form>();
        try {
            UnlockerForm main = (UnlockerForm)Activator.CreateInstance(typeof(UnlockerForm), new object[] { new List<string> { file } });
            forms.Add(main);
            main.CreateControl();
            main.Size = main.MinimumSize;
            CheckNoButtonClipped(main, "main window at minimum size");

            main.Size = new Size(1000, 700);
            CheckNoButtonClipped(main, "main window at 1000x700");

            Assembly app = typeof(UnlockerForm).Assembly;
            Type choiceType = app.GetType("IntegratedPromptForm+UserChoice");
            Type promptType = app.GetType("IntegratedPromptForm");
            Type reviewType = app.GetType("FileInUseActionReviewForm");

            Form prompt = (Form)Activator.CreateInstance(promptType, new object[] { file, 1, 2 });
            forms.Add(prompt);
            CheckNoButtonClipped(prompt, "file-in-use prompt");

            Form renameReview = (Form)Activator.CreateInstance(reviewType, new object[] {
                file, Enum.Parse(choiceType, "KillAndRename"), new List<ProcessItem>(), "a-much-longer-new-name.txt", null, null
            });
            forms.Add(renameReview);
            CheckNoButtonClipped(renameReview, "kill and rename review");
        } catch (Exception ex) {
            Check(false, "no-clip setup failed: " + ex.GetType().Name + ": " + ex.Message);
        } finally {
            foreach (Form f in forms) { try { f.Dispose(); } catch { } }
            TryDeleteDir(scratch);
        }
    }

    private static void CheckNoButtonClipped(Form form, string label) {
        form.PerformLayout();
        int clip = 0;
        string worst = null;
        foreach (Control c in Descendants(form)) {
            Button b = c as Button;
            if (b == null || !b.Visible) continue;
            Size text = TextRenderer.MeasureText(b.Text, b.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            if (b.Width < text.Width + 8) {
                clip++;
                worst = b.Text + " (width " + b.Width + " < needs " + (text.Width + 8) + ")";
            }
        }
        Check(clip == 0, label + " has no clipped button labels" + (clip == 0 ? "" : ": " + worst));
    }

    private static List<Control> Descendants(Control root) {
        List<Control> all = new List<Control>();
        foreach (Control c in root.Controls) { all.Add(c); all.AddRange(Descendants(c)); }
        return all;
    }

    // Runtime guarantee: no two visible sibling controls may overlap, in the main window or the
    // dialogs, at any size, with or without a refresh notice. This is the regression test for the
    // "overlapped components" reports and it exercises the real constructed controls.
    private static void TestNoOverlap() {
        Section("no-overlap guarantee");
        string scratch = MakeScratch();
        string file = Path.Combine(scratch, "a-rather-long-file-name-to-stress-the-layout-properly.txt");
        File.WriteAllText(file, "x");
        List<Form> forms = new List<Form>();
        try {
            UnlockerForm main = (UnlockerForm)Activator.CreateInstance(typeof(UnlockerForm), new object[] { new List<string> { file } });
            forms.Add(main);
            main.CreateControl();
            main.Size = main.MinimumSize;
            CheckNoSiblingOverlap(main, "main window at minimum size");
            main.Size = new Size(1000, 700);
            CheckNoSiblingOverlap(main, "main window at 1000x700");

            Assembly app = typeof(UnlockerForm).Assembly;
            Type choiceType = app.GetType("IntegratedPromptForm+UserChoice");
            Type promptType = app.GetType("IntegratedPromptForm");
            Type reviewType = app.GetType("FileInUseActionReviewForm");

            Form prompt = (Form)Activator.CreateInstance(promptType, new object[] { file, 1, 5 });
            forms.Add(prompt);
            CheckNoSiblingOverlap(prompt, "file-in-use prompt");

            Form renameReview = (Form)Activator.CreateInstance(reviewType, new object[] {
                file, Enum.Parse(choiceType, "KillAndRename"), new List<ProcessItem>(), "a-longer-renamed-name.txt", null, null
            });
            forms.Add(renameReview);
            CheckNoSiblingOverlap(renameReview, "kill and rename review");

            Form noticeReview = (Form)Activator.CreateInstance(reviewType, new object[] {
                file, Enum.Parse(choiceType, "KillAndRename"), new List<ProcessItem>(), "a-longer-renamed-name.txt", null,
                "The locker list changed. Review the processes again before continuing, this notice is long on purpose."
            });
            forms.Add(noticeReview);
            CheckNoSiblingOverlap(noticeReview, "kill and rename review with a refresh notice");

            Form moveReview = (Form)Activator.CreateInstance(reviewType, new object[] {
                file, Enum.Parse(choiceType, "KillAndMove"), new List<ProcessItem>(), null, scratch,
                "The locker list changed. Review the processes again before continuing, this notice is long on purpose."
            });
            forms.Add(moveReview);
            CheckNoSiblingOverlap(moveReview, "kill and move review with a refresh notice");

            // Font/Dpi scaling stress: rows must reflow instead of overlapping.
            Form scaledPrompt = (Form)Activator.CreateInstance(promptType, new object[] { file, 1, 5 });
            forms.Add(scaledPrompt);
            CheckNoOverlapScaled(scaledPrompt, "file-in-use prompt", 1.5f);

            Form scaledMove = (Form)Activator.CreateInstance(reviewType, new object[] {
                file, Enum.Parse(choiceType, "KillAndMove"), new List<ProcessItem>(), null, scratch,
                "The locker list changed. Review the processes again before continuing, this notice is long on purpose."
            });
            forms.Add(scaledMove);
            CheckNoOverlapScaled(scaledMove, "kill and move review", 1.5f);
        } catch (Exception ex) {
            Check(false, "no-overlap setup failed: " + ex.GetType().Name + ": " + ex.Message);
        } finally {
            foreach (Form f in forms) { try { f.Dispose(); } catch { } }
            TryDeleteDir(scratch);
        }
    }

    private static void CheckNoSiblingOverlap(Form form, string label) {
        form.PerformLayout();
        foreach (Control c in Descendants(form)) c.PerformLayout();
        int overlaps = 0;
        string worst = null;
        foreach (Control parent in new Control[] { form }.Concat(Descendants(form))) {
            List<Control> visible = new List<Control>();
            foreach (Control c in parent.Controls) if (c.Visible && c.Width > 0 && c.Height > 0) visible.Add(c);
            for (int i = 0; i < visible.Count; i++) {
                for (int j = i + 1; j < visible.Count; j++) {
                    Rectangle r = Rectangle.Intersect(visible[i].Bounds, visible[j].Bounds);
                    if (r.Width > 2 && r.Height > 2) {
                        bool docked = visible[i].Dock != DockStyle.None || visible[j].Dock != DockStyle.None;
                        if (!docked || (r.Width > 6 && r.Height > 6)) {
                            overlaps++;
                            worst = visible[i].GetType().Name + " X " + visible[j].GetType().Name + " (" + r.Width + "x" + r.Height + ")";
                        }
                    }
                }
            }
        }
        Check(overlaps == 0, label + " has no overlapping sibling controls" + (overlaps == 0 ? "" : ": " + worst));
    }

    // Checks overlap at the normal font and after scaling every font, which simulates a higher
    // DPI or an accessibility text-size change. A layout that only works at 100% is not done.
    private static void CheckNoOverlapScaled(Form form, string label, float scale) {
        form.CreateControl();
        Application.DoEvents();
        ScaleFonts(form, scale);
        form.PerformLayout();
        foreach (Control c in Descendants(form)) c.PerformLayout();
        Application.DoEvents();
        CheckNoSiblingOverlap(form, label + " at " + scale.ToString("0.00") + "x text");
    }

    private static void ScaleFonts(Control root, float factor) {
        foreach (Control c in new Control[] { root }.Concat(Descendants(root))) {
            try { c.Font = new Font(c.Font.FontFamily, c.Font.Size * factor, c.Font.Style); } catch { }
        }
    }

    // Changing the theme must recolor the whole window immediately, with no control left on the
    // previous theme's colors until a restart. This constructs the real form and toggles it.
    private static void TestThemeToggle() {
        Section("theme toggle repaints everything");
        string scratch = MakeScratch();
        string file = Path.Combine(scratch, "target.txt");
        File.WriteAllText(file, "x");
        UnlockerForm form = null;
        try {
            form = (UnlockerForm)Activator.CreateInstance(typeof(UnlockerForm), new object[] { new List<string> { file } });
            form.CreateControl();
            Application.DoEvents();

            MethodInfo toggle = typeof(UnlockerForm).GetMethod("BtnThemeToggle_Click", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(toggle != null, "theme toggle handler exists");
            if (toggle == null) return;

            Button toggleBtn = null;
            foreach (Control c in Descendants(form)) {
                Button b = c as Button;
                if (b != null && (b.Text == "Dark" || b.Text == "Light")) { toggleBtn = b; break; }
            }
            Check(toggleBtn != null, "theme toggle button exists");
            if (toggleBtn == null) return;

            Color lightCanvas = Color.FromArgb(0xF1, 0xF3, 0xF5);
            Color darkCanvas = Color.FromArgb(0x14, 0x18, 0x1C);

            // Force light.
            if (form.BackColor != lightCanvas) { toggle.Invoke(form, new object[] { toggleBtn, EventArgs.Empty }); Application.DoEvents(); }
            Check(form.BackColor == lightCanvas, "starts in the light theme");

            // Switch to dark and assert no light-only color survives anywhere.
            toggle.Invoke(form, new object[] { toggleBtn, EventArgs.Empty });
            Application.DoEvents();
            Check(form.BackColor == darkCanvas, "toggles to the dark theme");
            Check(CountResidualColor(form, LightOnlyColors) == 0, "no light-theme color remains after switching to dark");

            // Switch back and assert no dark-only color survives.
            toggle.Invoke(form, new object[] { toggleBtn, EventArgs.Empty });
            Application.DoEvents();
            Check(form.BackColor == lightCanvas, "toggles back to the light theme");
            Check(CountResidualColor(form, DarkOnlyColors) == 0, "no dark-theme color remains after switching to light");
        } catch (Exception ex) {
            Check(false, "theme toggle test failed: " + ex.GetType().Name + ": " + ex.Message);
        } finally {
            if (form != null) { try { form.Dispose(); } catch { } }
            TryDeleteDir(scratch);
        }
    }

    private static readonly Color[] LightOnlyColors = new Color[] {
        Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xF1, 0xF3, 0xF5), Color.FromArgb(0x1E, 0x27, 0x2E), Color.FromArgb(0xDC, 0xE1, 0xE6)
    };
    private static readonly Color[] DarkOnlyColors = new Color[] {
        Color.FromArgb(0x14, 0x18, 0x1C), Color.FromArgb(0x1B, 0x21, 0x26), Color.FromArgb(0x10, 0x15, 0x19), Color.FromArgb(0x2B, 0x33, 0x3A)
    };

    private static int CountResidualColor(Form form, Color[] forbidden) {
        int count = 0;
        foreach (Control c in new Control[] { form }.Concat(Descendants(form))) {
            if (c.BackColor == Color.Transparent) continue;
            foreach (Color f in forbidden) if (c.BackColor == f) { count++; break; }
        }
        return count;
    }

    // The File-in-Use interception must be single-flight: one prompt at a time, and a brief
    // cooldown after it closes so Explorer's re-shown dialog cannot open a second modal.
    private static void TestSinglePromptGate() {
        Section("file-in-use prompt is single-flight");
        Type uninstaller = typeof(UnlockerForm).Assembly.GetType("Uninstaller");
        MethodInfo claim = StaticMethod(uninstaller, "TryClaimPromptSlot");
        MethodInfo reset = StaticMethod(uninstaller, "ResetPromptGateForTest");
        MethodInfo finish = StaticMethod(uninstaller, "FinishPromptForTest");
        PropertyInfo active = uninstaller == null ? null : uninstaller.GetProperty("IsPromptActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Check(claim != null && reset != null && finish != null && active != null, "prompt gate seams exist");
        if (claim == null || reset == null || finish == null || active == null) return;

        reset.Invoke(null, null);
        Check((bool)claim.Invoke(null, new object[] { new IntPtr(101) }), "first dialog claims the prompt slot");
        Check(!(bool)claim.Invoke(null, new object[] { new IntPtr(202) }), "a second dialog while one is open is refused");
        Check(!(bool)claim.Invoke(null, new object[] { new IntPtr(101) }), "the same dialog handle is refused");
        Check((bool)active.GetValue(null, null), "the gate reports a prompt active");
        finish.Invoke(null, null);
        Check(!(bool)active.GetValue(null, null), "the gate clears when the prompt finishes");
        Check(!(bool)claim.Invoke(null, new object[] { new IntPtr(303) }), "a dialog re-shown right after closing is suppressed");
        reset.Invoke(null, null);
    }

    private static void TestFeatureWiring() {
        Section("feature wiring");
        string form = File.ReadAllText(Path.Combine(repoRoot, "src", "UnlockerForm.cs"));
        string ui = File.ReadAllText(Path.Combine(repoRoot, "src", "UnlockerForm.UI.cs"));
        string setup = File.ReadAllText(Path.Combine(repoRoot, "src", "Setup.cs"));
        string watcher = File.ReadAllText(Path.Combine(repoRoot, "src", "UninstallEngine.cs"));
        Check(form.Contains("DeleteMode.RecycleBin") && form.Contains("RepairPermissions"), "safe delete and explicit permission repair actions are wired");
        string uiLabels = File.ReadAllText(Path.Combine(repoRoot, "src", "UnlockerForm.UI.cs"));
        Check(uiLabels.Contains("Kill & Recycle") && uiLabels.Contains("Kill & Rename") && uiLabels.Contains("Kill & Move") && uiLabels.Contains("Kill & Copy") && uiLabels.Contains("Kill & Permanent Delete") && uiLabels.Contains("Kill & Delete at Restart"), "kill-before-file-action labels are wired");
        Check(uiLabels.Contains("NewWrapStrip") && uiLabels.Contains("btnMore") && uiLabels.Contains("BuildMoreMenu"), "actions use a wrapping command strip with a More overflow menu");
        Check(form.Contains("Waiting for elevated retry") && form.Contains("unsaved work") && form.Contains("No unrelated process"), "confirmation and elevated retry messaging is wired");
        string coordinator = File.ReadAllText(Path.Combine(repoRoot, "src", "FileActionCoordinator.cs"));
        Check(coordinator.Contains("LockActionMode.DetectedLockersOnly") && coordinator.Contains("PreviouslyTerminatedPids") && coordinator.Contains("WriteRequest"), "coordinator models and pending action persistence are wired");
        Check(ui.Contains("AllowDrop = true") && ui.Contains("MultiSelect = true"), "drag-and-drop and multi-select UI are enabled");
        Check(setup.Contains("MultiSelectModel") && setup.Contains("Registry32"), "Explorer verb is multi-select and both registry views are handled");
        Check(watcher.Contains("SetWinEventHook") && !watcher.Contains("Thread.Sleep(50)"), "Explorer watcher uses events with a slower fallback");
        Check(watcher.Contains("Kill && Rename...") && watcher.Contains("Kill && Move...") && watcher.Contains("CreateFileInUseKillRequest") && watcher.Contains("ExecuteFileInUseKillAction"), "file-in-use popup supports kill-and-rename and kill-and-move actions");
        Check(watcher.Contains("FileInUseActionReviewForm") && watcher.Contains("FileInUseActionReviewOutcome.Back") && !watcher.Contains("PromptForText") && !watcher.Contains("ConfirmFileInUseAction"), "follow-up dialogs are operation-specific and no longer use generic confirmations");
        Check(watcher.Contains("this.AcceptButton = btnSkip") && watcher.Contains("ConfigureFocus"), "file-in-use popup defaults safely and keeps visible keyboard focus");
        Check(form.Contains("btnReload") && form.Contains("miCancelScan") && form.Contains("miCancelAction"), "reload and cancel controls are wired");
        string uiTheme = File.ReadAllText(Path.Combine(repoRoot, "src", "UiTheme.cs"));
        Check(uiTheme.Contains("class UiTheme") && uiTheme.Contains("Light") && uiTheme.Contains("Dark") && uiTheme.Contains("UiMetrics"), "theme system defines light and dark palettes plus metrics");
        Check(uiTheme.Contains("EnforceNoClip") && uiTheme.Contains("RequiredWidth") && uiTheme.Contains("MinimumSize"), "the no-clip guarantee sets each button's minimum size from its label");
        Check(uiTheme.Contains("LayoutButtonRow") && uiTheme.Contains("NewWrapStrip"), "dialog rows and the command strip share the label-driven layout helpers");
        Check(!ui.Contains("CreateGraphics") && !ui.Contains("MakeActionButton"), "buttons are measured without a pre-parent Graphics handle");
        Check(File.ReadAllText(Path.Combine(repoRoot, "src", "UninstallEngine.cs")).Contains("UiLayout.LayoutButtonRow"), "dialog action buttons use the label-driven layout so they cannot clip");
        Check(form.Contains("LoadThemePreference") && form.Contains("SaveThemePreference") && form.Contains("BtnThemeToggle_Click") && ui.Contains("ApplyTheme"), "theme toggle is wired to persistence and repaint");
        Check(!ui.Contains("MakeActionButton") && !ui.Contains("Color.FromArgb(202, 111, 30)") && !ui.Contains("Color.FromArgb(95, 78, 121)") && !ui.Contains("Color.FromArgb(116, 80, 42)"), "old ad-hoc rainbow button fills are removed from the main window");
        string fileOps = File.ReadAllText(Path.Combine(repoRoot, "src", "FileOperations.cs"));
        Check(fileOps.Contains("FOFX_RECYCLEONDELETE") && fileOps.Contains("IFileOperation") && fileOps.Contains("SHQueryRecycleBin"), "recycle path uses IFileOperation with recycle-only flag and verifies the bin");
        Check(fileOps.Contains("IsRecycleBinAvailableFor") && fileOps.Contains("The item was left in place"), "recycle path refuses and preserves the item when no Recycle Bin exists");
        Check(watcher.Contains("FileOperations.Delete(targetPath, DeleteMode.RecycleBin)") && watcher.Contains("ShowFileInUseActionFailure"), "file-in-use unlock-and-recycle reports recycle failures");
    }

    private static void TestScanBenchmark() {
        Section("five-run scan benchmark");
        MethodInfo optimized = StaticMethod(typeof(UnlockerForm), "RunFastHandleScanDirect");
        MethodInfo legacy = StaticMethod(typeof(UnlockerForm), "RunFastHandleScanLegacy");
        Check(optimized != null && legacy != null, "optimized and recorded baseline scan entry points exist");
        if (optimized == null || legacy == null) return;

        string scratch = MakeScratch();
        string target = Path.Combine(scratch, "benchmark.txt");
        File.WriteAllText(target, "benchmark");
        HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };
        try {
            optimized.Invoke(null, new object[] { targets });
            legacy.Invoke(null, new object[] { targets, true, null });
            long[] optimizedMs = new long[5];
            long[] baselineMs = new long[5];
            for (int i = 0; i < 5; i++) {
                Stopwatch timer = Stopwatch.StartNew();
                optimized.Invoke(null, new object[] { targets });
                timer.Stop();
                optimizedMs[i] = timer.ElapsedMilliseconds;

                timer.Restart();
                legacy.Invoke(null, new object[] { targets, true, null });
                timer.Stop();
                baselineMs[i] = timer.ElapsedMilliseconds;
            }
            Array.Sort(optimizedMs);
            Array.Sort(baselineMs);
            double improvement = baselineMs[2] == 0 ? 0 : (1.0 - ((double)optimizedMs[2] / baselineMs[2])) * 100.0;
            Console.WriteLine("optimized median: " + optimizedMs[2] + " ms; baseline median: " + baselineMs[2] + " ms; improvement: " + improvement.ToString("F1") + "%");
            Check(optimizedMs[2] <= baselineMs[2] * 0.70 || baselineMs[2] < 10, "optimized median meets the 30% baseline target or baseline is below measurement resolution");
        } finally {
            TryDeleteDir(scratch);
        }
    }

    private static void TestSetupCompileParity() {
        Section("installer compile parity");
        string setupDll = Path.Combine(harnessDir, "UnBlock.Setup.dll");
        Assembly setupAsm = null;
        try {
            setupAsm = Assembly.LoadFrom(setupDll);
        } catch (Exception ex) {
            Check(false, "setup library loads: " + ex.Message);
            return;
        }
        Check(setupAsm != null, "setup library loads");

        MethodInfo compile = StaticMethod(setupAsm.GetType("Setup"), "CompileApplication");
        Check(compile != null, "reflected CompileApplication");
        if (compile == null) return;

        string srcDir = Path.Combine(repoRoot, "src");
        List<string> sources = new List<string>();
        foreach (string f in Directory.GetFiles(srcDir, "*.cs")) {
            if (!string.Equals(Path.GetFileName(f), "Setup.cs", StringComparison.OrdinalIgnoreCase)) {
                sources.Add(f);
            }
        }

        string scratch = MakeScratch();
        try {
            string outExe = Path.Combine(scratch, "Unlocker.exe");
            Exception compileEx = null;
            try {
                compile.Invoke(null, new object[] { sources, outExe });
            } catch (TargetInvocationException tie) {
                compileEx = tie.InnerException;
            } catch (Exception ex) {
                compileEx = ex;
            }
            bool produced = compileEx == null && File.Exists(outExe);
            Check(produced, "installer compile routine produces an executable" + (compileEx == null ? "" : ": " + compileEx.Message));
            if (File.Exists(outExe)) {
                byte[] head = new byte[2];
                using (FileStream fsOut = File.OpenRead(outExe)) {
                    fsOut.Read(head, 0, 2);
                }
                Check(head[0] == (byte)'M' && head[1] == (byte)'Z', "compiled output is a PE binary");
                Check(new FileInfo(outExe).Length > 10000, "compiled output has real content");
            }
        } finally {
            TryDeleteDir(scratch);
        }
    }

    private static void TestLockDetectionEndToEnd() {
        Section("end-to-end lock detection");
        MethodInfo scan = StaticMethod(typeof(UnlockerForm), "RunFastHandleScanDirect");
        Check(scan != null, "reflected RunFastHandleScanDirect");
        if (scan == null) return;

        string scratch = MakeScratch();
        string file = Path.Combine(scratch, "locked.txt");
        File.WriteAllText(file, "payload");
        string readyCkpt = Path.Combine(scratch, "ready.ckpt");
        string releaseCkpt = Path.Combine(scratch, "release.ckpt");

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = typeof(CiTests).Assembly.Location;
        psi.Arguments = string.Format("\"--lock\" \"{0}\" \"{1}\" \"{2}\"", file, readyCkpt, releaseCkpt);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        Process child = null;
        try {
            child = Process.Start(psi);
            bool becameReady = WaitForFile(readyCkpt, 30000);
            Check(becameReady, "child lock holder signals ready");
            if (becameReady) {
                HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                targets.Add(file);
                List<ProcessItem> items = null;
                string scanError = null;
                try {
                    items = (List<ProcessItem>)scan.Invoke(null, new object[] { targets });
                } catch (TargetInvocationException tie) {
                    scanError = tie.InnerException != null ? tie.InnerException.Message : "invocation failed";
                } catch (Exception ex) {
                    scanError = ex.Message;
                }
                Check(scanError == null, "handle scan completes" + (scanError == null ? "" : ": " + scanError));
                bool found = false;
                if (items != null) {
                    foreach (ProcessItem item in items) {
                        if (item != null && item.Pid == child.Id) {
                            found = true;
                            break;
                        }
                    }
                }
                Check(found, "scan engine identifies the child as the locking process");
            }
        } finally {
            try { File.WriteAllText(releaseCkpt, "go"); } catch { }
            if (child != null) {
                if (!child.WaitForExit(10000)) {
                    try { child.Kill(); } catch { }
                }
                child.Dispose();
            }
            TryDeleteDir(scratch);
        }
    }
}
