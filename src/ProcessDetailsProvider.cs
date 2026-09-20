using System;
using System.Diagnostics;
using System.Management;
using System.Security.Principal;

public partial class UnlockerForm {
    internal static ProcessDetails GetProcessDetails(int pid, bool includeCommandLine) {
        ProcessDetails details = new ProcessDetails {
            Pid = pid,
            Name = GetProcessName(pid),
            ExecutablePath = GetProcessPath(pid),
            ParentPid = GetParentPid(pid),
            Availability = "Available"
        };

        if (pid == 4) {
            details.Account = "NT AUTHORITY\\SYSTEM";
            details.Availability = "Kernel process details are limited by Windows.";
            return details;
        }

        IntPtr processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (processHandle == IntPtr.Zero) {
            details.Availability = "The process is protected or has exited.";
            return details;
        }

        IntPtr token = IntPtr.Zero;
        try {
            bool wow64;
            if (IsWow64Process(processHandle, out wow64)) details.IsWow64 = wow64;

            if (OpenProcessToken(processHandle, 0x0008, out token)) {
                try {
                    using (WindowsIdentity identity = new WindowsIdentity(token)) {
                        details.Account = identity.Name;
                    }
                } catch { }
                token = IntPtr.Zero;
            }
        } catch { }
        finally {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(processHandle);
        }

        try {
            using (Process process = Process.GetProcessById(pid)) {
                try { details.StartTimeUtc = process.StartTime.ToUniversalTime(); } catch { }
            }
        } catch { }

        if (includeCommandLine) details.CommandLine = QueryCommandLine(pid);
        return details;
    }

    private static int GetParentPid(int pid) {
        lock (CacheLock) {
            int parent;
            return ProcessParentMap.TryGetValue(pid, out parent) ? parent : 0;
        }
    }

    private static string QueryCommandLine(int pid) {
        try {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid)) {
                foreach (ManagementObject process in searcher.Get()) {
                    using (process) {
                        object value = process["CommandLine"];
                        return value == null ? null : value.ToString();
                    }
                }
            }
        } catch { }
        return null;
    }
}
