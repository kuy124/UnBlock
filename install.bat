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

# Point to the Unlocker.cs file sitting next to this install.bat
$SourceCsPath = Join-Path $InstallSourceDir "Unlocker.cs"

# Error Check: Make sure the user didn't separate the files
if (-not (Test-Path $SourceCsPath)) {
    [System.Windows.Forms.MessageBox]::Show("Could not find 'Unlocker.cs' in the installation folder.`n`nPlease make sure both install.bat and Unlocker.cs are extracted into the exact same folder before running.", "Setup Error", "OK", "Error")
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
$UninstallerPath = Join-Path $InstallDir "uninstall.bat"

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Compile the C# file dynamically from the source directory, and save the .exe into the installation directory
try {
    Write-Host "[*] Compiling UnBlock engine directly to System architecture..."
    Add-Type -Path $SourceCsPath -OutputAssembly $ExePath -OutputType WindowsApplication -ReferencedAssemblies "System.Windows.Forms.dll", "System.Drawing.dll", "System.dll", "System.Core.dll" -ErrorAction Stop
} catch {
    $errMsg = $_.Exception.Message
    [System.Windows.Forms.MessageBox]::Show("Compilation failed!`n`nErrors:`n$errMsg", "Setup Error", "OK", "Error")
    Exit
}

# Silent post-install headless warmup (caching indices immediately for fast first load)
Write-Host "[*] Warming up process caches..."
Start-Process -FilePath $ExePath -ArgumentList "[WARMUP]" -WindowStyle Hidden -Wait

# Write Uninstaller script to the install directory
# Using a double-quoted string here so $InstallDir is strictly resolved during installation!
$UninstallerCode = @"
@echo off
:: Polished Uninstaller for UnBlock

:: Check for Admin Privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    powershell -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath '%~f0' -ArgumentList '-silent' -Verb RunAs"
    exit /b
)

:: Relaunch with hidden console if not already running silently
if "%~1" neq "-silent" (
    powershell -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath '%~f0' -ArgumentList '-silent'"
    exit /b
)

:: Prompt user with a GUI confirmation message box
powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; if ([System.Windows.Forms.MessageBox]::Show('Are you sure you want to completely remove UnBlock and all of its components?', 'Uninstall UnBlock', 'YesNo', 'Question') -eq 'No') { exit 1 }"
if %errorLevel% neq 0 exit /b

:: Fast execution pipeline
taskkill /f /im Unlocker.exe >nul 2>&1

reg delete "HKLM\SOFTWARE\Classes\*\shell\UnBlock" /f >nul 2>&1
reg delete "HKLM\SOFTWARE\Classes\Directory\shell\UnBlock" /f >nul 2>&1
reg delete "HKLM\SOFTWARE\Classes\Directory\Background\shell\UnBlock" /f >nul 2>&1
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UnBlock" /f >nul 2>&1

powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show('UnBlock has been successfully uninstalled.', 'Uninstall Complete', 'OK', 'Information')"

:: Fast self-deletion via hidden CMD process
powershell -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath 'cmd.exe' -ArgumentList '/c timeout /t 1 >nul & rmdir /s /q `"$InstallDir`"' -WindowStyle Hidden"
exit /b
"@

Set-Content -Path $UninstallerPath -Value $UninstallerCode -Encoding UTF8 -Force

# =========================================================================
# HARDENED REGISTRY SETUP
# =========================================================================
Write-Host "[*] Registering Context Menus..."
$baseKey = [Microsoft.Win32.Registry]::LocalMachine

# 1. Right Click -> Files
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

# 4. Windows Add/Remove Programs (Apps & Features) Integration
Write-Host "[*] Registering with Windows Add/Remove Programs..."
$uninstallKey = $baseKey.CreateSubKey("SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UnBlock")
$uninstallKey.SetValue("DisplayName", "UnBlock File & Folder Unlocker")
$uninstallKey.SetValue("DisplayVersion", "2.1.0")
$uninstallKey.SetValue("Publisher", "UnBlock")
$uninstallKey.SetValue("UninstallString", "`"$UninstallerPath`" -silent")  # Quiet launch integration
$uninstallKey.SetValue("InstallLocation", "`"$InstallDir`"")
$uninstallKey.SetValue("DisplayIcon", "`"$ExePath`"")
$uninstallKey.SetValue("NoModify", [int]1, [Microsoft.Win32.RegistryValueKind]::DWord)
$uninstallKey.SetValue("NoRepair", [int]1, [Microsoft.Win32.RegistryValueKind]::DWord)

[System.Windows.Forms.MessageBox]::Show("UnBlock was installed successfully!`n`nYou can now Right-Click on any locked file or folder to unlock it.`n`nInstalled to:`n$InstallDir", "Setup Complete", "OK", "Information")
##POWERSHELL_END##