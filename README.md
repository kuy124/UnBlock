# UnBlock

UnBlock is a lightweight, zero-dependency Windows utility designed to instantly identify and terminate processes holding active locks on files or directories, preventing you from moving, renaming, or deleting them.

Unlike heavy, opaque utilities, UnBlock compiles locally on your machine during installation—ensuring 100% security transparency with zero malware risk.

---

## ⚡ Key Features

* **Instant Folder Scanning**: Bypasses slow, disk-heavy directory traversal by directly querying active system processes executing out of the target folder.
* **Direct File Handle Resolution**: Leverages the native Windows Restart Manager API to pinpoint the exact application holding the lock.
* **Real-Time Handle Refresh**: Automatically detects when the operating system releases a file handle after process termination and updates the UI instantly.
* **Shell Integration**: Adds seamless, native right-click context menu options ("UnBlock File" and "UnBlock Folder") to Windows Explorer.
* **Zero Dependencies**: Pure, native Windows architecture with no external runtimes, wrappers, or bloat.

---

## 🔒 Security & Trust Transparency

Because file-unlocking utilities require administrative access, UnBlock prioritizes complete open-source transparency:
* **No Pre-compiled Binaries**: The installation script builds the executable directly on your machine from the provided source code.
* **Local Compilation**: Eliminates the risk of downloading modified, malicious, or packaged `.exe` files.
* **Inspectable Source**: Every line of code can be reviewed prior to installation.

---

## 🚀 Installation & Setup

1. Clone or download this repository to your local machine.
2. Right-click `install.bat` and select **Run as administrator**.
3. Choose your desired installation directory via the folder picker (e.g., `C:\Program Files\UnBlock`).
4. The script will automatically compile the native executable (`UnBlock.exe`), register the Windows Explorer context menu extensions, and run a rapid background warmup.

---

## 🛠️ How to Use

1. Navigate to the locked file or folder in Windows Explorer.
2. Right-click the target resource and select your action:
   * **UnBlock File**: For individual locked files.
   * **UnBlock Folder**: When right-clicking a directory.
   * **UnBlock This Folder**: When right-clicking inside empty space within a directory.
3. The UnBlock management window will display all locking processes:
   * **Terminate Process**: Closes the specific selected process.
   * **Terminate All**: Force-closes all identified locking handles simultaneously.
   * **Close**: Safely exits the utility.

*Note: If a targeted process requires elevated system privileges to terminate, UnBlock will automatically prompt to restart itself with Administrator permissions.*

---

## ❌ Uninstallation

1. Navigate to your installation folder (Default: `C:\Program Files\UnBlock`).
2. Right-click `uninstall.bat` and select **Run as administrator**.
3. The uninstaller will cleanly scrub all registered Windows Explorer context menu entries and delete the application folder.

---

## 📄 License

This project is open-source and available under the [MIT License](LICENSE).
