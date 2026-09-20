using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization;
using System.Threading;

public enum DeleteMode {
    RecycleBin,
    Permanent,
    OnRestart
}

public enum FileOperationKind {
    Delete,
    Rename,
    Move,
    Copy,
    RepairPermissions
}

 [DataContract]
public class FileOperationRequest {
    [DataMember(Order = 0)]
    public FileOperationKind Kind { get; set; }
    [DataMember(Order = 1)]
    public DeleteMode DeleteMode { get; set; }
    [DataMember(Order = 2)]
    public string SourcePath { get; set; }
    [DataMember(Order = 3)]
    public string DestinationPath { get; set; }
    [DataMember(Order = 4)]
    public bool ReplaceExisting { get; set; }
}

public class FileOperationResult {
    public bool Success { get; set; }
    public bool AlreadyGone { get; set; }
    public bool Scheduled { get; set; }
    public int ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public string CompletedPath { get; set; }

    public static FileOperationResult Ok(string path) {
        return new FileOperationResult { Success = true, CompletedPath = path };
    }

    public static FileOperationResult Fail(int code, string message) {
        return new FileOperationResult { Success = false, ErrorCode = code, ErrorMessage = message };
    }
}

public static class FileOperations {
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;
    private const uint FO_DELETE = 0x00000003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint FOFX_EARLYFAILURE = 0x00100000;

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const uint RPC_E_CHANGED_MODE = 0x80010106;
    private const uint DRIVE_REMOTE = 4;
    private const uint DRIVE_REMOVABLE = 2;

