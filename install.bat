@echo off
:: Enhanced Installation Script for UnBlock
color 0B
echo =========================================
echo       UnBlock Installation Utility
echo =========================================
echo.

:: Check for Admin Privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [!] Requesting Administrator privileges...
    powershell -STA -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo [*] Administrator privileges confirmed.
echo [*] Loading PowerShell Installer engine... Please wait.
:: We pass '%~dp0' to PowerShell so it knows the folder install.bat is running from.
powershell -STA -NoProfile -ExecutionPolicy Bypass -Command "$InstallSourceDir = '%~dp0'; $content = Get-Content -LiteralPath '%~f0'; $start = $false; $script = ($content | Where-Object { if ($_ -match '^##POWERSHELL_START##') { $start = $true; return $false }; if ($_ -match '^##POWERSHELL_END##') { $start = $false }; $start }) -join [char]10; Invoke-Expression $script"
exit /b

##POWERSHELL_START##
Add-Type -AssemblyName System.Windows.Forms

# Gather every C# source file sitting next to this install.bat
$SourceFiles = @(Get-ChildItem -LiteralPath $InstallSourceDir -Filter "*.cs" -File | Select-Object -ExpandProperty FullName)

# Error Check: Make sure the user didn't separate the files
if ($SourceFiles.Count -eq 0) {
    [System.Windows.Forms.MessageBox]::Show("Could not find any .cs source files in the installation folder.`n`nPlease make sure install.bat and all the .cs source files are extracted into the exact same folder before running.", "Setup Error", "OK", "Error")
    Exit
}

$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
$dialog.Description = "Select where you want to install UnBlock. (An 'UnBlock' folder will be created inside your selection)."
$dialog.ShowNewFolderButton = $true
$dialog.SelectedPath = [Environment]::GetFolderPath("ProgramFiles")

if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
    $InstallDir = Join-Path $dialog.SelectedPath "UnBlock"
} else {
    $InstallDir = Join-Path [Environment]::GetFolderPath("ProgramFiles") "UnBlock"
}

$ExePath = Join-Path $InstallDir "Unlocker.exe"
$UninstallExePath = Join-Path $InstallDir "uninstall.exe"

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Compile all C# source files dynamically from the source directory, and save the .exe into the installation directory
try {
    Write-Host "[*] Compiling UnBlock engine directly to System architecture..."
    Add-Type -Path $SourceFiles -OutputAssembly $ExePath -OutputType WindowsApplication -ReferencedAssemblies "System.Windows.Forms.dll", "System.Drawing.dll", "System.dll", "System.Core.dll" -ErrorAction Stop
} catch {
    $errMsg = $_.Exception.Message
    [System.Windows.Forms.MessageBox]::Show("Compilation failed!`n`nErrors:`n$errMsg", "Setup Error", "OK", "Error")
    Exit
}

# Silent post-install headless warmup (caching indices immediately for fast first load)
Write-Host "[*] Warming up process caches..."
Start-Process -FilePath $ExePath -ArgumentList "[WARMUP]" -WindowStyle Hidden -Wait

# Dedicated native uninstaller binary (GUI only - no console, works from Settings > Apps)
Write-Host "[*] Deploying native uninstaller..."
Copy-Item -Path $ExePath -Destination $UninstallExePath -Force

# =========================================================================
# HARDENED REGISTRY SETUP
# =========================================================================
Write-Host "[*] Registering Context Menus..."
$baseKey = [Microsoft.Win32.Registry]::LocalMachine

