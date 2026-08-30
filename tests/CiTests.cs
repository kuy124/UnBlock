using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

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
            if (Directory.Exists(srcDir) && Directory.GetFiles(srcDir, "*.cs").Length >= 8) return dir.FullName;
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
            "Models.cs", "Program.cs", "UninstallEngine.cs",
            "UnlockerForm.cs", "UnlockerForm.Native.cs", "UnlockerForm.Scan.cs", "UnlockerForm.UI.cs"
        };
        string[] actual = Directory.GetFiles(srcDir, "*.cs")
            .Select(Path.GetFileName)
            .Where(n => !string.Equals(n, "Setup.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Check(expected.Length == actual.Length && expected.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(actual),
            "all seven app modules are picked up exactly once");
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
        MethodInfo m = StaticMethod(typeof(UnlockerForm), "AttemptDeleteDirect");
        Check(m != null, "reflected AttemptDeleteDirect");
        if (m == null) return;

        string scratch = MakeScratch();
        try {
            string file = Path.Combine(scratch, "file.txt");
            File.WriteAllText(file, "x");
            object[] a = new object[] { file, 0, null };
            Check((bool)m.Invoke(null, a), "temp file deleted");
            Check((int)a[1] == 0, "no win32 error reported");
            Check(!File.Exists(file), "temp file gone from disk");

            Check((bool)m.Invoke(null, new object[] { Path.Combine(scratch, "ghost.txt"), 0, null }), "already-missing path counts as deleted");

            string dir = Path.Combine(scratch, "tree");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "child.txt"), "y");
            Check((bool)m.Invoke(null, new object[] { dir, 0, null }), "non-empty directory removed recursively");
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
            Check(!(bool)k.Invoke(null, new object[] { 0x7FFFFFFF, "no-such-process" }), "killing an unknown pid fails quietly");
        }

        MethodInfo u = StaticMethod(typeof(UnlockerForm), "UnlockSafelyDirect");
        Check(u != null, "reflected UnlockSafelyDirect");
        if (u != null) {
            Check((bool)u.Invoke(null, new object[] { 0x7FFFFFFF, null, "no-such-process" }), "unlock with no handles is a no-op success");
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