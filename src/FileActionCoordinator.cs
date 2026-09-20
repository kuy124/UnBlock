using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public enum LockActionMode {
    None,
    DetectedLockersOnly
}

public enum FileActionStatus {
    Completed,
    Partial,
    Cancelled,
    Failed,
    NeedsElevation
}

[DataContract]
public sealed class FileActionRequest {
    [DataMember(Order = 0)] public int SchemaVersion { get; set; }
    [DataMember(Order = 1)] public string ActionId { get; set; }
    [DataMember(Order = 2)] public string Title { get; set; }
    [DataMember(Order = 3)] public List<FileOperationRequest> Operations { get; set; }
    [DataMember(Order = 4)] public LockActionMode LockActionMode { get; set; }
    [DataMember(Order = 5)] public bool AllowProtectedTargets { get; set; }
    [DataMember(Order = 6)] public DateTime CreatedUtc { get; set; }
    [DataMember(Order = 7)] public List<int> PreviouslyTerminatedPids { get; set; }

    public FileActionRequest() {
        SchemaVersion = 1;
        ActionId = Guid.NewGuid().ToString("N");
        Operations = new List<FileOperationRequest>();
        LockActionMode = LockActionMode.DetectedLockersOnly;
        CreatedUtc = DateTime.UtcNow;
        PreviouslyTerminatedPids = new List<int>();
    }
}

public sealed class FileActionPlan {
    public FileActionRequest Request { get; set; }
    public ScanResult Scan { get; set; }
    public List<ProcessItem> Lockers { get; set; }
    public string LockerSignature { get; set; }
    public string ErrorMessage { get; set; }

    public FileActionPlan() {
        Lockers = new List<ProcessItem>();
    }
}

[DataContract]
public sealed class FileActionTargetResult {
    [DataMember(Order = 0)] public string SourcePath { get; set; }
    [DataMember(Order = 1)] public string CompletedPath { get; set; }
    [DataMember(Order = 2)] public bool Success { get; set; }
    [DataMember(Order = 3)] public bool AlreadyGone { get; set; }
    [DataMember(Order = 4)] public bool Scheduled { get; set; }
    [DataMember(Order = 5)] public int ErrorCode { get; set; }
    [DataMember(Order = 6)] public string ErrorMessage { get; set; }
}

[DataContract]
public sealed class FileActionResult {
    [DataMember(Order = 0)] public int SchemaVersion { get; set; }
    [DataMember(Order = 1)] public string ActionId { get; set; }
    [DataMember(Order = 2)] public FileActionStatus Status { get; set; }
    [DataMember(Order = 3)] public List<FileActionTargetResult> Targets { get; set; }
    [DataMember(Order = 4)] public List<int> TerminatedPids { get; set; }
    [DataMember(Order = 5)] public List<int> UnresolvablePids { get; set; }
    [DataMember(Order = 6)] public string ErrorMessage { get; set; }
    [DataMember(Order = 7)] public bool CanRetryElevated { get; set; }

    public FileActionResult() {
        SchemaVersion = 1;
        Targets = new List<FileActionTargetResult>();
        TerminatedPids = new List<int>();
        UnresolvablePids = new List<int>();
    }
}

internal static class FileActionCoordinator {
    internal static FileActionPlan Prepare(FileActionRequest request, CancellationToken token, Action<string, int> progress) {
        FileActionPlan plan = new FileActionPlan { Request = request };
        string validationError = ValidateRequest(request);
        if (!string.IsNullOrEmpty(validationError)) {
            plan.ErrorMessage = validationError;
            return plan;
        }
        if (token.IsCancellationRequested) {
            plan.ErrorMessage = "Operation cancelled.";
            return plan;
        }

        if (request.LockActionMode == LockActionMode.None) {
            plan.Scan = new ScanResult();
            plan.LockerSignature = "";
            return plan;
        }

        List<string> existingTargets = new List<string>();
        foreach (FileOperationRequest operation in request.Operations) {
            if ((File.Exists(operation.SourcePath) || Directory.Exists(operation.SourcePath)) && !ContainsPath(existingTargets, operation.SourcePath)) existingTargets.Add(operation.SourcePath);
        }
        if (existingTargets.Count == 0) {
            plan.Scan = new ScanResult();
            plan.LockerSignature = "";
            return plan;
        }

        if (progress != null) progress("Refreshing locks...", 5);
        ScanRequest scanRequest = new ScanRequest(existingTargets) {
            ForceRefresh = true,
            IncludeModules = true,
            Deadline = TimeSpan.FromSeconds(30),
            CancellationToken = token
        };
        plan.Scan = UnlockerForm.RunScan(scanRequest, delegate(int value) {
            if (progress != null) progress("Refreshing locks...", Math.Min(60, value));
        });
        if (plan.Scan.Status != ScanStatus.Completed) {
            plan.ErrorMessage = plan.Scan.ErrorMessage ?? ("Lock scan did not complete: " + plan.Scan.Status);
            return plan;
        }

        foreach (ProcessItem item in plan.Scan.Processes) {
            if (item == null || !MatchesAnyOperation(item, request.Operations)) continue;
            plan.Lockers.Add(item);
        }
        plan.LockerSignature = BuildLockerSignature(plan.Lockers);
        return plan;
    }

