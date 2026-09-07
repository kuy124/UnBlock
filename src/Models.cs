using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public enum Severity {
    Low,      // Benign / Green
    Medium,   // Active Read / Orange
    High      // Severe Write/Delete Lockout / Red
}

// --- Privilege Adjustment Structures ---
    // --- Privilege Adjustment Constants ---
    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID {
        public uint LowPart;
        public int HighPart;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct PROCESSENTRY32 {
    public uint dwSize;
    public uint cntUsage;
    public uint th32ProcessID;
    public IntPtr th32DefaultHeapID;
    public uint th32ModuleID;
    public uint cntThreads;
    public uint th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szExeFile;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MODULEENTRY32 {
    public uint dwSize;
    public uint th32ModuleID;
    public uint th32ProcessID;
    public uint GlblcntUsage;
    public uint ProccntUsage;
    public IntPtr modBaseAddr;
    public uint modBaseSize;
    public IntPtr hModule;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szModule;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szExePath;
}

[StructLayout(LayoutKind.Sequential)]
public struct MEMORY_BASIC_INFORMATION {
    public IntPtr BaseAddress;
    public IntPtr AllocationBase;
    public uint AllocationProtect;
    public IntPtr RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}

public struct HandleInfo {
    public IntPtr HandleValue;
    public ushort ObjectTypeIndex;
    public uint GrantedAccess;
}

public class TargetMatchInfo {
    public string OriginalPath { get; set; }
    public string NormalizedPath { get; set; }
    public bool IsDir { get; set; }
    public bool IsNetwork { get; set; }
    public string networkSearchPath { get; set; }
    public string TargetDevicePath { get; set; }
    public string DevicePathWithSlash { get; set; }
}

public class ProcessItem {
    public int Pid { get; set; }
    public string Name { get; set; }
    public string Path { get; set; }
    public uint GrantedAccess { get; set; }
    public bool IsDir { get; set; }
    public List<IntPtr> Handles { get; set; }
    public bool IsModuleLock { get; set; }

    public ProcessItem() {
        Handles = new List<IntPtr>();
    }
}

public enum DeletePromptResult {
    KillAndDelete,
    Skip,
    SkipAll,
    Cancel
}

public class DeletePromptForm : Form {
    public DeletePromptResult Result { get; private set; }

    public DeletePromptForm(string targetPath, int errCode, string details, bool isInitialBatch) {
        this.Result = DeletePromptResult.Cancel;
        this.Text = "File in Use — UnBlock";
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.ClientSize = new Size(540, 215);
        this.BackColor = Color.FromArgb(249, 250, 252);
        this.TopMost = true;
        this.Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        PictureBox picIcon = new PictureBox();
        picIcon.Location = new Point(18, 16);
        picIcon.Size = new Size(32, 32);
        picIcon.SizeMode = PictureBoxSizeMode.CenterImage;
        picIcon.Image = SystemIcons.Warning.ToBitmap();

        string fileName = Path.GetFileName(targetPath);
        if (string.IsNullOrEmpty(fileName)) fileName = targetPath;

        Label lblTitle = new Label();
        lblTitle.Text = fileName;
        lblTitle.Location = new Point(58, 14);
        lblTitle.Size = new Size(460, 20);
        lblTitle.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);
        lblTitle.ForeColor = Color.FromArgb(24, 28, 32);
        lblTitle.AutoEllipsis = true;

        Label lblSub = new Label();
        lblSub.Text = targetPath;
        lblSub.Location = new Point(59, 34);
        lblSub.Size = new Size(459, 16);
        lblSub.Font = new Font("Segoe UI", 8.5F);
        lblSub.ForeColor = Color.FromArgb(110, 118, 128);
        lblSub.AutoEllipsis = true;

        Panel infoCard = new Panel();
        infoCard.Location = new Point(18, 56);
        infoCard.Size = new Size(504, 80);
        infoCard.BackColor = Color.White;
        infoCard.Paint += delegate(object s, PaintEventArgs pe) {
            using (Pen p = new Pen(Color.FromArgb(226, 230, 236), 1)) {
                pe.Graphics.DrawRectangle(p, 0, 0, infoCard.Width - 1, infoCard.Height - 1);
            }
        };

        Label lblMsg = new Label();
        lblMsg.Text = isInitialBatch ? details :
            string.Format("'{0}' cannot be deleted because an application is using it ({1}).\n\nChoose 'Kill & Delete' to close the lock, or 'Skip' / 'Skip All' to delete the other non-locked files.", fileName, details);
        lblMsg.Location = new Point(12, 10);
        lblMsg.Size = new Size(480, 60);
        lblMsg.Font = new Font("Segoe UI", 8.5F);
        lblMsg.ForeColor = Color.FromArgb(60, 66, 74);
        infoCard.Controls.Add(lblMsg);

        Panel bottomBar = new Panel();
        bottomBar.Dock = DockStyle.Bottom;
        bottomBar.Height = 52;
        bottomBar.BackColor = Color.FromArgb(242, 244, 248);

        Panel borderTop = new Panel();
        borderTop.Dock = DockStyle.Top;
        borderTop.Height = 1;
        borderTop.BackColor = Color.FromArgb(226, 230, 236);
        bottomBar.Controls.Add(borderTop);

        Button btnKill = new Button();
        btnKill.Text = "Kill & Delete";
        btnKill.Size = new Size(110, 32);
        btnKill.Location = new Point(55, 10);
        btnKill.FlatStyle = FlatStyle.Flat;
        btnKill.BackColor = Color.FromArgb(215, 45, 35);
        btnKill.ForeColor = Color.White;
        btnKill.Font = new Font("Segoe UI", 8.8F, FontStyle.Bold);
        btnKill.Cursor = Cursors.Hand;
        btnKill.FlatAppearance.BorderSize = 0;
        btnKill.Click += delegate { this.Result = DeletePromptResult.KillAndDelete; this.Close(); };

        Button btnSkip = new Button();
        btnSkip.Text = "Skip";
        btnSkip.Size = new Size(80, 32);
        btnSkip.Location = new Point(175, 10);
        btnSkip.FlatStyle = FlatStyle.Flat;
        btnSkip.BackColor = Color.FromArgb(226, 230, 236);
        btnSkip.ForeColor = Color.FromArgb(40, 45, 50);
        btnSkip.Font = new Font("Segoe UI", 8.8F, FontStyle.Bold);
        btnSkip.Cursor = Cursors.Hand;
        btnSkip.FlatAppearance.BorderSize = 0;
        btnSkip.Click += delegate { this.Result = DeletePromptResult.Skip; this.Close(); };

        Button btnSkipAll = new Button();
        btnSkipAll.Text = "Skip All";
        btnSkipAll.Size = new Size(88, 32);
        btnSkipAll.Location = new Point(265, 10);
        btnSkipAll.FlatStyle = FlatStyle.Flat;
        btnSkipAll.BackColor = Color.FromArgb(226, 230, 236);
        btnSkipAll.ForeColor = Color.FromArgb(40, 45, 50);
        btnSkipAll.Font = new Font("Segoe UI", 8.8F, FontStyle.Bold);
        btnSkipAll.Cursor = Cursors.Hand;
        btnSkipAll.FlatAppearance.BorderSize = 0;
        btnSkipAll.Click += delegate { this.Result = DeletePromptResult.SkipAll; this.Close(); };

        Button btnCancel = new Button();
        btnCancel.Text = "Cancel";
        btnCancel.Size = new Size(80, 32);
        btnCancel.Location = new Point(363, 10);
        btnCancel.FlatStyle = FlatStyle.Flat;
        btnCancel.BackColor = Color.FromArgb(226, 230, 236);
        btnCancel.ForeColor = Color.FromArgb(40, 45, 50);
        btnCancel.Font = new Font("Segoe UI", 8.8F, FontStyle.Regular);
        btnCancel.Cursor = Cursors.Hand;
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += delegate { this.Result = DeletePromptResult.Cancel; this.Close(); };

        bottomBar.Controls.Add(btnKill);
        bottomBar.Controls.Add(btnSkip);
        bottomBar.Controls.Add(btnSkipAll);
        bottomBar.Controls.Add(btnCancel);

        this.Controls.Add(picIcon);
        this.Controls.Add(lblTitle);
        this.Controls.Add(lblSub);
        this.Controls.Add(infoCard);
        this.Controls.Add(bottomBar);

        this.AcceptButton = btnKill;
        this.CancelButton = btnCancel;
    }
}