# 1. Right Click -> Files (Points directly to engine, eliminating VBS scripts entirely)
$keyFile = $baseKey.CreateSubKey("SOFTWARE\Classes\*\shell\UnBlock")
$keyFile.SetValue("", "UnBlock")
$keyFile.SetValue("Icon", "shell32.dll,239")
$keyFileCmd = $baseKey.CreateSubKey("SOFTWARE\Classes\*\shell\UnBlock\command")
$keyFileCmd.SetValue("", "`"$ExePath`" `"%1`"")

# 2. Right Click -> Folders
$keyDir = $baseKey.CreateSubKey("SOFTWARE\Classes\Directory\shell\UnBlock")
$keyDir.SetValue("", "UnBlock")
$keyDir.SetValue("Icon", "shell32.dll,239")
$keyDirCmd = $baseKey.CreateSubKey("SOFTWARE\Classes\Directory\shell\UnBlock\command")
$keyDirCmd.SetValue("", "`"$ExePath`" `"%1`"")

# 3. Right Click -> Empty Space Inside a Folder
$keyBg = $baseKey.CreateSubKey("SOFTWARE\Classes\Directory\Background\shell\UnBlock")
$keyBg.SetValue("", "UnBlock This Folder")
$keyBg.SetValue("Icon", "shell32.dll,239")
$keyBgCmd = $baseKey.CreateSubKey("SOFTWARE\Classes\Directory\Background\shell\UnBlock\command")
$keyBgCmd.SetValue("", "`"$ExePath`" `"%V`"")

# 4. Right Click -> Drives
$keyDrive = $baseKey.CreateSubKey("SOFTWARE\Classes\Drive\shell\UnBlock")
$keyDrive.SetValue("", "UnBlock")
$keyDrive.SetValue("Icon", "shell32.dll,239")
$keyDriveCmd = $baseKey.CreateSubKey("SOFTWARE\Classes\Drive\shell\UnBlock\command")
$keyDriveCmd.SetValue("", "`"$ExePath`" `"%1`"")

# 5. Windows 11: offer to put UnBlock directly on the right-click menu.
#    The modern Win11 menu hides classic entries behind 'Show more options';
#    restoring the classic menu system-wide is the only reliable way to
#    surface them without an MSIX/COM shell extension.
if ([Environment]::OSVersion.Version.Build -ge 22000) {
    $menuChoice = [System.Windows.Forms.MessageBox]::Show("Put UnBlock directly on the Windows 11 right-click menu?`n`nBy default, Windows 11 hides classic entries behind 'Show more options'. Choosing Yes restores the classic full right-click menu system-wide so UnBlock is always one click away.`n`n(Note: Explorer will restart once to apply.)", "Windows 11 Right-Click Menu", "YesNo", "Question")
    if ($menuChoice -eq [System.Windows.Forms.DialogResult]::Yes) {
        Write-Host "[*] Enabling classic right-click menu for Windows 11..."
        New-Item -Path "HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32" -Force | Out-Null
        New-ItemProperty -Path "HKCU:\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32" -Name '(default)' -Value '' -PropertyType String -Force | Out-Null
        New-Item -Path "HKCU:\Software\UnBlock" -Force | Out-Null
        New-ItemProperty -Path "HKCU:\Software\UnBlock" -Name "ClassicMenu" -Value 1 -PropertyType DWord -Force | Out-Null
        Stop-Process -Name explorer -Force
    }
}

# 6. Windows Add/Remove Programs (Apps & Features) Integration
Write-Host "[*] Registering with Windows Add/Remove Programs..."
$uninstallKey = $baseKey.CreateSubKey("SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UnBlock")
$uninstallKey.SetValue("DisplayName", "UnBlock File & Folder Unlocker")
$uninstallKey.SetValue("DisplayVersion", "2.1.0")
$uninstallKey.SetValue("Publisher", "UnBlock")
$uninstallKey.SetValue("UninstallString", "`"$UninstallExePath`"")  # Native GUI uninstaller (no console)
$uninstallKey.SetValue("QuietUninstallString", "`"$UninstallExePath`" /SILENT")
$uninstallKey.SetValue("InstallLocation", "`"$InstallDir`"")
$uninstallKey.SetValue("DisplayIcon", "`"$ExePath`"")
$uninstallKey.SetValue("NoModify", [int]1, [Microsoft.Win32.RegistryValueKind]::DWord)
$uninstallKey.SetValue("NoRepair", [int]1, [Microsoft.Win32.RegistryValueKind]::DWord)

# =========================================================================
# DYNAMIC WATCHER SETUP (NO FILE-USE LOCKS, NO CONSOLE FLASHES)
# =========================================================================
Write-Host "[*] Deploying dynamic background watcher..."
$LocalDir = Join-Path $env:LocalAppData "UnBlock"
if (-not (Test-Path $LocalDir)) {
    New-Item -ItemType Directory -Path $LocalDir -Force | Out-Null
}
$WatcherExe = Join-Path $LocalDir "UnBlockWatcher.exe"
Copy-Item -Path $ExePath -Destination $WatcherExe -Force

# Register the hidden task to run the watcher in Session 0 (completely hidden background)
$cleanupCommand = "`"$WatcherExe`" [WATCHER] `"$InstallDir`""
Start-Process -FilePath "schtasks" -ArgumentList "/create /tn `"UnBlock-Cleanup`" /sc ONLOGON /ru `"SYSTEM`" /rl HIGHEST /tr `"$cleanupCommand`" /f" -WindowStyle Hidden -Wait

# Fire the watcher immediately so it is active without requiring a logoff/restart
Start-Process -FilePath "schtasks" -ArgumentList "/run /tn `"UnBlock-Cleanup`"" -WindowStyle Hidden -Wait

[System.Windows.Forms.MessageBox]::Show("UnBlock was installed successfully!`n`nYou can now Right-Click on any locked file or folder to unlock it.`n`nInstalled to:`n$InstallDir", "Setup Complete", "OK", "Information")
##POWERSHELL_END##