    internal static FileActionResult ExecuteConfirmed(FileActionRequest request, FileActionPlan plan, bool isAdmin, CancellationToken token, Action<string, int> progress) {
        FileActionResult result = new FileActionResult { ActionId = request == null ? null : request.ActionId };
        if (request == null || plan == null || !string.IsNullOrEmpty(plan.ErrorMessage)) {
            result.Status = FileActionStatus.Failed;
            result.ErrorMessage = plan == null ? "The file action was not prepared." : plan.ErrorMessage;
            return result;
        }
        if (token.IsCancellationRequested) {
            result.Status = FileActionStatus.Cancelled;
            result.ErrorMessage = "Operation cancelled.";
            return result;
        }

        if (plan.Lockers.Count > 0) {
            for (int i = 0; i < plan.Lockers.Count; i++) {
                ProcessItem item = plan.Lockers[i];
                if (progress != null) progress("Terminating " + item.Name + " (PID " + item.Pid + ")...", 60 + (i * 20 / Math.Max(1, plan.Lockers.Count)));
                if (item.Pid == 4 || item.Pid == Process.GetCurrentProcess().Id) {
                    result.UnresolvablePids.Add(item.Pid);
                    continue;
                }
                if (UnlockerForm.KillProcessDirect(item.Pid, item.Name)) result.TerminatedPids.Add(item.Pid);
                else result.UnresolvablePids.Add(item.Pid);
            }
            if (result.UnresolvablePids.Count > 0) {
                result.Status = (!isAdmin && !ContainsPid(result.UnresolvablePids, 4)) ? FileActionStatus.NeedsElevation : FileActionStatus.Failed;
                result.CanRetryElevated = result.Status == FileActionStatus.NeedsElevation;
                result.ErrorMessage = ContainsPid(result.UnresolvablePids, 4) ? "Windows System process PID 4 cannot be terminated." : "One or more locking processes could not be terminated.";
                return result;
            }

            FileActionPlan verification = Prepare(request, token, progress);
            if (!string.IsNullOrEmpty(verification.ErrorMessage)) {
                result.Status = token.IsCancellationRequested ? FileActionStatus.Cancelled : FileActionStatus.Failed;
                result.ErrorMessage = verification.ErrorMessage;
                return result;
            }
            if (verification.Lockers.Count > 0) {
                foreach (ProcessItem item in verification.Lockers) if (!ContainsPid(result.UnresolvablePids, item.Pid)) result.UnresolvablePids.Add(item.Pid);
                result.Status = !isAdmin ? FileActionStatus.NeedsElevation : FileActionStatus.Failed;
                result.CanRetryElevated = result.Status == FileActionStatus.NeedsElevation;
                result.ErrorMessage = "Locking processes remain after termination. The file action was not started.";
                return result;
            }
        }

        for (int i = 0; i < request.Operations.Count; i++) {
            if (token.IsCancellationRequested) {
                result.Status = FileActionStatus.Cancelled;
                result.ErrorMessage = "Operation cancelled after completed targets were preserved.";
                return result;
            }
            FileOperationRequest operation = request.Operations[i];
            if (progress != null) progress(ActionLabel(operation) + "...", 80 + (i * 20 / Math.Max(1, request.Operations.Count)));
            FileOperationResult operationResult = FileOperations.Execute(operation, token, delegate(int value) { });
            result.Targets.Add(new FileActionTargetResult {
                SourcePath = operation.SourcePath,
                CompletedPath = operationResult.CompletedPath,
                Success = operationResult.Success,
                AlreadyGone = operationResult.AlreadyGone,
                Scheduled = operationResult.Scheduled,
                ErrorCode = operationResult.ErrorCode,
                ErrorMessage = operationResult.ErrorMessage
            });
        }

        bool allSucceeded = true;
        foreach (FileActionTargetResult target in result.Targets) if (!target.Success) allSucceeded = false;
        result.Status = allSucceeded ? FileActionStatus.Completed : FileActionStatus.Partial;
        result.ErrorMessage = allSucceeded ? null : "One or more targets could not be completed.";
        if (progress != null) progress(allSucceeded ? "Completed." : "Completed with warnings.", 100);
        return result;
    }

