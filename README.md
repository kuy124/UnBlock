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

### During Explorer Deletion
When Windows Explorer cannot delete a file that is in use, UnBlock replaces the stock error dialog with its own prompt. From it you kill the locking process and delete, or unlock the handles and delete.

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