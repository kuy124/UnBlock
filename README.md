# UnBlock

UnBlock is a lightweight utility designed to resolve "File in Use" errors on Windows. It allows you to right-click a locked file or folder to identify and close the background processes preventing you from modifying, moving, or deleting it.

To ensure transparency and security, the utility compiles locally on your machine during installation from the provided C# source code.

---

## Key Features

* **On-Demand Scanning:** Quickly identifies processes holding open file or folder handles.
* **Shell Integration:** Adds context menu options directly to the Windows right-click menu.
* **Transparent Build Process:** The installer securely builds the executable directly on your computer from the readable source files.
* **Minimal Footprint:** Runs only when invoked, without background services or persistent system resource consumption.

---

## Installation

1. Download the latest release `.zip` file.
2. Extract the archive completely to a standard directory on your computer (do not run the installer directly from inside the compressed folder).
3. Open the extracted folder and run **`install.bat`** as an Administrator to allow the installer to register the context menu and compile the executable.
4. Select an installation path when prompted, or accept the default directory (`C:\Program Files\UnBlock`).

---

## How to Use

Once installed, navigate to the locked file or folder:

1. Right-click the item and select the appropriate action:
   * **UnBlock File** (when right-clicking a single file)
   * **UnBlock Folder** (when right-clicking a folder)
   * **UnBlock This Folder** (when right-clicking the empty background area within an open directory)
2. Review the listed processes currently holding file locks.
3. Select a process and click **Terminate Process**, or choose **Terminate All** to close all listed locking applications.

*Note: If a stubborn system process requires administrative privileges to terminate, the utility will request elevation to proceed.*

---

## How to Uninstall

To remove the utility from your system:

1. Navigate to the installation directory (default: `C:\Program Files\UnBlock`).
2. Run **`uninstall.bat`** as an Administrator.
3. The script will remove the shell context menu entries, clean up associated registry keys, and delete the installation directory.

---

## License

This project is open-source and distributed under the MIT License.