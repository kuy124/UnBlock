using System;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

// Partial form part: window layout construction and small UI helpers.
public partial class UnlockerForm {

    private void InitializeComponent() {
        this.Text = "UnBlock File & Folder Unlocker";
        this.Size = new Size(830, 560);
        this.MinimumSize = new Size(790, 480);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.FromArgb(244, 246, 248);
        this.toolTip = new ToolTip();

        try {
            IntPtr hIcon = ExtractIcon(IntPtr.Zero, "shell32.dll", 239);
            if (hIcon != IntPtr.Zero) { this.Icon = Icon.FromHandle(hIcon); }
        } catch { }

        // ---------- Header ----------
        Panel headerPanel = new Panel() {
            Dock = DockStyle.Top,
            Height = 66,
            Width = 744,
            BackColor = Color.FromArgb(30, 39, 46)
        };

        Label lblAppTitle = new Label() {
            Text = "UnBlock",
            Location = new Point(16, 8),
            Size = new Size(220, 26),
            Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
            ForeColor = Color.White
        };

        lblTarget = new Label() {
            Location = new Point(18, 37),
            Size = new Size(400, 20),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = Color.FromArgb(189, 195, 199),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        FlowLayoutPanel headerButtons = new FlowLayoutPanel() {
            Dock = DockStyle.Right,
            WrapContents = false,
            BackColor = Color.FromArgb(30, 39, 46),
            Padding = new Padding(0, 15, 14, 0)
        };

        btnAddFile = MakeHeaderButton("+ File", Color.FromArgb(52, 152, 219));
        btnAddFile.Click += BtnAddFile_Click;
        toolTip.SetToolTip(btnAddFile, "Browse and add a file to analyze.");

        btnAddFolder = MakeHeaderButton("+ Folder", Color.FromArgb(41, 128, 185));
        btnAddFolder.Click += BtnAddFolder_Click;
        toolTip.SetToolTip(btnAddFolder, "Browse and add a folder to analyze.");

        headerButtons.Controls.Add(btnAddFile);
        headerButtons.Controls.Add(btnAddFolder);

        if (!isAdmin) {
            btnElevate = MakeHeaderButton("Elevate", Color.FromArgb(241, 196, 15));
            btnElevate.ForeColor = Color.Black;
            btnElevate.Click += BtnElevate_Click;
            toolTip.SetToolTip(btnElevate, "Restart UnBlock as Administrator to enable complete security adjustments.");
            headerButtons.Controls.Add(btnElevate);
        }

        int headerFlowWidth = headerButtons.Padding.Horizontal;
        foreach (Control c in headerButtons.Controls) headerFlowWidth += c.Width + c.Margin.Horizontal;
        headerButtons.Width = headerFlowWidth + 2;

        headerPanel.Controls.Add(lblAppTitle);
        headerPanel.Controls.Add(lblTarget);
        headerPanel.Controls.Add(headerButtons);

        // ---------- Toolbar (search + status + admin badge) ----------
        Panel toolbarPanel = new Panel() {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Color.White
        };
        Panel toolbarBorder = new Panel() { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(227, 231, 234) };

        Panel filterHost = new Panel() { Dock = DockStyle.Left, Width = 276, BackColor = Color.White };
        txtFilter = new TextBox() {
            Location = new Point(16, 12),
            Size = new Size(250, 25),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };
        txtFilter.TextChanged += TxtFilter_TextChanged;
        toolTip.SetToolTip(txtFilter, "Filter results by process name, PID, or path.");
        filterHost.Controls.Add(txtFilter);

        lblStatus = new Label() {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = Color.DimGray,
            Text = "Ready."
        };
        Panel statusHost = new Panel() { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 8, 0) };
        statusHost.Controls.Add(lblStatus);

        lblAdminState = new Label() {
            Text = isAdmin ? "Administrator" : "Standard user",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = isAdmin ? Color.FromArgb(39, 174, 96) : Color.FromArgb(211, 84, 0),
            TextAlign = ContentAlignment.MiddleRight
        };
        FlowLayoutPanel adminHost = new FlowLayoutPanel() {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Color.White,
            Padding = new Padding(0, 14, 16, 0)
        };
        adminHost.Controls.Add(lblAdminState);

        toolbarPanel.Controls.Add(statusHost);
        toolbarPanel.Controls.Add(adminHost);
        toolbarPanel.Controls.Add(filterHost);
        toolbarPanel.Controls.Add(toolbarBorder);

        // ---------- Progress strip ----------
        progressBar = new ProgressBar() {
            Dock = DockStyle.Top,
            Height = 4,
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };

        // ---------- Action bar ----------
        Panel actionBar = new Panel() {
            Dock = DockStyle.Bottom,
            Height = 62,
            BackColor = Color.White
        };
        Panel actionBarBorder = new Panel() { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(227, 231, 234) };

        FlowLayoutPanel actionFlow = new FlowLayoutPanel() {
            Dock = DockStyle.Fill,
            WrapContents = false,
            BackColor = Color.White,
            Padding = new Padding(12, 12, 0, 0)
        };

        btnUnlock = MakeActionButton("Unlock Selected", Color.FromArgb(39, 174, 96), Color.White);
        btnUnlock.Click += BtnUnlock_Click;
        toolTip.SetToolTip(btnUnlock, "Close the file handle(s) held by the selected process.");

        btnUnlockAll = MakeActionButton("Unlock All", Color.FromArgb(30, 132, 73), Color.White);
        btnUnlockAll.Click += BtnUnlockAll_Click;
        toolTip.SetToolTip(btnUnlockAll, "Close every locking handle found on the target(s).");

        btnKill = MakeActionButton("Kill Process", Color.FromArgb(192, 57, 43), Color.White);
        btnKill.Click += BtnKill_Click;
        toolTip.SetToolTip(btnKill, "Forcibly terminate the selected locking program.");

        btnKillAll = MakeActionButton("Kill All", Color.FromArgb(146, 43, 33), Color.White);
        btnKillAll.Click += BtnKillAll_Click;
        toolTip.SetToolTip(btnKillAll, "Forcibly terminate ALL processes shown in the list.");

        btnForceDelete = MakeActionButton("Force Delete", Color.FromArgb(202, 111, 30), Color.White);
        btnForceDelete.Click += BtnForceDelete_Click;
        toolTip.SetToolTip(btnForceDelete, "Delete the target(s): kills locks, resets ACLs, falls back to delete-on-reboot.");

        actionFlow.Controls.Add(btnUnlock);
        actionFlow.Controls.Add(btnUnlockAll);
        actionFlow.Controls.Add(MakeActionSeparator());
        actionFlow.Controls.Add(btnKill);
        actionFlow.Controls.Add(btnKillAll);
        actionFlow.Controls.Add(MakeActionSeparator());
        actionFlow.Controls.Add(btnForceDelete);

        Panel closeHost = new Panel() {
            Dock = DockStyle.Right,
            Width = 96,
            BackColor = Color.White,
            Padding = new Padding(0, 12, 10, 12)
        };
        btnClose = MakeActionButton("Close", Color.FromArgb(149, 165, 166), Color.Black);
        btnClose.Dock = DockStyle.Fill;
        btnClose.Click += delegate { this.Close(); };
        closeHost.Controls.Add(btnClose);

        actionBar.Controls.Add(actionFlow);
        actionBar.Controls.Add(closeHost);
        actionBar.Controls.Add(actionBarBorder);

        // ---------- Results list ----------
        Panel listHost = new Panel() {
            Dock = DockStyle.Fill,
            BackColor = this.BackColor,
            Padding = new Padding(12, 10, 12, 10)
        };

        imageList = new ImageList();
        imageList.ImageSize = new Size(16, 16);
        imageList.ColorDepth = ColorDepth.Depth32Bit;

        listView = new ListView() {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            GridLines = false,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            BorderStyle = BorderStyle.FixedSingle,
            SmallImageList = imageList
        };
        try {
            typeof(ListView).InvokeMember("DoubleBuffered",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                null, listView, new object[] { true });
        } catch { }

        listView.Columns.Add("Process", 175);
        listView.Columns.Add("PID", 55);
        listView.Columns.Add("Severity", 135);
        listView.Columns.Add("Process Path", 300);
        listView.SelectedIndexChanged += delegate { UpdateButtonStates(); };
        listView.ClientSizeChanged += delegate { FillLastColumn(); };
        listView.DoubleClick += ListView_DoubleClick;

        ContextMenu contextMenu = new ContextMenu();
        MenuItem openLocationItem = new MenuItem("Open Process File Location");
        openLocationItem.Click += delegate {
            if (listView.SelectedItems.Count > 0) {
                var pItem = listView.SelectedItems[0].Tag as ProcessItem;
                if (pItem != null && File.Exists(pItem.Path)) {
                    try { Process.Start("explorer.exe", "/select,\"" + pItem.Path + "\""); } catch { }
                }
            }
        };
        contextMenu.MenuItems.Add(openLocationItem);
        listView.ContextMenu = contextMenu;

        listHost.Controls.Add(listView);

        // ---------- Assemble (reverse docking order: last added is laid out first) ----------
        this.Controls.Add(listHost);
        this.Controls.Add(progressBar);
        this.Controls.Add(toolbarPanel);
        this.Controls.Add(actionBar);
        this.Controls.Add(headerPanel);

        this.CancelButton = btnClose;

        try {
            SendMessage(txtFilter.Handle, EM_SETCUEBANNER, 1, "Search process, PID or path...");
        } catch { }

        UpdateButtonStates();
    }

