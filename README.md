# UnBlock

UnBlock is a lightweight, zero-dependency Windows utility designed to identify and terminate processes that are holding active locks on files or directories, preventing you from moving, renaming, or deleting them.

---

## Key Features

* **Instant Folder Scanning**: Instead of performing slow, disk-heavy directory traversal, UnBlock instantly checks running system processes to see if they are executing out of the target folder. This makes directory scanning immediate and quiet.
* **Direct File Handle Resolution**: When scanning a specific file, UnBlock queries the Windows Restart Manager API to locate the exact application holding the lock.
* **Real-Time UI Updates**: When you terminate a locking process, the application automatically waits for the operating system to release the file handle and refreshes the list in real-time.
* **Context Menu Integration**: Adds convenient right-click options ("UnBlock File" or "UnBlock Folder") directly to your Windows Explorer context menu.

---

## How to Install

1. Save the provided `install.bat` script to your computer.
2. Right-click the `install.bat` file and select **Run as administrator**.
3. A folder browser dialog will open. Select the directory where you want to install UnBlock (an `UnBlock` folder will be created inside your selection).
4. The installer will automatically compile the executable (`Unlocker.exe`), register the context menu options, and run a brief background warmup.

---

## How to Use

1. Navigate to the file or folder that is locked.
2. Right-click the target resource:
   * Select **UnBlock File** for individual files.
   * Select **UnBlock Folder** for directories.
   * Select **UnBlock This Folder** (if right-clicking empty space inside a folder).
3. The UnBlock window will open and display any locking processes:
   * **Terminate Process**: Closes the selected process and refreshes the list.
   * **Terminate All**: Closes all identified locking processes at once.
   * **Close**: Safely exits the utility.

*Note: If a locked process requires elevated permissions to be closed, the application will display a prompt asking to restart itself as an Administrator*

---

## How to Uninstall

1. Navigate to the folder where you installed UnBlock (the default path is `C:\Program Files\UnBlock`).
2. Right-click the `uninstall.bat` file and select **Run as administrator**.
3. The uninstaller will remove all associated registry keys and delete the application folder.