    internal static bool LaunchElevated(FileActionRequest request, out string errorMessage) {
        errorMessage = null;
        try {
            string requestPath = PendingActionStore.WriteRequest(request);
            ProcessStartInfo startInfo = new ProcessStartInfo {
                FileName = Application.ExecutablePath,
                Arguments = "[PENDING_ACTION] " + QuoteArgument(requestPath),
                Verb = "runas",
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return true;
        } catch (Exception ex) {
            errorMessage = ex.Message;
            return false;
        }
    }

    internal static FileActionResult ConsumeResult(string actionId) {
        return PendingActionStore.ConsumeResult(actionId);
    }

    internal static int RunPendingAction(string requestPath) {
        FileActionRequest request = PendingActionStore.ReadRequest(requestPath);
        if (request == null) return 3;
        FileActionResult result;
        try {
            FileActionPlan plan = Prepare(request, CancellationToken.None, null);
            result = ExecuteConfirmed(request, plan, true, CancellationToken.None, null);
        } catch (Exception ex) {
            result = new FileActionResult {
                ActionId = request.ActionId,
                Status = FileActionStatus.Failed,
                ErrorMessage = ex.Message
            };
        }
        PendingActionStore.WriteResult(result);
        return result.Status == FileActionStatus.Completed ? 0 : result.Status == FileActionStatus.Cancelled ? 4 : result.Status == FileActionStatus.Failed ? 3 : 1;
    }

    internal static string BuildLockerSignature(List<ProcessItem> lockers) {
        StringBuilder signature = new StringBuilder();
        List<string> values = new List<string>();
        foreach (ProcessItem item in lockers) values.Add(item.Pid + "|" + (item.Path ?? "") + "|" + (item.Name ?? ""));
        values.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values) signature.Append(value).Append(";");
        return signature.ToString();
    }

    internal static bool IsProtectedSystemTarget(string path) {
        if (string.IsNullOrEmpty(path)) return false;
        try {
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            string windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\', '/');
            string system = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.System)).TrimEnd('\\', '/');
            return string.Equals(full, windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(full, system, StringComparison.OrdinalIgnoreCase) || full.StartsWith(system + "\\", StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    private static string ValidateRequest(FileActionRequest request) {
        if (request == null || request.Operations == null || request.Operations.Count == 0) return "No file action was selected.";
        try {
            foreach (FileOperationRequest operation in request.Operations) {
                if (operation == null) return "The file action contains an empty operation.";
                if (!request.AllowProtectedTargets && IsProtectedSystemTarget(operation.SourcePath)) return "Protected Windows targets require the explicit advanced action.";
                FileOperationResult validation = FileOperations.Validate(operation);
                if (!validation.Success) return validation.ErrorMessage;
            }
        } catch (Exception ex) {
            return ex.Message;
        }
        return null;
    }

    private static bool MatchesAnyOperation(ProcessItem item, List<FileOperationRequest> operations) {
        if (item.Matches == null) return false;
        foreach (LockMatch match in item.Matches) {
            if (match == null) continue;
            foreach (FileOperationRequest operation in operations) if (SamePath(match.TargetPath, operation.SourcePath)) return true;
        }
        return false;
    }

    private static bool ContainsPath(List<string> paths, string candidate) {
        foreach (string path in paths) if (SamePath(path, candidate)) return true;
        return false;
    }

    private static bool SamePath(string left, string right) {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        try { return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool ContainsPid(List<int> pids, int pid) {
        foreach (int value in pids) if (value == pid) return true;
        return false;
    }

    private static string ActionLabel(FileOperationRequest operation) {
        if (operation.Kind == FileOperationKind.Rename) return "Renaming " + operation.SourcePath;
        if (operation.Kind == FileOperationKind.Move) return "Moving " + operation.SourcePath;
        if (operation.Kind == FileOperationKind.Copy) return "Copying " + operation.SourcePath;
        if (operation.DeleteMode == DeleteMode.RecycleBin) return "Moving to Recycle Bin";
        if (operation.DeleteMode == DeleteMode.OnRestart) return "Scheduling deletion at restart";
        return "Permanently deleting";
    }

    private static string QuoteArgument(string value) {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }
}

internal static class PendingActionStore {
    private const int PendingExpirationMinutes = 10;

    private static string DirectoryPath {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnBlock\\Pending"); }
    }

    internal static string WriteRequest(FileActionRequest request) {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, "action-" + request.ActionId + ".json");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteJson(temp, request);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
        return path;
    }

    internal static FileActionRequest ReadRequest(string path) {
        try { return ReadJson<FileActionRequest>(path); } catch { return null; }
    }

    internal static void WriteResult(FileActionResult result) {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, "action-" + result.ActionId + ".result.json");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteJson(temp, result);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    internal static FileActionResult ConsumeResult(string actionId) {
        if (string.IsNullOrEmpty(actionId)) return null;
        string path = Path.Combine(DirectoryPath, "action-" + actionId + ".result.json");
        try {
            if (!File.Exists(path)) return null;
            FileActionResult result = ReadJson<FileActionResult>(path);
            File.Delete(path);
            string requestPath = Path.Combine(DirectoryPath, "action-" + actionId + ".json");
            if (File.Exists(requestPath)) File.Delete(requestPath);
            return result;
        } catch { return null; }
    }

    internal static void CleanupExpired() {
        try {
            if (!Directory.Exists(DirectoryPath)) return;
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-PendingExpirationMinutes);
            foreach (string path in Directory.GetFiles(DirectoryPath, "action-*.json")) {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            foreach (string path in Directory.GetFiles(DirectoryPath, "action-*.result.json")) {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
        } catch { }
    }

    private static void WriteJson<T>(string path, T value) {
        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
        using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) serializer.WriteObject(stream, value);
    }

    private static T ReadJson<T>(string path) {
        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return (T)serializer.ReadObject(stream);
    }
}
