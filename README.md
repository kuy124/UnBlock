<div align="center">
  <h1>UnBlock</h1>
  <p>
    <b>A lightning-fast, lightweight utility designed to resolve "File in Use" and "Folder Access Denied" errors on Windows.</b>
  </p>
</div>

<p align="center">
  By adding a simple right-click option to your context menu, UnBlock instantly identifies and resolves background processes that are preventing you from moving, renaming, or deleting your files. To guarantee absolute security and transparency, UnBlock does not ship as a pre-compiled executable. Instead, the installer compiles the readable C# source code locally on your machine during setup.
</p>

<br>
<hr>

## Core Features

<ul>
  <li><strong>Instant Memory Scanning:</strong> Bypasses slow file-by-file checking by directly querying the Windows Kernel for open handles, resulting in zero-lag detection.</li>
  <li><strong>True Unlocking (No Data Loss):</strong> Safely severs the specific file lock (handle) without forcing the entire application to terminate, protecting your unsaved work.</li>
  <li><strong>Deep System Detection:</strong> Utilizes <code>SeDebugPrivilege</code> to expose hidden file locks from Anti-Virus software, SQL servers, and core Windows System processes.</li>
  <li><strong>Network & Local Support:</strong> Seamlessly detects locks on local drives (<code>C:\</code>) as well as remote network shares (UNC paths).</li>
  <li><strong>Zero Bloatware:</strong> Operates strictly on-demand. There are no background services, no telemetry, and zero idle CPU or RAM consumption.</li>
</ul>

<hr>

## Installation Guide

<ol>
  <li>Download the latest release <code>.zip</code> archive.</li>
  <li>Extract the archive completely to a standard folder on your computer <i>(do not run scripts directly from inside the compressed folder)</i>.</li>
  <li>Right-click <strong><code>install.bat</code></strong> and select <strong>Run as Administrator</strong>.</li>
  <li>Choose an installation path when prompted (or accept the default <code>C:\Program Files\UnBlock</code>).</li>
</ol>

<blockquote>
  <i>The setup script will automatically compile the executable, secure the binaries, and register the right-click context menus.</i>
</blockquote>

<hr>

## How to Use

Once installed, dealing with locked files becomes effortless:

<ol>
  <li>Right-click the stubborn item and select:
    <ul>
      <li><kbd>UnBlock File</kbd> (when right-clicking a single file)</li>
      <li><kbd>UnBlock Folder</kbd> (when right-clicking a directory)</li>
      <li><kbd>UnBlock This Folder</kbd> (when right-clicking the empty background space inside an open folder)</li>
    </ul>
  </li>
  <li>The scanner will instantly display a list of the exact processes holding your files hostage.</li>
  <li>Choose your preferred action:
    <ul>
      <li><strong>Unlock / Unlock All:</strong> Safely closes the application's connection to the file without closing the program itself. <i>(Highly Recommended)</i></li>
      <li><strong>Kill / Kill All:</strong> Forcefully terminates the entire application holding the lock. Use this if the program is frozen or executing directly from the target folder.</li>
    </ul>
  </li>
</ol>

<p>
  <i><strong>Note:</strong> Windows System Kernel processes (PID 4) cannot be terminated, but UnBlock will still reveal them so you know exactly what is tying up your resources.</i>
</p>

<hr>

<details>
  <summary><b>How to Uninstall</b> <i>(Click to expand)</i></summary>
  <br>
  <ol>
    <li>Navigate to the installation directory (Default: <code>C:\Program Files\UnBlock</code>).</li>
    <li>Right-click <strong><code>uninstall.bat</code></strong> and select <strong>Run as Administrator</strong>.</li>
    <li>The uninstaller will instantly remove all shell context menu entries, clean up registry keys, and delete the application folder.</li>
  </ol>
</details>

<details>
  <summary><b>License</b> <i>(Click to expand)</i></summary>
  <br>
  <p>This project is open-source and distributed under the <strong>MIT License</strong>.</p>
</details>