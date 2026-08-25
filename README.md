<div align="center">
  <h1>UnBlock</h1>
  <p>
    <b>A lightweight utility designed to resolve "File in Use" and "Folder Access Denied" errors on Windows.</b>
  </p>
</div>

<p align="center">
  When Windows blocks you from moving, renaming, or deleting a file because it is "open in another program," UnBlock helps you identify the culprit. By adding a simple right-click option to your context menu, it displays the specific background processes holding your files hostage, allowing you to release the lock or close the program safely.
</p>

<p align="center">
  To ensure security and transparency, UnBlock does not ship a pre-built main program. The <code>setup.exe</code> installer compiles the readable C# source code from the <code>src</code> folder locally on your machine during setup, and then removes itself and the sources automatically — leaving only the installed program.
</p>

<br>
<hr>

## Quick Setup Guide

Follow these steps to set up UnBlock on your computer.

### Step 1: Extract the Files
1. Download the latest release `UnBlock.zip` archive.
2. **Do not double-click or run files directly inside the zip folder.**
3. Right-click the `.zip` file, select **Extract All...**, and extract the contents to a normal folder of your choice.

### Step 2: Run the Setup
1. Double-click **`setup.exe`**.
   * If Windows SmartScreen appears (the setup binary is unsigned), click **More info** → **Run anyway**.
2. Choose where you want to install UnBlock (or simply press **OK** to accept the default folder: `C:\Program Files\UnBlock`).
3. The setup compiles the source code locally, registers your right-click menu, and then **deletes itself and the `src` folder automatically** — leaving only the installed program behind.

> <i>Transparency note: <code>setup.exe</code> is compiled from <code>src/Setup.cs</code>. Prefer to build it yourself? Run:<br>
> <code>%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /out:setup.exe src\Setup.cs</code></i>
> <i>Developer note: when <code>setup.exe</code> runs from a source checkout (a <code>.git</code> folder sits next to it), it detects developer mode and keeps itself and the <code>src</code> folder after installing. The <code>/KEEPSETUP</code> flag forces the same behavior anywhere.</i>

<hr>

## How to Use

Dealing with locked files is now simple and integrated into your daily workflow.

### Option A: Right-Click Integration
1. Select one or **multiple** stubborn files or folders in Windows Explorer.
2. Right-click your selection and select:
   * **`UnBlock`** (when clicking files or folders)
   * **`UnBlock This Folder`** (when clicking the empty background space inside an open directory)
3. If multiple items are selected, UnBlock will automatically group them into a single window to keep your screen clean.

### Option B: Standalone Mode
You can also launch UnBlock directly from its installation folder or your Start Menu without right-clicking. 
* Use the **`+ File`** and **`+ Folder`** buttons in the top header to manually add items to the analysis list.

---

## Understanding the Lock Severity

Once the scan completes, UnBlock categorizes the open file handles using clear color coding so you know exactly which process is the primary blocker:

* <span style="color:#c0392b">🔴</span> **High Severity (Red):** Represents exclusive write/delete locks. These processes are actively modifying the file or preventing any other program from touching it.
* <span style="color:#d35400">🟡</span> **Medium Severity (Orange):** Represents active readers. The file is being read (such as a media player playing a video or an active file copy in progress).
* <span style="color:#27ae60">🟢</span> **Low Severity (Green):** Represents benign usage. The process is simply resting in or browsing the folder (such as an idle command prompt window or WinRAR sitting open on the path). These rows are slightly faded out to help you quickly ignore them and focus on the red locks.

---

## Releasing Locks

When you identify the locking program, select it in the list and choose your action:

* **Unlock Selected / Unlock All:** Safely disconnects the program from the file without closing the program itself. This is highly recommended as it prevents data loss in your other applications.
* **Kill Process / Kill All:** Forcefully closes the entire program holding the lock. Use this if the application is frozen, unresponsive, or running directly from the folder you want to delete.

> <i>Note: Windows System Kernel processes (PID 4) cannot be terminated, but UnBlock will still identify them for you so you know why the resource is occupied.</i>

---

## Maintenance & Removal

### Normal Uninstallation
UnBlock integrates cleanly with Windows. If you wish to remove it:
1. Open your Windows **Settings** (or Control Panel).
2. Go to **Apps** > **Installed Apps** (or Apps & Features).
3. Search for **UnBlock File & Folder Unlocker** and click **Uninstall**.
4. The uninstaller runs completely silently in the background, removes all registry keys, and deletes the program folders.

### Dynamic Self-Cleaning
If you decide to simply delete the `C:\Program Files\UnBlock` folder manually:
* An invisible background system task will automatically detect that the program is missing.
* It will cleanly sweep away your right-click context menu options and registration entries without requiring you to run an uninstaller or reboot your PC.

<hr>

<details>
  <summary><b>License</b> <i>(Click to expand)</i></summary>
  <br>
  <p>This project is open-source and distributed under the <strong>MIT License</strong>.</p>
</details>