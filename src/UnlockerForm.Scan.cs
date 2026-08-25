using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Partial form part: the lock detection engine (handle table scan,
// module enumeration, path matching, strict-lock probing, snapshots).
public partial class UnlockerForm {

    internal static void InitFileTypeIndex() {
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
}
