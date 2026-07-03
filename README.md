# UnBlock

UnBlock is a lightweight utility designed to identify and terminate processes that are holding active locks on files or directories, preventing them from being moved, renamed, or deleted. 

---

## Key Features

* **Shallow-First Scan**: Uses a level-by-level (BFS) queue to scan folders, prioritizing surface-level files where locks are most common.
* **Instant Process Matching**: Instantly checks running processes against target paths to detect executing applications without waiting for disk traversal.
* **Accurate Lock Checking**: Implements dual-pass file verification to prevent read-only or restricted-permission files from being falsely flagged as locked.
* **Windows Shell Integration**: Adds right-click options to your context menu for quick scanning.

---

## How to Install

1. Save the provided `install.bat` script to your computer.
2. Right-click the `install.bat` file and select **Run as administrator**.
3. A folder browser dialog will open. Select the folder where you want to install UnBlock (it will create an `UnBlock` folder inside your selection).
4. The installer will compile the executable (`Unlocker.exe`), register the context menu options, and run a fast, silent test scan to warm up system components.

---

## How to Use

1. Navigate to the file or folder that is locked.
2. Right-click the target resource.
3. Select **UnBlock File**, **UnBlock Folder**, or **UnBlock This Folder** (if clicking in empty space).
4. The UnBlock window will open and display any locking processes:
   * **Terminate Process**: Closes the selected process.
   * **Terminate All**: Closes all identified locking processes.
   * **Close**: Safely exits the scanner.

*Note: If an active process requires elevated permissions to be closed, the application will prompt you to restart it as an Administrator.*

---

## How to Uninstall

1. Navigate to the folder where UnBlock was installed (default is `C:\Program Files\UnBlock`).
2. Right-click the `uninstall.bat` file and select **Run as administrator**.
3. The uninstaller will remove all associated registry keys and delete the application folder.