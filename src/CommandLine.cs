using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

internal sealed class CommandLineOptions {
    public bool IsCli { get; set; }
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }
    public bool Json { get; set; }
    public bool Wait { get; set; }
    public bool Kill { get; set; }
    public string Error { get; set; }
    public List<string> Paths { get; private set; }

    public CommandLineOptions() {
        Paths = new List<string>();
    }

    public static CommandLineOptions Parse(string[] args) {
        CommandLineOptions options = new CommandLineOptions();
        if (args == null) return options;
        bool endOfOptions = false;
        foreach (string arg in args) {
            if (string.IsNullOrEmpty(arg)) continue;
            if (endOfOptions) {
                options.Paths.Add(arg.Trim('\"'));
            } else if (arg == "--help" || arg == "-h" || arg == "/?") {
                options.IsCli = true;
                options.ShowHelp = true;
            } else if (arg == "--version") {
                options.IsCli = true;
                options.ShowVersion = true;
            } else if (arg == "--json") {
                options.IsCli = true;
                options.Json = true;
            } else if (arg == "--wait") {
                options.IsCli = true;
                options.Wait = true;
            } else if (arg == "--kill") {
                options.IsCli = true;
                options.Kill = true;
            } else if (arg == "--") {
                options.IsCli = true;
                endOfOptions = true;
            } else if (arg.StartsWith("--", StringComparison.Ordinal)) {
                options.IsCli = true;
                options.Error = "Unknown option: " + arg;
            } else {
                options.Paths.Add(arg.Trim('"'));
            }
        }
        return options;
    }
}

[DataContract]
internal sealed class CliResultDto {
    [DataMember(Order = 0)] public int SchemaVersion;
    [DataMember(Order = 1)] public string Status;
    [DataMember(Order = 2)] public string Error;
    [DataMember(Order = 3)] public List<CliTargetDto> Targets;
}

[DataContract]
internal sealed class CliTargetDto {
    [DataMember(Order = 0)] public string Path;
    [DataMember(Order = 1)] public bool Exists;
    [DataMember(Order = 2)] public List<CliProcessDto> Processes;
}

[DataContract]
internal sealed class CliProcessDto {
    [DataMember(Order = 0)] public int Pid;
    [DataMember(Order = 1)] public string Name;
    [DataMember(Order = 2)] public string ExecutablePath;
    [DataMember(Order = 3)] public string Account;
    [DataMember(Order = 4)] public int ParentPid;
    [DataMember(Order = 5)] public string CommandLine;
    [DataMember(Order = 6)] public string Architecture;
    [DataMember(Order = 7)] public string Availability;
    [DataMember(Order = 8)] public List<CliLockDto> Locks;
}

[DataContract]
internal sealed class CliLockDto {
    [DataMember(Order = 0)] public string TargetPath;
    [DataMember(Order = 1)] public string LockedPath;
    [DataMember(Order = 2)] public string Access;
    [DataMember(Order = 3)] public string Severity;
    [DataMember(Order = 4)] public string Source;
    [DataMember(Order = 5)] public bool CanUnlock;
}

