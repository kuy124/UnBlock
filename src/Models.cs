using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public enum Severity {
    Low,      // Benign / Green
    Medium,   // Active Read / Orange
    High      // Severe Write/Delete Lockout / Red
}

// --- Privilege Adjustment Structures ---
    // --- Privilege Adjustment Constants ---
    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID {
        public uint LowPart;
        public int HighPart;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
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