    private static readonly Guid CLSID_FileOperation = new Guid("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IID_IFileOperation = new Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPath);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IFileOperation ppv);

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation {
        void Advise(IntPtr pfops, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems(IntPtr punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, IntPtr pfopsItem);
        void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
        void DeleteItems(IntPtr punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IntPtr pfopsItem);
        void PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    public static FileOperationResult Validate(FileOperationRequest request) {
        if (request == null || string.IsNullOrEmpty(request.SourcePath)) {
            return FileOperationResult.Fail(87, "A source path is required.");
        }

        if (request.Kind == FileOperationKind.Delete || request.Kind == FileOperationKind.RepairPermissions) {
            return FileOperationResult.Ok(request.SourcePath);
        }
        if (!File.Exists(request.SourcePath) && !Directory.Exists(request.SourcePath)) {
            return FileOperationResult.Fail(2, "The source path no longer exists.");
        }

        if (request.Kind == FileOperationKind.Rename) {
            if (string.IsNullOrEmpty(request.DestinationPath) || request.DestinationPath.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                request.DestinationPath.IndexOf('\\') >= 0 || request.DestinationPath.IndexOf('/') >= 0 ||
                request.DestinationPath == "." || request.DestinationPath == "..") {
                return FileOperationResult.Fail(123, "The new name is not valid.");
            }
            string parent = Path.GetDirectoryName(request.SourcePath);
            if (string.IsNullOrEmpty(parent)) return FileOperationResult.Fail(161, "The source has no parent directory.");
            string destination = Path.Combine(parent, request.DestinationPath);
            if (!request.ReplaceExisting && (File.Exists(destination) || Directory.Exists(destination))) return FileOperationResult.Fail(183, "The destination already exists.");
            return FileOperationResult.Ok(destination);
        }

        if (request.Kind == FileOperationKind.Move || request.Kind == FileOperationKind.Copy) {
            if (string.IsNullOrEmpty(request.DestinationPath)) return FileOperationResult.Fail(87, "A destination is required.");
            string destination = request.DestinationPath;
            string finalPath = Directory.Exists(destination) ? Path.Combine(destination, Path.GetFileName(request.SourcePath.TrimEnd('\\', '/'))) : destination;
            if (!request.ReplaceExisting && (File.Exists(finalPath) || Directory.Exists(finalPath))) return FileOperationResult.Fail(183, "The destination already exists.");
            if (string.Equals(Path.GetFullPath(request.SourcePath).TrimEnd('\\', '/'), Path.GetFullPath(finalPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return FileOperationResult.Fail(183, "The source and destination are the same.");
            if (Directory.Exists(request.SourcePath) && IsPathInside(finalPath, request.SourcePath)) return FileOperationResult.Fail(87, "A folder cannot be moved or copied into itself.");
        }
        return FileOperationResult.Ok(request.SourcePath);
    }

    private static bool IsPathInside(string candidate, string parent) {
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd('\\', '/');
        string normalizedParent = Path.GetFullPath(parent).TrimEnd('\\', '/');
        return normalizedCandidate.StartsWith(normalizedParent + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static FileOperationResult Execute(FileOperationRequest request, CancellationToken cancellationToken, Action<int> progress) {
        try {
            FileOperationResult validation = Validate(request);
            if (!validation.Success) return validation;
            if (cancellationToken.IsCancellationRequested) return FileOperationResult.Fail(995, "Operation cancelled.");
            switch (request.Kind) {
                case FileOperationKind.Delete:
                    return Delete(request.SourcePath, request.DeleteMode);
                case FileOperationKind.Rename:
                    return RenameCore(request.SourcePath, request.DestinationPath, request.ReplaceExisting);
                case FileOperationKind.Move:
                    return Move(request.SourcePath, request.DestinationPath, request.ReplaceExisting, cancellationToken, progress);
                case FileOperationKind.Copy:
                    return Copy(request.SourcePath, request.DestinationPath, request.ReplaceExisting, cancellationToken, progress);
                case FileOperationKind.RepairPermissions:
                    return RepairPermissions(request.SourcePath);
                default:
                    return FileOperationResult.Fail(87, "Unsupported file operation.");
            }
        } catch (OperationCanceledException) {
            return FileOperationResult.Fail(995, "Operation cancelled.");
        } catch (Exception ex) {
            return FileOperationResult.Fail(Marshal.GetHRForException(ex) & 0xFFFF, ex.Message);
        }
    }

    public static FileOperationResult Delete(string path, DeleteMode mode) {
        if (!File.Exists(path) && !Directory.Exists(path)) {
            return new FileOperationResult { Success = true, AlreadyGone = true, CompletedPath = path };
        }

        if (mode == DeleteMode.RecycleBin) return DeleteToRecycleBin(path);
        if (mode == DeleteMode.OnRestart) {
            if (MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)) {
                return new FileOperationResult { Success = true, Scheduled = true, CompletedPath = path };
            }
            return FileOperationResult.Fail(Marshal.GetLastWin32Error(), "Windows could not schedule this path for deletion at restart.");
        }

        try {
            if (File.Exists(path)) {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            } else {
                Directory.Delete(path, true);
            }
            return FileOperationResult.Ok(path);
        } catch (Exception ex) {
            return FileOperationResult.Fail(Marshal.GetHRForException(ex) & 0xFFFF, ex.Message);
        }
    }

    public static FileOperationResult DeleteToRecycleBin(string path) {
        bool exists = File.Exists(path) || Directory.Exists(path);
        if (!exists) return FileOperationResult.Ok(path);

        string root = GetDriveRoot(path);
        if (!IsRecycleBinAvailableFor(path)) {
            return FileOperationResult.Fail(0x32, "Windows cannot move \"" + path + "\" to the Recycle Bin because the location does not have one (for example a removable, network, or cloud-only drive). The item was left in place.");
        }

        long itemsBefore = RecycleBinItemCount(root);
        FileOperationResult shellResult = TryRecycleWithShell(path, root, itemsBefore);
        if (shellResult != null) return shellResult;

        return TryRecycleWithLegacyShell(path, root, itemsBefore);
    }

    private static FileOperationResult TryRecycleWithShell(string path, string root, long itemsBefore) {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        bool needsUninitialize = hr == 0;
        if (hr != 0 && unchecked((uint)hr) != RPC_E_CHANGED_MODE) return null;
        try {
            Guid clsid = CLSID_FileOperation;
            Guid iid = IID_IFileOperation;
            IFileOperation op;
            int createHr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out op);
            if (createHr != 0 || op == null) return null;
            try {
                op.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE);
                Guid itemIid = IID_IShellItem;
                IShellItem item;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemIid, out item);
                if (item == null) return null;
                op.DeleteItem(item, IntPtr.Zero);
                op.PerformOperations();
                bool aborted;
                op.GetAnyOperationsAborted(out aborted);
                return VerifyRecycled(path, root, itemsBefore, aborted);
            } finally {
                Marshal.ReleaseComObject(op);
            }
        } catch {
            return null;
        } finally {
            if (needsUninitialize) CoUninitialize();
        }
    }

    private static FileOperationResult TryRecycleWithLegacyShell(string path, string root, long itemsBefore) {
        string from = path + "\0\0";
        SHFILEOPSTRUCT op = new SHFILEOPSTRUCT {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT)
        };
        int result = SHFileOperation(ref op);
        if (result != 0) return VerifyRecycled(path, root, itemsBefore, true);
        return VerifyRecycled(path, root, itemsBefore, op.fAnyOperationsAborted);
    }

    // FOF_ALLOWUNDO only requests the bin; Windows silently deletes for good when a drive has no bin or the item exceeds the size cap.
    // Confirm the item actually left the source and the bin gained an entry, otherwise keep it and report a failure.
    private static FileOperationResult VerifyRecycled(string path, string root, long itemsBefore, bool aborted) {
        bool stillExists = File.Exists(path) || Directory.Exists(path);
        if (stillExists) {
            return FileOperationResult.Fail(0x32, "Windows could not move \"" + path + "\" to the Recycle Bin. The item was left in place.");
        }

        long itemsAfter = RecycleBinItemCount(root);
        if (itemsBefore >= 0 && itemsAfter >= 0) {
            if (itemsAfter <= itemsBefore) {
                return FileOperationResult.Fail(0x32, "Windows removed \"" + path + "\" but the item did not appear in the Recycle Bin (it may have exceeded the bin size limit). No file can be restored.");
            }
            return FileOperationResult.Ok(path);
        }

        if (aborted) {
            return FileOperationResult.Fail(0x4C7, "Windows could not move \"" + path + "\" to the Recycle Bin. The item was left in place.");
        }
        return FileOperationResult.Ok(path);
    }

    private static long RecycleBinItemCount(string root) {
        if (string.IsNullOrEmpty(root)) return -1;
        try {
            SHQUERYRBINFO info = new SHQUERYRBINFO();
            info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
            int hr = SHQueryRecycleBin(root, ref info);
            return hr == 0 ? info.i64NumItems : -1;
        } catch {
            return -1;
        }
    }

    public static bool IsRecycleBinAvailableFor(string path) {
        try {
            string full = Path.GetFullPath(path);
            // UNC paths have no per-share Recycle Bin; Windows deletes them for good.
            if (full.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            string root = GetDriveRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            uint type = GetDriveType(root);
            if (type == DRIVE_REMOVABLE || type == DRIVE_REMOTE) return false;
            return true;
        } catch {
            return false;
        }
    }

    private static string GetDriveRoot(string path) {
        try {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;
            return root.TrimEnd('\\') + "\\";
        } catch {
            return null;
        }
    }

    public static FileOperationResult Rename(string path, string newName, bool replaceExisting) {
        FileOperationRequest request = new FileOperationRequest {
            Kind = FileOperationKind.Rename,
            SourcePath = path,
            DestinationPath = newName,
            ReplaceExisting = replaceExisting
        };
        FileOperationResult validation = Validate(request);
        return validation.Success ? RenameCore(path, newName, replaceExisting) : validation;
    }

    private static FileOperationResult RenameCore(string path, string newName, bool replaceExisting) {
        try {
            string parent = Path.GetDirectoryName(path);
            string destination = Path.Combine(parent, newName);
            if (!replaceExisting && (File.Exists(destination) || Directory.Exists(destination))) return FileOperationResult.Fail(183, "The destination already exists.");
            if (replaceExisting && File.Exists(destination)) File.Delete(destination);
            if (replaceExisting && Directory.Exists(destination)) Directory.Delete(destination, true);
            if (File.Exists(path)) File.Move(path, destination);
            else if (Directory.Exists(path)) Directory.Move(path, destination);
            else return new FileOperationResult { Success = true, AlreadyGone = true, CompletedPath = destination };
            return FileOperationResult.Ok(destination);
        } catch (Exception ex) {
            return FileOperationResult.Fail(Marshal.GetHRForException(ex) & 0xFFFF, ex.Message);
        }
    }

    public static FileOperationResult Move(string path, string destination, bool replaceExisting, CancellationToken cancellationToken, Action<int> progress) {
        if (string.IsNullOrEmpty(destination)) return FileOperationResult.Fail(87, "A destination is required.");
        if (cancellationToken.IsCancellationRequested) return FileOperationResult.Fail(995, "Operation cancelled.");

        string finalPath = Directory.Exists(destination) ? Path.Combine(destination, Path.GetFileName(path.TrimEnd('\\', '/'))) : destination;
        if (!replaceExisting && (File.Exists(finalPath) || Directory.Exists(finalPath))) return FileOperationResult.Fail(183, "The destination already exists.");
        if (File.Exists(path)) {
            if (replaceExisting && File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(path, finalPath);
            return FileOperationResult.Ok(finalPath);
        }
        if (Directory.Exists(path)) {
            if (replaceExisting && Directory.Exists(finalPath)) Directory.Delete(finalPath, true);
            try {
                Directory.Move(path, finalPath);
            } catch (IOException) {
                CopyDirectory(path, finalPath, cancellationToken, progress);
                Delete(path, DeleteMode.Permanent);
            }
            return FileOperationResult.Ok(finalPath);
        }
        return new FileOperationResult { Success = true, AlreadyGone = true, CompletedPath = path };
    }

    public static FileOperationResult Copy(string path, string destination, bool replaceExisting, CancellationToken cancellationToken, Action<int> progress) {
        if (string.IsNullOrEmpty(destination)) return FileOperationResult.Fail(87, "A destination is required.");
        string finalPath = Directory.Exists(destination) ? Path.Combine(destination, Path.GetFileName(path.TrimEnd('\\', '/'))) : destination;
        if (!replaceExisting && (File.Exists(finalPath) || Directory.Exists(finalPath))) return FileOperationResult.Fail(183, "The destination already exists.");
        if (File.Exists(path)) {
            if (replaceExisting && File.Exists(finalPath)) File.Delete(finalPath);
            File.Copy(path, finalPath, replaceExisting);
            return FileOperationResult.Ok(finalPath);
        }
        if (Directory.Exists(path)) {
            if (replaceExisting && Directory.Exists(finalPath)) Directory.Delete(finalPath, true);
            CopyDirectory(path, finalPath, cancellationToken, progress);
            return FileOperationResult.Ok(finalPath);
        }
        return new FileOperationResult { Success = true, AlreadyGone = true, CompletedPath = path };
    }

    private static void CopyDirectory(string source, string destination, CancellationToken token, Action<int> progress) {
        Directory.CreateDirectory(destination);
        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        int total = Math.Max(1, files.Length);
        for (int i = 0; i < files.Length; i++) {
            token.ThrowIfCancellationRequested();
            string relative = files[i].Substring(source.Length).TrimStart('\\', '/');
            string target = Path.Combine(destination, relative);
            string parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(files[i], target, true);
            if (progress != null) progress((i + 1) * 100 / total);
        }
    }

    private static FileOperationResult RepairPermissions(string path) {
        try {
            using (ProcessRunner takeown = new ProcessRunner("takeown.exe", "/F \"" + path + "\" /A")) takeown.Run(10000);
            using (ProcessRunner icacls = new ProcessRunner("icacls.exe", "\"" + path + "\" /grant *S-1-5-32-544:F *S-1-5-32-545:F /T /C")) icacls.Run(10000);
            return FileOperationResult.Ok(path);
        } catch (Exception ex) {
            return FileOperationResult.Fail(Marshal.GetHRForException(ex) & 0xFFFF, ex.Message);
        }
    }

    private sealed class ProcessRunner : IDisposable {
        private readonly System.Diagnostics.Process process;
        public ProcessRunner(string fileName, string arguments) {
            process = new System.Diagnostics.Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;
        }
        public void Run(int timeout) {
            process.Start();
            process.WaitForExit(timeout);
        }
        public void Dispose() { process.Dispose(); }
    }
}