    private Size MeasureButtonText(Button btn, string text, int paddingX, int height) {
        int textWidth = 60;
        try {
            using (Graphics g = btn.CreateGraphics()) {
                SizeF ts = g.MeasureString(text, btn.Font);
                textWidth = (int)Math.Ceiling(ts.Width);
            }
        } catch { }
        return new Size(textWidth + paddingX, height);
    }

    private Button MakeHeaderButton(string text, Color backColor) {
        Button btn = new Button() {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0)
        };
        btn.Size = MeasureButtonText(btn, text, 26, 34);
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    private Button MakeActionButton(string text, Color backColor, Color foreColor) {
        Button btn = new Button() {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = foreColor,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 6, 0)
        };
        btn.Size = MeasureButtonText(btn, text, 30, 40);
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(backColor, 0.05f);
        btn.EnabledChanged += delegate {
            btn.BackColor = btn.Enabled ? backColor : Color.FromArgb(189, 195, 199);
            btn.ForeColor = btn.Enabled ? foreColor : Color.FromArgb(120, 125, 130);
        };
        return btn;
    }

    private Panel MakeActionSeparator() {
        return new Panel() {
            Width = 1,
            Height = 28,
            BackColor = Color.FromArgb(208, 213, 217),
            Margin = new Padding(7, 6, 7, 0)
        };
    }

    private void FillLastColumn() {
        if (listView.Columns.Count < 4 || listView.Width == 0) return;
        int others = 0;
        for (int i = 0; i < listView.Columns.Count - 1; i++) others += listView.Columns[i].Width;
        int remaining = listView.ClientSize.Width - others - SystemInformation.VerticalScrollBarWidth;
        if (remaining > 80) listView.Columns[listView.Columns.Count - 1].Width = remaining;
    }

    private void SetStatus(string text) {
        SetStatus(text, Color.DimGray);
    }

    private void SetStatus(string text, Color color) {
        if (lblStatus == null) return;
        lblStatus.Text = text;
        lblStatus.ForeColor = color;
    }

    private void ShowListMessage(string message) {
        listView.BeginUpdate();
        listView.Items.Clear();
        ListViewItem emptyItem = new ListViewItem(new string[] { "", "", "", message });
        emptyItem.ForeColor = Color.Gray;
        listView.Items.Add(emptyItem);
        listView.EndUpdate();
    }
}
