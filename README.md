<div align="center">
  <h1>UnBlock</h1>
  <p>
    <b>Resolves "File in Use" and "Folder Access Denied" errors on Windows.</b>
  </p>
  <p>
    <a href="https://github.com/kuy124/UnBlock/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/kuy124/UnBlock/actions/workflows/ci.yml/badge.svg"></a>
  </p>
</div>

<p align="center">
  When Windows blocks a move, rename, or delete because a file is "open in another program," UnBlock finds the process behind it. A right-click entry on your context menu scans the file and lists the background processes holding it, and you release the lock or close the program from that list.
</p>

<p align="center">
  UnBlock does not ship a pre-built main program. During setup, <code>setup.exe</code> compiles the readable C# source from the <code>src</code> folder on your machine, then removes itself and the sources, leaving the installed program behind.
</p>

<br>
<hr>

## Quick Setup Guide

### Step 1: Extract the Files
1. Download the latest release <code>UnBlock.zip</code>.
2. **Do not run files directly inside the zip.**
3. Right-click the <code>.zip</code>, select **Extract All...**, and extract the contents to a normal folder.

### Step 2: Run the Setup
1. Double-click **<code>setup.exe</code>**.
   * Windows SmartScreen may warn because the setup binary is unsigned. Click **More info** → **Run anyway**.
2. Choose a destination, or press **OK** to accept <code>C:\Program Files\UnBlock</code>.
3. Setup compiles the source locally, registers your right-click menu, and deletes itself and the <code>src</code> folder.

> <i>Transparency note: <code>setup.exe</code> is compiled from <code>src/Setup.cs</code>. Prefer to build it yourself? Run:<br>
> <code>%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /out:setup.exe src\Setup.cs</code></i>
> <i>Developer note: when <code>setup.exe</code> runs from a source checkout (a <code>.git</code> folder sits next to it), it keeps itself and the <code>src</code> folder. The <code>/KEEPSETUP</code> flag forces the same behavior anywhere.</i>

<hr>

## How to Use

### Option A: Right-Click Integration
1. Select one or **multiple** locked files or folders in Windows Explorer.
2. Right-click your selection and pick:
   * **<code>UnBlock</code>** (files or folders)
   * **<code>UnBlock This Folder</code>** (empty background space inside an open directory)
3. Multiple selected items group into a single window.

### Option B: Standalone Mode
Launch UnBlock from its install folder or Start Menu without right-clicking.
* Use the **<code>+ File</code>** and **<code>+ Folder</code>** buttons in the header to add items to the list.
* Use the **<code>Dark</code>** / **<code>Light</code>** button in the header to switch the interface theme. The choice is remembered, and a fresh install follows your Windows app theme.

### During Explorer File-in-Use Prompts
When Windows Explorer reports that a file or folder is in use, UnBlock replaces the stock error dialog with its own prompt. From that popup you can choose **Kill & Recycle**, **Kill & Rename**, or **Kill & Move** without returning to the Explorer right-click menu. Rename opens a Rename-only dialog, Move opens a Move-only dialog, and Recycle opens a matching Recycle-only review. Each dialog shows the target and detected lockers, provides only its matching kill action, and lets you go Back or Cancel. UnBlock refreshes the scan before termination, asks you to review a changed locker list again, and verifies that the lock is gone before changing the file.

---

## Understanding the Lock Severity

After the scan finishes, UnBlock color-codes the open file handles:

* <span style="color:#c0392b">🔴</span> **High Severity (Red):** exclusive write/delete locks. The process is modifying the file or blocking other programs from touching it.
* <span style="color:#d35400">🟡</span> **Medium Severity (Orange):** active readers, such as a media player or a file copy in progress.
* <span style="color:#27ae60">🟢</span> **Low Severity (Green):** benign use, such as an idle command prompt or WinRAR open on the path. These rows fade out so you can ignore them and focus on the red locks.

---

## Releasing Locks

Select the locking program and choose an action:

* **Unlock Selected / Unlock All:** disconnects the program from the file without closing it. Prevents data loss in your other applications.
* **Kill Process / Kill All:** closes the program holding the lock. Use this when the application is frozen, unresponsive, or running from the folder you want to delete.

> <i>Note: Windows System Kernel processes (PID 4) cannot be terminated. UnBlock still identifies them so you know why the resource is occupied.</i>

## File Actions

Every normal file action is a **kill-before-action** workflow: UnBlock refreshes the lock scan, lists only processes locking the selected target, asks for confirmation, terminates those processes (never PID 4 or unrelated processes), verifies that the locks are gone, and then performs the requested operation. If no locker is found, the action continues without terminating any process. Confirmation warns that terminated applications may lose unsaved work, and the result reports each target separately.

The grouped action bar provides:

* **Kill & Recycle** — move targets to the Recycle Bin by default
* **Kill & Rename**, **Kill & Move**, and **Kill & Copy**
* **Kill & Permanent Delete** and **Kill & Delete at Restart** under the **More** menu
* **Repair Permissions** as a separate, explicit advanced action; it does not automatically terminate processes

Frequent actions sit on one command strip and the rest live under **More**, so the strip stays readable. Buttons are sized from their own labels and wrap to a new row when the window is narrow, so a label is never cut off.

Protected Windows and System targets are blocked from normal destructive actions. An administrator must deliberately choose the separate advanced action to permanently remove one. If a locker requires elevation, UnBlock serializes the exact pending action, resumes it in the elevated process, and reports the final per-target result back to the original window.

The details view includes executable path, account, parent PID, architecture, command line when readable, and exact locked paths.

You can drag files and folders onto the window, select several processes at once, reload a scan, cancel a scan, clear the target list, and open a process-details view by double-clicking a result.

## Command Line

The same executable can run without the window:

```text
Unlocker.exe --help
Unlocker.exe --version
Unlocker.exe [--json] [--wait] [--kill] <file-or-folder> [...]
```

`--json` writes versioned results with targets, process details, exact locked paths, access, severity, and lock source. `--wait` rescans until the locks are gone. `--kill` terminates processes that can be terminated and rescans. Results use exit code `0` for no unresolved locks, `1` for remaining locks or incomplete actions, `2` for invalid arguments, `3` for permission or scan failure, and `4` for cancellation or timeout.

---

## Maintenance & Removal

### Normal Uninstallation
1. Open Windows **Settings**.
2. Go to **Apps** > **Installed Apps**.
3. Find **UnBlock File & Folder Unlocker** and click **Uninstall**.
4. The uninstaller removes the registry keys and deletes the program folders.

### Dynamic Self-Cleaning
Delete the <code>C:\Program Files\UnBlock</code> folder manually and a background task notices the program is missing. It removes your right-click menu entries and registration without an uninstaller or a reboot.

<hr>

<details>
  <summary><b>License</b> <i>(Click to expand)</i></summary>
  <br>
  <p>This project is open-source and distributed under the <strong>MIT License</strong>.</p>
</details>
