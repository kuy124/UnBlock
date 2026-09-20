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
        this.Size = new Size(900, 620);
        this.MinimumSize = new Size(660, 520);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = theme.Canvas;
        this.toolTip = new ToolTip();
        this.AllowDrop = true;
        this.DragEnter += UnlockerForm_DragEnter;
        this.DragDrop += UnlockerForm_DragDrop;

        try {
            IntPtr hIcon = ExtractIcon(IntPtr.Zero, "shell32.dll", 239);
            if (hIcon != IntPtr.Zero) { this.Icon = Icon.FromHandle(hIcon); }
        } catch { }

        // ---------- Header ----------
        // A two-column table: the text column fills and the button column auto-sizes. Table cells
        // cannot overlap, so the target path can never run under the header buttons.
        headerPanel = new Panel() {
            Dock = DockStyle.Top,
            Height = 68,
            BackColor = theme.Header,
            Tag = SurfaceRole.Header
        };

        Font headerFont = new Font("Segoe UI", 9F, FontStyle.Bold);

        TableLayoutPanel headerLayout = new TableLayoutPanel() {
            Dock = DockStyle.Fill,
            BackColor = theme.Header,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(14, 0, 12, 0),
            Margin = new Padding(0),
            Tag = SurfaceRole.Header
        };
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        lblAppTitle = new Label() {
            Text = "UnBlock",
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
            ForeColor = theme.HeaderText,
            BackColor = theme.Header,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(0, 8, 8, 0),
            Tag = SurfaceRole.HeaderText
        };

        lblTarget = new Label() {
            Text = "No files or folders selected",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = theme.HeaderMuted,
            BackColor = theme.Header,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 0, 8, 8),
            Tag = SurfaceRole.HeaderMuted
        };

        Panel headerTextHost = new Panel() {
            Dock = DockStyle.Fill,
            BackColor = theme.Header,
            Margin = new Padding(0),
            AutoSize = false
        };
        headerTextHost.Tag = SurfaceRole.Header;
        // Fill is added before Top in z-order via Controls.Add order: add target (fill) then title (top).
        headerTextHost.Controls.Add(lblTarget);
        headerTextHost.Controls.Add(lblAppTitle);

        headerButtons = new FlowLayoutPanel() {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = theme.Header,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        headerButtons.Tag = SurfaceRole.Header;

        UiButtonFactory.Roles addFileRoles = new UiButtonFactory.Roles { Kind = UiButtonFactory.Role.Primary };
        btnAddFile = UiButtonFactory.Filled(headerButtons, addFileRoles, theme, "+ File", headerFont, toolTip, "Browse and add a file to analyze.");
        btnAddFile.Click += BtnAddFile_Click;

        UiButtonFactory.Roles addFolderRoles = new UiButtonFactory.Roles { Kind = UiButtonFactory.Role.Primary };
        btnAddFolder = UiButtonFactory.Filled(headerButtons, addFolderRoles, theme, "+ Folder", headerFont, toolTip, "Browse and add a folder to analyze.");
        btnAddFolder.Click += BtnAddFolder_Click;

        headerButtons.Controls.Add(btnAddFile);
        headerButtons.Controls.Add(btnAddFolder);

        if (!isAdmin) {
            UiButtonFactory.Roles elevateRoles = new UiButtonFactory.Roles { Kind = UiButtonFactory.Role.Outline };
            btnElevate = UiButtonFactory.Outline(headerButtons, elevateRoles, theme, "Elevate", headerFont, toolTip, "Restart UnBlock as Administrator to enable complete security adjustments.");
            btnElevate.Click += BtnElevate_Click;
            headerButtons.Controls.Add(btnElevate);
        }

        btnThemeToggle = UiButtonFactory.Outline(headerButtons, new UiButtonFactory.Roles { Kind = UiButtonFactory.Role.Outline }, theme, ThemeToggleText(), headerFont, toolTip, "Switch between the light and dark interface.");
        btnThemeToggle.Click += BtnThemeToggle_Click;
        headerButtons.Controls.Add(btnThemeToggle);

        // Center the button row vertically inside the header without absolute positions.
        Panel headerButtonHost = new Panel() {
            Dock = DockStyle.Fill,
            BackColor = theme.Header,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Tag = SurfaceRole.Header
        };
        headerButtonHost.Controls.Add(headerButtons);
        headerButtonHost.Layout += delegate {
            headerButtons.Location = new Point(0, Math.Max(0, (headerButtonHost.Height - headerButtons.Height) / 2));
        };

        foreach (Control c in headerButtons.Controls) c.Margin = new Padding(6, 0, 0, 0);

        headerLayout.Controls.Add(headerTextHost, 0, 0);
        headerLayout.Controls.Add(headerButtonHost, 1, 0);
        headerPanel.Controls.Add(headerLayout);

        // ---------- Toolbar (search + status + admin badge) ----------
        Panel toolbarPanel = new Panel() {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = theme.Surface,
            Tag = SurfaceRole.Surface
        };
        toolbarBorder = new Panel() { Dock = DockStyle.Bottom, Height = 1, BackColor = theme.Border, Tag = SurfaceRole.Border };

        Panel filterHost = new Panel() { Dock = DockStyle.Left, Width = 276, MinimumSize = new Size(160, 0), BackColor = theme.Surface, Padding = new Padding(16, 12, 10, 0), Tag = SurfaceRole.Surface };
        txtFilter = new TextBox() {
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            BorderStyle = BorderStyle.FixedSingle
        };
        txtFilter.TextChanged += TxtFilter_TextChanged;
        toolTip.SetToolTip(txtFilter, "Filter results by process name, PID, or path.");
        filterHost.Controls.Add(txtFilter);

        lblStatus = new Label() {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = theme.TextMuted,
            BackColor = theme.Surface,
            Text = "Ready.",
            Tag = SurfaceRole.StatusLabel
        };
        Panel statusHost = new Panel() { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 8, 0), BackColor = theme.Surface, Tag = SurfaceRole.Surface };
        statusHost.Controls.Add(lblStatus);

        lblAdminState = new Label() {
            Text = isAdmin ? "Administrator" : "Standard user",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = isAdmin ? theme.SuccessText : theme.DangerText,
            BackColor = theme.Surface,
            TextAlign = ContentAlignment.MiddleRight,
            Tag = SurfaceRole.Surface
        };
        FlowLayoutPanel adminHost = new FlowLayoutPanel() {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = theme.Surface,
            Padding = new Padding(0, 14, 16, 0),
            Tag = SurfaceRole.Surface
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
        // One wrap strip: buttons keep their full label width and wrap to a new row when the
        // window is too narrow, so a label can never be clipped. The bar grows upward to fit
        // whatever number of rows the wrap produces.
        actionBar = new Panel() {
            Dock = DockStyle.Bottom,
            Height = 0,
            BackColor = theme.Surface,
            Tag = SurfaceRole.Surface
        };
        actionBarBorder = new Panel() { Dock = DockStyle.Top, Height = 1, BackColor = theme.Border, Tag = SurfaceRole.Border };

        UiButtonFactory.Roles primaryRoles = new UiButtonFactory.Roles { Kind = UiButtonFactory.Role.Primary };
        UiButtonFactory.Roles dangerRoles = new UiButtonFactory.Roles { Kind = UiButtonFactory.Role.Danger };
        UiButtonFactory.Roles outlineRoles = new UiButtonFactory.Roles { Kind = UiButtonFactory.Role.Outline };
        Font actionFont = new Font("Segoe UI", 9F, FontStyle.Bold);

        actionStrip = UiLayout.NewWrapStrip(theme, UiMetrics.ActionBarPadX, UiMetrics.ActionBarPadY);

        // Scroll host: normally invisible, it only shows a scrollbar if the wrapped rows exceed
        // the capped bar height, so buttons stay reachable without covering the results list.
        actionStripHost = new Panel() {
            Dock = DockStyle.Fill,
            BackColor = theme.Surface,
            AutoScroll = false,
            Padding = new Padding(0),
            Tag = SurfaceRole.Surface
        };
        actionStripHost.Controls.Add(actionStrip);

        // Buttons are parented to the strip so their font and tooltips are realized before
        // measurement; the frequent ones stay in the strip and the rest move to the More menu.
        btnUnlock = UiButtonFactory.Filled(actionStrip, primaryRoles, theme, "Unlock Selected", actionFont, toolTip, "Close the file handle(s) held by the selected process.");
        btnUnlock.Click += BtnUnlock_Click;

        btnKill = UiButtonFactory.Filled(actionStrip, dangerRoles, theme, "Kill Selected", actionFont, toolTip, "Forcibly terminate the selected locking program.");
        btnKill.Click += BtnKill_Click;

        btnKillRecycle = UiButtonFactory.Filled(actionStrip, primaryRoles, theme, "Kill & Recycle", actionFont, toolTip, "Terminate detected lockers, then move the target(s) to the Recycle Bin. If the drive has no Recycle Bin, the item is left in place.");
        btnKillRecycle.Click += BtnForceDelete_Click;

        btnKillRename = UiButtonFactory.Outline(actionStrip, outlineRoles, theme, "Kill & Rename", actionFont, toolTip, "Terminate detected lockers, then rename the selected target.");
        btnKillRename.Click += delegate { BeginKillRename(); };

        btnKillMove = UiButtonFactory.Outline(actionStrip, outlineRoles, theme, "Kill & Move", actionFont, toolTip, "Terminate detected lockers, then move the selected target(s).");
        btnKillMove.Click += delegate { BeginKillMoveOrCopy(false); };

        btnKillCopy = UiButtonFactory.Outline(actionStrip, outlineRoles, theme, "Kill & Copy", actionFont, toolTip, "Terminate detected lockers, then copy the selected target(s).");
        btnKillCopy.Click += delegate { BeginKillMoveOrCopy(true); };

        btnReload = UiButtonFactory.Outline(actionStrip, outlineRoles, theme, "Reload", actionFont, toolTip, "Scan the current targets again.");
        btnReload.Click += delegate { StartAsyncScan(true); };

        // The strip holds the frequent actions only so it stays to one row at normal widths.
        Button[] stripButtons = new Button[] {
            btnUnlock, btnKill, btnKillRecycle, btnKillRename, btnKillMove, btnKillCopy, btnReload
        };
        foreach (Button button in stripButtons) {
            button.Margin = new Padding(0, 0, UiMetrics.RowGap, UiMetrics.RowGap);
            actionStrip.Controls.Add(button);
        }

        // The remaining, less frequent actions live in the More menu: same real commands, but
        // they stop competing for the strip's width. This is what keeps the bar uncramped.
        btnMore = UiButtonFactory.Outline(actionStrip, outlineRoles, theme, "More", actionFont, toolTip, "More actions: unlock or kill everything, permanent delete, delete at restart, repair permissions, and window controls.");
        btnMore.Margin = new Padding(0, 0, UiMetrics.RowGap, UiMetrics.RowGap);
        actionStrip.Controls.Add(btnMore);

        BuildMoreMenu(theme);

        closeHost = new Panel() {
            Dock = DockStyle.Right,
            Width = 116,
            BackColor = theme.Surface,
            Padding = new Padding(0, UiMetrics.ActionBarPadY, UiMetrics.ActionBarPadX, UiMetrics.ActionBarPadY),
            Tag = SurfaceRole.Surface
        };
        btnClose = UiButtonFactory.Outline(closeHost, outlineRoles, theme, "Close", this.Font, toolTip, "Close this window.");
        btnClose.Dock = DockStyle.Top;
        btnClose.Click += delegate { this.Close(); };
        closeHost.Controls.Add(btnClose);

        actionBar.Controls.Add(actionStripHost);
        actionBar.Controls.Add(closeHost);
        actionBar.Controls.Add(actionBarBorder);
        FitActionBar();

        // ---------- Results list ----------
        Panel listHost = new Panel() {
            Dock = DockStyle.Fill,
            BackColor = theme.Canvas,
            Padding = new Padding(12, 10, 12, 10),
            Tag = SurfaceRole.Canvas
        };

        imageList = new ImageList();
        imageList.ImageSize = new Size(16, 16);
        imageList.ColorDepth = ColorDepth.Depth32Bit;

        listView = new ListView() {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            GridLines = false,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = theme.Surface,
            ForeColor = theme.TextPrimary,
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
        listView.Columns.Add("Locked Path", 300);
        listView.SelectedIndexChanged += delegate { UpdateButtonStates(); };
        listView.ClientSizeChanged += delegate { FillLastColumn(); };
        listView.DoubleClick += delegate {
            if (listView.SelectedItems.Count > 0) {
                ProcessItem selected = listView.SelectedItems[0].Tag as ProcessItem;
                if (selected != null) ShowProcessDetails(selected);
            }
        };

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
        MenuItem detailsItem = new MenuItem("View Process Details");
        detailsItem.Click += delegate {
            if (listView.SelectedItems.Count > 0) {
                ProcessItem selected = listView.SelectedItems[0].Tag as ProcessItem;
                if (selected != null) ShowProcessDetails(selected);
            }
        };
        contextMenu.MenuItems.Add(detailsItem);
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

        // Re-fit the wrapping action bar whenever the width changes so rows never clip.
        this.Resize += delegate { FitActionBar(); };
        this.Shown += delegate { FitActionBar(); };

        try {
            SendMessage(txtFilter.Handle, EM_SETCUEBANNER, 1, "Search process, PID or path...");
        } catch { }

        UpdateButtonStates();
        FitActionBar();
    }

    // Grows the bottom action bar so every wrapped row of buttons stays visible. Called after
    // construction, on resize, and after a theme change. Runs a two-pass layout because the
    // strip only knows how many rows it wrapped to after it has been laid out at the new width.
    // The bar is capped so it can never eat the results list; past the cap the strip scrolls,
    // which keeps every button reachable without clipping or covering the list.
    private void FitActionBar() {
        if (actionBar == null || actionStrip == null || actionStripHost == null) return;
        actionStrip.PerformLayout();
        int oneRow = UiMetrics.ButtonHeight + (UiMetrics.ActionBarPadY * 2);
        int cap = oneRow + (int)Math.Round(2.5 * (UiMetrics.ButtonHeight + UiMetrics.RowGap));
        int wanted = Math.Max(actionStrip.Height, oneRow);
        int target = Math.Min(wanted, cap);

        actionStripHost.AutoScroll = wanted > cap;
        if (actionBar.Height != target) {
            actionBar.Height = target;
            actionBar.PerformLayout();
            actionStrip.PerformLayout();
            int settled = Math.Min(Math.Max(actionStrip.Height, oneRow), cap);
            if (actionBar.Height != settled) {
                actionBar.Height = settled;
                actionBar.PerformLayout();
            }
        }
    }

    // Less frequent and destructive actions live here. Real menu items with real handlers, so
    // nothing is a dead control, and the command strip stays uncramped.
    private void BuildMoreMenu(UiTheme theme) {
        moreMenu = new ContextMenuStrip {
            ShowImageMargin = false,
            BackColor = theme.Surface,
            ForeColor = theme.TextPrimary,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };

        miUnlockAll = AddMoreItem("Unlock All", "Close every locking handle found on the target(s).", BtnUnlockAll_Click);
        miKillAll = AddMoreItem("Kill All", "Forcibly terminate all processes shown in the list.", BtnKillAll_Click);
        moreMenu.Items.Add(new ToolStripSeparator());
        miKillPermanent = AddMoreItem("Kill & Permanent Delete", "Permanently delete the target(s). This cannot be undone.", delegate { BeginKillDelete(DeleteMode.Permanent, false); });
        miKillRestart = AddMoreItem("Kill & Delete at Restart", "Schedule the target(s) for deletion at restart.", delegate { BeginKillDelete(DeleteMode.OnRestart, false); });
        miRepairPermissions = AddMoreItem("Repair Permissions", "Explicitly change permissions without terminating processes.", delegate { RepairTargetPermissions(); });
        moreMenu.Items.Add(new ToolStripSeparator());
        miCancelScan = AddMoreItem("Cancel Scan", "Stop the current scan and keep completed results.", BtnCancelScan_Click);
        miCancelAction = AddMoreItem("Cancel Action", "Stop pending termination and file work.", BtnCancelAction_Click);
        miClearTargets = AddMoreItem("Clear Targets", "Remove all queued files and folders.", BtnClearTargets_Click);

        btnMore.Click += delegate { moreMenu.Show(btnMore, new Point(0, btnMore.Height)); };
    }

    private ToolStripMenuItem AddMoreItem(string text, string tooltip, EventHandler handler) {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.ToolTipText = tooltip;
        item.Click += handler;
        moreMenu.Items.Add(item);
        return item;
    }

    private string ThemeToggleText() {
        return theme.IsDark ? "Light" : "Dark";
    }

    // Re-applies the active theme to every themed control in the window. Buttons are restyled by
    // role and surfaces by SurfaceRole, so nothing keeps the previous theme's colors after a
    // toggle. Runs one layout pass and one repaint to avoid flicker.
    private void ApplyTheme(UiTheme next) {
        theme = next;
        SuspendLayout();
        try {
            this.BackColor = theme.Canvas;
            UiTheming.Apply(this, theme, StatusColor);
            if (lblStatus != null) lblStatus.ForeColor = StatusColor(pendingStatusColor);
            if (lblAdminState != null) lblAdminState.ForeColor = isAdmin ? theme.SuccessText : theme.DangerText;
            if (btnThemeToggle != null) btnThemeToggle.Text = ThemeToggleText();
            ThemeMoreMenu(theme);
        } finally {
            ResumeLayout(true);
        }
        UpdateButtonStates();
        FitActionBar();
        this.Invalidate(true);
        this.Update();
    }

    private void ThemeMoreMenu(UiTheme theme) {
        if (moreMenu == null) return;
        moreMenu.BackColor = theme.Surface;
        moreMenu.ForeColor = theme.TextPrimary;
        foreach (ToolStripItem item in moreMenu.Items) {
            item.ForeColor = theme.TextPrimary;
            item.BackColor = theme.Surface;
        }
    }

    // Distributes the list width across all columns by weight so none can clip, keeping a floor
    // per column. The first column gets the widest share because process names are the longest.
    private void FillLastColumn() {
        if (listView.Columns.Count < 4 || listView.Width == 0) return;
        int available = listView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
        if (available < 200) return;
        int[] weights = { 34, 12, 22, 32 };
        int[] floors = { 120, 48, 90, 120 };
        int floorSum = 0;
        for (int i = 0; i < floors.Length; i++) floorSum += floors[i];
        if (available <= floorSum) {
            for (int i = 0; i < listView.Columns.Count; i++) listView.Columns[i].Width = Math.Max(40, floors[i]);
            return;
        }
        int extra = available - floorSum;
        for (int i = 0; i < listView.Columns.Count; i++) {
            int width = floors[i] + (int)Math.Round(extra * (weights[i] / 100.0));
            listView.Columns[i].Width = width;
        }
    }

    private void SetStatus(string text) {
        SetStatus(text, theme.TextMuted);
    }

    private void SetStatus(string text, Color statusColor) {
        if (lblStatus == null) return;
        pendingStatusColor = SemanticStatus(statusColor);
        lblStatus.Text = text;
        lblStatus.ForeColor = StatusColor(pendingStatusColor);
    }

    // Normalizes whatever a caller passes into one semantic token, so a later theme change can
    // re-map it. Anything not recognized is treated as the neutral status color.
    private Color SemanticStatus(Color requested) {
        if (LightDanger.Equals(requested) || LightWarning.Equals(requested) || ThemeDangerText(theme).Equals(requested)) return LightDanger;
        if (LightSuccess.Equals(requested) || ThemeSuccessText(theme).Equals(requested)) return LightSuccess;
        return LightMuted;
    }

    private static Color ThemeDangerText(UiTheme t) { return t.DangerText; }
    private static Color ThemeSuccessText(UiTheme t) { return t.SuccessText; }

    // Maps a semantic status token onto the active theme so dark mode keeps AA contrast.
    private Color StatusColor(Color token) {
        if (LightDanger.Equals(token)) return theme.DangerText;
        if (LightSuccess.Equals(token)) return theme.SuccessText;
        return theme.TextMuted;
    }

    private Color pendingStatusColor = Color.FromArgb(127, 140, 141);

    private static readonly Color LightDanger = Color.FromArgb(192, 57, 43);
    private static readonly Color LightSuccess = Color.FromArgb(39, 174, 96);
    private static readonly Color LightWarning = Color.FromArgb(211, 84, 0);
    private static readonly Color LightMuted = Color.FromArgb(127, 140, 141);

    private void ShowListMessage(string message) {
        listView.BeginUpdate();
        listView.Items.Clear();
        ListViewItem emptyItem = new ListViewItem(new string[] { "", "", "", message });
        emptyItem.ForeColor = theme.TextMuted;
        listView.Items.Add(emptyItem);
        listView.EndUpdate();
    }
}
