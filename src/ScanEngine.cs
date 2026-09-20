using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public partial class UnlockerForm {
    private sealed class ScanDeadline {
        public readonly CancellationToken Token;
        public readonly DateTime DeadlineUtc;
        public bool Cancelled;
        public bool TimedOut;
        public bool Partial;

        public ScanDeadline(CancellationToken token, TimeSpan timeout) {
            Token = token;
            DeadlineUtc = timeout <= TimeSpan.Zero ? DateTime.MaxValue : DateTime.UtcNow.Add(timeout);
        }

        public bool ShouldStop() {
            if (Token.IsCancellationRequested) {
                Cancelled = true;
                return true;
            }
            if (DateTime.UtcNow >= DeadlineUtc) {
                TimedOut = true;
                return true;
            }
            return false;
        }
    }

    internal static ScanResult RunScan(ScanRequest request, Action<int> progressCallback) {
        if (request == null) request = new ScanRequest(null);

        ScanResult result = new ScanResult();
        ScanDeadline deadline = new ScanDeadline(request.CancellationToken, request.Deadline);
        try {
            result.Processes = RunFastHandleScan(request.Targets, request.ForceRefresh, request.IncludeModules, progressCallback, deadline);
            if (deadline.Cancelled) result.Status = ScanStatus.Cancelled;
            else if (deadline.TimedOut) result.Status = ScanStatus.TimedOut;
            else if (deadline.Partial) result.Status = ScanStatus.Partial;
            else result.Status = ScanStatus.Completed;
        } catch (Exception ex) {
            result.Status = ScanStatus.Failed;
            result.ErrorMessage = ex.Message;
        }
        result.FinishedUtc = DateTime.UtcNow;
        return result;
    }

    internal static List<ProcessItem> RunFastHandleScan(HashSet<string> targets, bool forceRefresh, Action<int> progressCallback) {
        ScanRequest request = new ScanRequest(targets);
        request.ForceRefresh = forceRefresh;
        return RunScan(request, progressCallback).Processes;
    }

    internal static List<ProcessItem> RunFastHandleScan(HashSet<string> targets, bool forceRefresh, Action<int> progressCallback, CancellationToken token) {
        ScanRequest request = new ScanRequest(targets);
        request.ForceRefresh = forceRefresh;
        request.CancellationToken = token;
        return RunScan(request, progressCallback).Processes;
    }

    private static List<ProcessItem> RunFastHandleScan(HashSet<string> targets, bool forceRefresh, bool includeModules, Action<int> progressCallback, ScanDeadline deadline) {
        var finalLockingProcesses = new Dictionary<int, ProcessItem>();
        var addedPids = new HashSet<int>();
        var targetList = new List<TargetMatchInfo>();

        InitFileTypeIndex();
        if (deadline.ShouldStop()) return new List<ProcessItem>();
        if (progressCallback != null) progressCallback(5);
        RefreshProcessSnapshot(forceRefresh);
        if (progressCallback != null) progressCallback(10);

        if (targets != null) {
            foreach (string rawTarget in targets) {
                if (deadline.ShouldStop()) break;
                TargetMatchInfo targetInfo = NormalizeTarget(rawTarget);
                if (targetInfo != null) targetList.Add(targetInfo);
            }
        }
        if (targetList.Count == 0) return new List<ProcessItem>();

        var pathLockCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool allTargetsAreFiles = true;
        foreach (TargetMatchInfo info in targetList) {
            if (deadline.ShouldStop()) break;
            if (!info.IsDir) {
                string probeKey = info.OriginalPath.TrimEnd('\\', '/');
                pathLockCache[probeKey] = IsPathStrictlyLocked(probeKey);
            } else {
                allTargetsAreFiles = false;
            }
        }

        lock (CacheLock) {
            foreach (KeyValuePair<int, string> entry in ProcessPathMap) {
                if (deadline.ShouldStop()) break;
                int pid = entry.Key;
                string processPath = entry.Value;
                if (string.IsNullOrEmpty(processPath)) continue;
                foreach (TargetMatchInfo info in targetList) {
                    if (!MatchesDosPath(processPath, info) || !addedPids.Add(pid)) continue;
                    ProcessItem item = CreateProcessItem(pid, processPath, info.IsDir);
                    item.IsModuleLock = true;
                    item.Matches.Add(new LockMatch {
                        TargetPath = info.OriginalPath,
                        LockedPath = processPath,
                        AccessDescription = "Process executable path",
                        Severity = Severity.High,
                        Source = LockSource.Module,
                        CanUnlock = false
                    });
                    finalLockingProcesses[pid] = item;
                    break;
                }
            }
        }
        if (progressCallback != null) progressCallback(20);

        if (includeModules && ShouldScanModules(targetList)) {
            List<int> activePids;
            lock (CacheLock) activePids = new List<int>(ProcessNameMap.Keys);
            object processLock = new object();
            var moduleCache = new ConcurrentDictionary<int, List<string>>();
            int moduleWorkers = Math.Max(2, Math.Min(16, Environment.ProcessorCount * 2));
            Parallel.ForEach(activePids, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = moduleWorkers }, delegate(int pid) {
                if (deadline.ShouldStop() || pid <= 4 || pid == Process.GetCurrentProcess().Id) return;
                List<string> modules;
                if (!moduleCache.TryGetValue(pid, out modules)) {
                    modules = GetProcessModules(pid);
                    moduleCache.TryAdd(pid, modules);
                }
                foreach (string modulePath in modules) {
                    if (deadline.ShouldStop()) return;
                    foreach (TargetMatchInfo info in targetList) {
                        if (!MatchesDeviceOrDosPath(modulePath, info)) continue;
                        lock (processLock) {
                            ProcessItem item;
                            if (!finalLockingProcesses.TryGetValue(pid, out item)) {
                                item = CreateProcessItem(pid, GetProcessPath(pid) ?? "Unknown System Component", info.IsDir);
                                finalLockingProcesses[pid] = item;
                            }
                            item.IsModuleLock = true;
                            AddMatch(item, new LockMatch {
                                TargetPath = info.OriginalPath,
                                LockedPath = modulePath,
                                AccessDescription = info.IsDir ? "Loaded module from target directory" : "Loaded DLL or mapped module",
                                Severity = Severity.High,
                                Source = LockSource.Module,
                                CanUnlock = false
                            });
                        }
                        break;
                    }
                }
            });
        }
        if (progressCallback != null) progressCallback(45);

        bool anyTargetStrictlyLocked = false;
        foreach (KeyValuePair<string, bool> probe in pathLockCache) {
            if (probe.Value) { anyTargetStrictlyLocked = true; break; }
        }
        if (allTargetsAreFiles && !anyTargetStrictlyLocked) {
            if (progressCallback != null) progressCallback(100);
            return SortProcesses(finalLockingProcesses);
        }

        IntPtr buffer = IntPtr.Zero;
        try {
            int bufferSize = 0x10000;
            buffer = Marshal.AllocHGlobal(bufferSize);
            int length = 0;
            int status;
            while ((status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, ref length)) == unchecked((int)0xC0000004)) {
                if (deadline.ShouldStop()) return SortProcesses(finalLockingProcesses);
                bufferSize = Math.Max(bufferSize * 2, length + 0x10000);
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(bufferSize);
            }
            if (status != 0) {
                deadline.Partial = true;
                return SortProcesses(finalLockingProcesses);
            }
            if (progressCallback != null) progressCallback(55);

            bool is64Bit = Marshal.SizeOf(typeof(IntPtr)) == 8;
            long handleCount = is64Bit ? Marshal.ReadInt64(buffer) : Marshal.ReadInt32(buffer);
            IntPtr ptr = new IntPtr(buffer.ToInt64() + (is64Bit ? 16 : 8));
            int entrySize = is64Bit ? 40 : 28;
            int currentPid = Process.GetCurrentProcess().Id;
            HashSet<int> livePids;
            lock (CacheLock) livePids = new HashSet<int>(ProcessNameMap.Keys);
            var handlesByPid = new Dictionary<int, List<HandleInfo>>();

            for (long i = 0; i < handleCount; i++) {
                if (deadline.ShouldStop()) break;
                int pid = is64Bit ? (int)Marshal.ReadInt64(ptr, 8) : Marshal.ReadInt32(ptr, 4);
                ushort objectType = (ushort)Marshal.ReadInt16(ptr, is64Bit ? 30 : 18);
                if (pid != currentPid && pid > 0 && livePids.Contains(pid) && (CachedFileTypeIndex == 0 || objectType == CachedFileTypeIndex)) {
                    IntPtr handleValue = is64Bit ? Marshal.ReadIntPtr(ptr, 16) : Marshal.ReadIntPtr(ptr, 8);
                    List<HandleInfo> processHandles;
                    if (!handlesByPid.TryGetValue(pid, out processHandles)) {
                        processHandles = new List<HandleInfo>();
                        handlesByPid[pid] = processHandles;
                    }
                    processHandles.Add(new HandleInfo {
                        HandleValue = handleValue,
                        ObjectTypeIndex = objectType,
                        GrantedAccess = (uint)Marshal.ReadInt32(ptr, is64Bit ? 24 : 12)
                    });
                }
                ptr = new IntPtr(ptr.ToInt64() + entrySize);
            }

            var work = new ConcurrentQueue<KeyValuePair<int, HandleInfo>>();
            foreach (KeyValuePair<int, List<HandleInfo>> entry in handlesByPid) {
                foreach (HandleInfo handle in entry.Value) work.Enqueue(new KeyValuePair<int, HandleInfo>(entry.Key, handle));
            }
            if (progressCallback != null) progressCallback(65);

            var sourceHandles = new Dictionary<int, IntPtr>();
            object sourceLock = new object();
            object resultLock = new object();
            Func<int, IntPtr> openSource = delegate(int pid) {
                lock (sourceLock) {
                    IntPtr source;
                    if (sourceHandles.TryGetValue(pid, out source)) return source;
                    source = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                    sourceHandles[pid] = source;
                    return source;
                }
            };

            int total = work.Count;
            int processed = 0;
            IntPtr currentProcessHandle = GetCurrentProcess();
            int workerCount = Math.Max(2, Math.Min(16, Environment.ProcessorCount * 2));
            var workers = new List<Thread>();
            for (int workerIndex = 0; workerIndex < workerCount; workerIndex++) {
                Thread worker = new Thread(delegate() {
                    KeyValuePair<int, HandleInfo> pair;
                    while (!deadline.ShouldStop() && work.TryDequeue(out pair)) {
                        ProcessOneHandle(pair.Key, pair.Value, targetList, pathLockCache, finalLockingProcesses, resultLock, openSource, currentProcessHandle, deadline);
                        int done = Interlocked.Increment(ref processed);
                        if (done % 25 == 0 && progressCallback != null) {
                            progressCallback(total == 0 ? 65 : 65 + (int)((done / (float)total) * 30));
                        }
                    }
                });
                worker.IsBackground = true;
                workers.Add(worker);
                worker.Start();
            }

            foreach (Thread worker in workers) worker.Join();
            lock (sourceLock) {
                foreach (IntPtr source in sourceHandles.Values) {
                    if (source != IntPtr.Zero) CloseHandle(source);
                }
                sourceHandles.Clear();
            }
            if (progressCallback != null) progressCallback(100);
            return SortProcesses(finalLockingProcesses);
        } finally {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static TargetMatchInfo NormalizeTarget(string rawTarget) {
        if (string.IsNullOrEmpty(rawTarget)) return null;
        try {
            string raw = rawTarget.Trim().Trim('"');
            bool isDevicePath = raw.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase);
            string full = isDevicePath ? raw : Path.GetFullPath(raw);
            bool isDir = Directory.Exists(full);
            string normalized = isDir ? full.TrimEnd('\\', '/') + "\\" : full;
            bool isNetwork = normalized.StartsWith("\\\\", StringComparison.OrdinalIgnoreCase);
            string networkSearchPath = isNetwork ? normalized.Substring(2).TrimEnd('\\', '/') : null;
            string driveLetter = Path.GetPathRoot(full);
            string devicePath = normalized;
            if (!isDevicePath && !isNetwork && !string.IsNullOrEmpty(driveLetter)) {
                StringBuilder deviceRoot = new StringBuilder(512);
                string drive = driveLetter.TrimEnd('\\', '/');
                if (QueryDosDevice(drive, deviceRoot, deviceRoot.Capacity) != 0) devicePath = normalized.Replace(drive, deviceRoot.ToString());
            }
            return new TargetMatchInfo {
                OriginalPath = full,
                NormalizedPath = normalized,
                IsDir = isDir,
                IsNetwork = isNetwork,
                networkSearchPath = networkSearchPath,
                TargetDevicePath = devicePath.TrimEnd('\\', '/'),
                DevicePathWithSlash = devicePath.EndsWith("\\") ? devicePath : devicePath + "\\"
            };
        } catch {
            return null;
        }
    }

    private static bool ShouldScanModules(List<TargetMatchInfo> targets) {
        foreach (TargetMatchInfo target in targets) {
            if (target.IsDir) return true;
            string extension = Path.GetExtension(target.OriginalPath);
            if (string.IsNullOrEmpty(extension)) return true;
            switch (extension.ToLowerInvariant()) {
                case ".dll":
                case ".exe":
                case ".sys":
                case ".ocx":
                case ".cpl":
                case ".scr":
                case ".ax":
                case ".drv":
                    return true;
            }
        }
        return false;
    }

    private static ProcessItem CreateProcessItem(int pid, string path, bool isDir) {
        ProcessItem item = new ProcessItem {
            Pid = pid,
            Name = GetProcessName(pid),
            Path = path,
            GrantedAccess = 0,
            IsDir = isDir,
            ParentPid = GetParentPid(pid)
        };
        return item;
    }

    private static void AddMatch(ProcessItem item, LockMatch match) {
        foreach (LockMatch existing in item.Matches) {
            if (string.Equals(existing.LockedPath, match.LockedPath, StringComparison.OrdinalIgnoreCase) && existing.Source == match.Source) return;
        }
        item.Matches.Add(match);
    }

    private static List<ProcessItem> SortProcesses(Dictionary<int, ProcessItem> processes) {
        var result = new List<ProcessItem>(processes.Values);
        result.Sort(delegate(ProcessItem left, ProcessItem right) {
            Severity leftSeverity = GetSeverity(GetAccessInfo(left));
            Severity rightSeverity = GetSeverity(GetAccessInfo(right));
            int severity = rightSeverity.CompareTo(leftSeverity);
            if (severity != 0) return severity;
            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    private static void ProcessOneHandle(int pid, HandleInfo handleInfo, List<TargetMatchInfo> targets,
        ConcurrentDictionary<string, bool> pathLockCache, Dictionary<int, ProcessItem> results,
        object resultLock, Func<int, IntPtr> openSource, IntPtr currentProcessHandle, ScanDeadline deadline) {
        if (deadline.ShouldStop()) return;
        IntPtr sourceProcess = openSource(pid);
        if (sourceProcess == IntPtr.Zero) return;
        IntPtr duplicate = IntPtr.Zero;
        if (!DuplicateHandle(sourceProcess, handleInfo.HandleValue, currentProcessHandle, out duplicate, 0, false, DUPLICATE_SAME_ACCESS)) return;
        try {
            if (GetFileType(duplicate) != FILE_TYPE_DISK) return;
            string objectName = GetObjectNameInternal(duplicate);
            if (string.IsNullOrEmpty(objectName)) return;
            foreach (TargetMatchInfo info in targets) {
                if (deadline.ShouldStop()) return;
                string normalizedObject = objectName.Replace('/', '\\');
                string relative = null;
                bool match = false;
                if (info.IsNetwork) {
                    int tailIndex = FindNetworkTailIndex(normalizedObject);
                    if (tailIndex >= 0) {
                        string tail = normalizedObject.Substring(tailIndex).TrimEnd('\\', '/');
                        match = info.IsDir ? tail.Equals(info.networkSearchPath, StringComparison.OrdinalIgnoreCase) || tail.StartsWith(info.networkSearchPath + "\\", StringComparison.OrdinalIgnoreCase) : tail.Equals(info.networkSearchPath, StringComparison.OrdinalIgnoreCase);
                        if (match && tail.Length > info.networkSearchPath.Length) relative = tail.Substring(info.networkSearchPath.Length).TrimStart('\\', '/');
                    }
                } else if (normalizedObject.StartsWith(info.DevicePathWithSlash, StringComparison.OrdinalIgnoreCase)) {
                    match = true;
                    relative = normalizedObject.Substring(info.DevicePathWithSlash.Length).TrimStart('\\', '/');
                } else if (normalizedObject.Equals(info.TargetDevicePath, StringComparison.OrdinalIgnoreCase)) {
                    match = true;
                }
                if (!match) continue;

                string dosPath = string.IsNullOrEmpty(relative) ? info.OriginalPath.TrimEnd('\\', '/') : info.OriginalPath.TrimEnd('\\', '/') + "\\" + relative;
                bool strictlyLocked = pathLockCache.GetOrAdd(dosPath, delegate(string path) { return IsPathStrictlyLocked(path); });
                if (!strictlyLocked) continue;

                lock (resultLock) {
                    ProcessItem item;
                    if (!results.TryGetValue(pid, out item)) {
                        item = CreateProcessItem(pid, GetProcessPath(pid) ?? "Unknown System Component", info.IsDir);
                        results[pid] = item;
                    }
                    if (handleInfo.GrantedAccess > item.GrantedAccess) item.GrantedAccess = handleInfo.GrantedAccess;
                    if (!item.Handles.Contains(handleInfo.HandleValue)) item.Handles.Add(handleInfo.HandleValue);
                    string access = GetAccessInfo(item);
                    AddMatch(item, new LockMatch {
                        TargetPath = info.OriginalPath,
                        LockedPath = dosPath,
                        GrantedAccess = handleInfo.GrantedAccess,
                        AccessDescription = access,
                        Severity = GetSeverity(access),
                        Source = LockSource.Handle,
                        CanUnlock = true
                    });
                }
                break;
            }
        } catch { }
        finally {
            CloseHandle(duplicate);
        }
    }
}