internal static class CommandLineRunner {
    public static int Run(CommandLineOptions options) {
        if (options.ShowHelp) {
            WriteUsage(Console.Out);
            return 0;
        }
        if (options.ShowVersion) {
            Console.WriteLine(BuildInfo.ProductName + " " + BuildInfo.Version);
            return 0;
        }
        if (!string.IsNullOrEmpty(options.Error)) return Fail(options, options.Error, 2);
        if (options.Paths.Count == 0) return Fail(options, "At least one file or folder path is required.", 2);

        ConsoleCancelEventHandler cancelHandler = null;
        CancellationTokenSource cancellation = new CancellationTokenSource();
        cancelHandler = delegate(object sender, ConsoleCancelEventArgs e) {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try {
            ScanResult result;
            bool killAttempted = false;
            do {
                ScanRequest request = new ScanRequest(options.Paths);
                request.ForceRefresh = true;
                request.Deadline = TimeSpan.FromSeconds(30);
                request.CancellationToken = cancellation.Token;
                result = UnlockerForm.RunScan(request, null);
                if (result.Status == ScanStatus.Failed) return Fail(options, result.ErrorMessage, 3, result);
                if (result.Status == ScanStatus.Cancelled) return Fail(options, "Operation cancelled.", 4, result);
                if (result.Status == ScanStatus.TimedOut) return Fail(options, "Scan timed out.", 4, result);

                if (options.Kill && !killAttempted) {
                    killAttempted = true;
                    bool killFailure = false;
                    foreach (ProcessItem item in result.Processes) {
                        if (item == null || item.Pid == 4) {
                            if (item != null && item.Pid == 4) killFailure = true;
                            continue;
                        }
                        if (!UnlockerForm.KillProcessDirect(item.Pid, item.Name)) killFailure = true;
                    }
                    if (killFailure && !options.Wait) {
                        WriteResult(options, result);
                        return 1;
                    }
                    if (result.Processes.Count > 0) {
                        Thread.Sleep(150);
                        continue;
                    }
                }
                if (options.Wait && result.Processes.Count > 0) {
                    Thread.Sleep(250);
                    continue;
                }
                break;
            } while (!cancellation.IsCancellationRequested);

            WriteResult(options, result);
            return result.Processes.Count == 0 ? 0 : 1;
        } finally {
            Console.CancelKeyPress -= cancelHandler;
            cancellation.Dispose();
        }
    }

    private static int Fail(CommandLineOptions options, string message, int code) {
        return Fail(options, message, code, null);
    }

    private static int Fail(CommandLineOptions options, string message, int code, ScanResult result) {
        if (options.Json) {
            WriteResult(options, result, message);
        } else {
            Console.Error.WriteLine(message);
        }
        return code;
    }

    private static void WriteResult(CommandLineOptions options, ScanResult result) {
        WriteResult(options, result, null);
    }

    private static void WriteResult(CommandLineOptions options, ScanResult result, string error) {
        if (result == null) result = new ScanResult();
        if (options.Json) {
            CliResultDto dto = BuildDto(result, error, options.Paths);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(CliResultDto));
            using (MemoryStream stream = new MemoryStream()) {
                serializer.WriteObject(stream, dto);
                Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
            }
            return;
        }

        if (!string.IsNullOrEmpty(error)) Console.Error.WriteLine(error);
        Console.WriteLine("Status: " + result.Status);
        foreach (ProcessItem item in result.Processes) {
            Console.WriteLine("- " + item.Name + " (PID " + item.Pid + ")");
            foreach (LockMatch match in item.Matches) {
                Console.WriteLine("  " + match.LockedPath + " | " + match.AccessDescription + " | " + match.Source);
            }
        }
        if (result.Processes.Count == 0) Console.WriteLine("No active locks found.");
    }

    private static CliResultDto BuildDto(ScanResult result, string error, List<string> paths) {
        CliResultDto dto = new CliResultDto {
            SchemaVersion = 1,
            Status = result.Status.ToString(),
            Error = error ?? result.ErrorMessage,
            Targets = new List<CliTargetDto>()
        };
        if (paths != null) {
            foreach (string path in paths) {
                dto.Targets.Add(new CliTargetDto {
                    Path = path,
                    Exists = File.Exists(path) || Directory.Exists(path),
                    Processes = new List<CliProcessDto>()
                });
            }
        }
        foreach (ProcessItem item in result.Processes) {
            CliProcessDto process = new CliProcessDto {
                Pid = item.Pid,
                Name = item.Name,
                ExecutablePath = item.Path,
                ParentPid = item.ParentPid,
                Locks = new List<CliLockDto>()
            };
            ProcessDetails details = UnlockerForm.GetProcessDetails(item.Pid, true);
            if (details != null) {
                process.ExecutablePath = details.ExecutablePath ?? process.ExecutablePath;
                process.Account = details.Account;
                process.ParentPid = details.ParentPid;
                process.CommandLine = details.CommandLine;
                process.Architecture = details.IsWow64 ? "32-bit" : (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit");
                process.Availability = details.Availability;
            }
            foreach (LockMatch match in item.Matches) {
                process.Locks.Add(new CliLockDto {
                    TargetPath = match.TargetPath,
                    LockedPath = match.LockedPath,
                    Access = match.AccessDescription,
                    Severity = match.Severity.ToString(),
                    Source = match.Source.ToString(),
                    CanUnlock = match.CanUnlock
                });
            }
            string targetPath = item.Matches.Count > 0 ? item.Matches[0].TargetPath : null;
            CliTargetDto target = null;
            foreach (CliTargetDto candidate in dto.Targets) {
                if (SameTargetPath(candidate.Path, targetPath)) {
                    target = candidate;
                    break;
                }
            }
            if (target == null) {
                target = new CliTargetDto { Path = targetPath, Exists = !string.IsNullOrEmpty(targetPath) && (File.Exists(targetPath) || Directory.Exists(targetPath)), Processes = new List<CliProcessDto>() };
                dto.Targets.Add(target);
            }
            target.Processes.Add(process);
        }
        return dto;
    }

    private static bool SameTargetPath(string left, string right) {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        try {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        } catch {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void WriteUsage(TextWriter writer) {
        writer.WriteLine("UnBlock " + BuildInfo.Version);
        writer.WriteLine("Usage: Unlocker.exe [--json] [--wait] [--kill] <file-or-folder> [...]");
        writer.WriteLine("  --json       Write versioned machine-readable output.");
        writer.WriteLine("  --wait       Keep scanning until all targets are released.");
        writer.WriteLine("  --kill       Terminate processes that can be terminated, then rescan.");
        writer.WriteLine("  --help       Show this help.");
        writer.WriteLine("  --version    Show the product version.");
    }
}
