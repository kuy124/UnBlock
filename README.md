# UnBlock

UnBlock is a fast, lightweight Windows tool that helps you deal with "File in Use" errors. It lets you right-click any locked file or folder and instantly close the background programs that are stopping you from moving, renaming, or deleting it.

Unlike many other utilities, UnBlock is completely transparent. It builds itself locally on your computer during installation, ensuring there are no hidden trackers, malware, or bloated software.

---

## Why Use UnBlock?

* **Fast and Accurate:** It instantly scans and finds the exact program holding onto your file or folder.
* **Simple Right-Click Menu:** It adds native "UnBlock" options directly to your normal Windows right-click menu.
* **100% Safe and Transparent:** To guarantee your security, we do not provide pre-packaged `.exe` files. The installer securely compiles the program on your own computer from the provided, easily readable source code. 
* **No Clutter:** It runs only when you click it. There are no background services draining your battery or CPU.

---

## Installation

1. Download the latest UnBlock `.zip` file from the Releases page.
2. **Extract** the downloaded `.zip` file to a regular folder on your computer. (Do not run the files directly from inside the `.zip`).
3. Open the extracted folder and double-click **`install.bat`**. 
   *(Note: Windows will ask for Administrator permission so it can add the right-click menu options and build the tool).*
4. A prompt will appear asking where you want to install it. You can choose a folder or just click OK to use the default location (`C:\Program Files\UnBlock`).

---

## How to Use

Once installed, simply navigate to the file or folder that is stuck.

1. Right-click the item and choose your action:
   * **UnBlock File**: Use this when right-clicking a single file.
   * **UnBlock Folder**: Use this when right-clicking a folder.
   * **UnBlock This Folder**: Use this when right-clicking the empty white space inside an open folder.
2. A window will pop up showing the exact programs locking your files.
3. Select a program from the list and click **Terminate Process**, or click **Terminate All** to close them all at once.

*Note: If a stubborn program requires higher system permissions to close, UnBlock will ask to restart itself as an Administrator to finish the job.*

---

## How to Uninstall

UnBlock leaves no mess behind. If you want to remove it:

1. Go to the folder where you installed UnBlock (the default is `C:\Program Files\UnBlock`).
2. Double-click **`uninstall.bat`**.
3. It will instantly remove the right-click menu options, clean up the registry, and delete the application folder.

---

## License

This project is completely free, open-source, and available under the MIT License.