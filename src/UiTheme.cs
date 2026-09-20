using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

// Palette and metrics for the main window, defined once and shared by both themes.
// A calm technical-tool look: neutral greys carry the window, one accent marks the primary
// action, danger/success carry meaning only. Reasons for each choice live in DESIGN.md.
internal sealed class UiTheme {
    internal enum Mode { Light, Dark }

    internal Mode Kind { get; private set; }

    // Surfaces
    internal Color Canvas { get; private set; }
    internal Color Surface { get; private set; }
    internal Color Header { get; private set; }

    // Text and lines
    internal Color TextPrimary { get; private set; }
    internal Color TextMuted { get; private set; }
    internal Color Border { get; private set; }
    internal Color HeaderText { get; private set; }
    internal Color HeaderMuted { get; private set; }

    // Filled actions (solid fill with white text)
    internal Color AccentFill { get; private set; }
    internal Color AccentFillHover { get; private set; }
    internal Color DangerFill { get; private set; }
    internal Color DangerFillHover { get; private set; }
    internal Color SuccessFill { get; private set; }
    internal Color SuccessFillHover { get; private set; }

    // Neutral (outline) actions
    internal Color NeutralFill { get; private set; }
    internal Color NeutralFillHover { get; private set; }
    internal Color NeutralText { get; private set; }

    // Status text and focus
    internal Color AccentText { get; private set; }
    internal Color DangerText { get; private set; }
    internal Color SuccessText { get; private set; }
    internal Color FocusRing { get; private set; }
    internal Color DisabledFill { get; private set; }
    internal Color DisabledText { get; private set; }

    internal bool IsDark { get { return Kind == Mode.Dark; } }

    private UiTheme() { }

    internal static UiTheme Light { get { return light; } }
    internal static UiTheme Dark { get { return dark; } }

    internal static UiTheme For(Mode mode) { return mode == Mode.Dark ? dark : light; }

    private static readonly UiTheme light = new UiTheme {
        Kind = Mode.Light,
        Canvas = Rgb(0xF1, 0xF3, 0xF5),
        Surface = Rgb(0xFF, 0xFF, 0xFF),
        Header = Rgb(0x1E, 0x27, 0x2E),
        TextPrimary = Rgb(0x1E, 0x27, 0x2E),
        TextMuted = Rgb(0x55, 0x60, 0x6B),
        Border = Rgb(0xDC, 0xE1, 0xE6),
        HeaderText = Rgb(0xFF, 0xFF, 0xFF),
        HeaderMuted = Rgb(0xBD, 0xC3, 0xC7),
        AccentFill = Rgb(0x25, 0x63, 0xEB),
        AccentFillHover = Rgb(0x1D, 0x4E, 0xD8),
        DangerFill = Rgb(0xC0, 0x39, 0x2B),
        DangerFillHover = Rgb(0xA6, 0x30, 0x24),
        SuccessFill = Rgb(0x1E, 0x84, 0x49),
        SuccessFillHover = Rgb(0x19, 0x6F, 0x3D),
        NeutralFill = Rgb(0xFF, 0xFF, 0xFF),
        NeutralFillHover = Rgb(0xF1, 0xF3, 0xF5),
        NeutralText = Rgb(0x1E, 0x27, 0x2E),
        AccentText = Rgb(0x25, 0x63, 0xEB),
        DangerText = Rgb(0xC0, 0x39, 0x2B),
        SuccessText = Rgb(0x1E, 0x84, 0x49),
        FocusRing = Rgb(0x25, 0x63, 0xEB),
        DisabledFill = Rgb(0xE6, 0xEA, 0xEE),
        DisabledText = Rgb(0x8A, 0x93, 0x9C)
    };

    private static readonly UiTheme dark = new UiTheme {
        Kind = Mode.Dark,
        Canvas = Rgb(0x14, 0x18, 0x1C),
        Surface = Rgb(0x1B, 0x21, 0x26),
        Header = Rgb(0x10, 0x15, 0x19),
        TextPrimary = Rgb(0xE6, 0xEA, 0xEE),
        TextMuted = Rgb(0x9A, 0xA4, 0xAF),
        Border = Rgb(0x2B, 0x33, 0x3A),
        HeaderText = Rgb(0xE6, 0xEA, 0xEE),
        HeaderMuted = Rgb(0x9A, 0xA4, 0xAF),
        // Fills stay deep so white text keeps AA contrast on dark surfaces.
        AccentFill = Rgb(0x25, 0x63, 0xEB),
        AccentFillHover = Rgb(0x1D, 0x4E, 0xD8),
        DangerFill = Rgb(0xB4, 0x33, 0x25),
        DangerFillHover = Rgb(0x98, 0x2A, 0x1F),
        SuccessFill = Rgb(0x1E, 0x84, 0x49),
        SuccessFillHover = Rgb(0x19, 0x6F, 0x3D),
        NeutralFill = Rgb(0x23, 0x2A, 0x30),
        NeutralFillHover = Rgb(0x2B, 0x33, 0x3A),
        NeutralText = Rgb(0xE6, 0xEA, 0xEE),
        AccentText = Rgb(0x4C, 0x8D, 0xFF),
        DangerText = Rgb(0xEC, 0x6B, 0x5A),
        SuccessText = Rgb(0x3F, 0xB3, 0x6A),
        FocusRing = Rgb(0x4C, 0x8D, 0xFF),
        DisabledFill = Rgb(0x23, 0x2A, 0x30),
        DisabledText = Rgb(0x6B, 0x76, 0x81)
    };

    private static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }
}

// Fixed metrics so spacing and control sizes stay consistent and predictable.
internal static class UiMetrics {
    internal const int ButtonHeight = 34;
    internal const int ButtonPadX = 16;
    internal const int ButtonMinWidth = 88;
    internal const int RowGap = 8;
    internal const int RowLabelWidth = 104;
    internal const int ActionBarPadX = 14;
    internal const int ActionBarPadY = 10;
}

// One place that guarantees a button can never be narrower than its own label.
// Every button in the app is built here so the no-clipping rule holds everywhere.
internal static class UiLayout {
    // Smallest width that still shows the full label plus symmetric padding.
    internal static int RequiredWidth(string text, Font font) {
        Size textSize = TextRenderer.MeasureText(text ?? "", font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        return textSize.Width + (UiMetrics.ButtonPadX * 2);
    }

    internal static Size RequiredSize(string text, Font font) {
        return new Size(Math.Max(UiMetrics.ButtonMinWidth, RequiredWidth(text, font)), UiMetrics.ButtonHeight);
    }

    // Configures a button so it auto-grows to its text and can never shrink below it.
    // AutoSize grows to fit; MinimumSize is the hard floor the layout cannot squeeze past.
    internal static void EnforceNoClip(Button button) {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = new Padding(UiMetrics.ButtonPadX, 0, UiMetrics.ButtonPadX, 0);
        button.MinimumSize = RequiredSize(button.Text, button.Font);
    }

    // A wrap-enabled strip that never clips: buttons keep their minimum size and wrap to a
    // new row when the width runs out, instead of being squeezed. Dock=Top is required so the
    // panel is width-constrained and actually wraps (AutoSize alone would overflow instead).
    internal static FlowLayoutPanel NewWrapStrip(UiTheme theme, int padX, int padY) {
        return new FlowLayoutPanel {
            Dock = DockStyle.Top,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = theme.Surface,
            Padding = new Padding(padX, padY, padX, padY),
            Margin = new Padding(0),
            Tag = SurfaceRole.Surface
        };
    }

    // Recomputes a wrap strip's height after a resize so no wrapped row is ever cut off.
    // PreferredSize does not account for wrapping against the current width, so the strip's
    // realized Height (after layout) is what tells us how many rows it produced. The strip's
    // own Padding already supplies the bar's vertical inset, so the host matches it directly.
    internal static void FitWrapStrip(FlowLayoutPanel strip, Control host, Padding hostPad) {
        if (strip == null || host == null) return;
        strip.PerformLayout();
        int target = strip.Controls.Count == 0 ? UiMetrics.ButtonHeight + hostPad.Vertical : strip.Height;
        if (host.Height != target) host.Height = target;
        host.PerformLayout();
    }

    // Lays a fixed set of buttons in a single left-to-right row inside a host panel, sizing each
    // to its own label so none can clip, and wrapping to a second line if the row would overflow.
    // Used by the modal dialogs, whose buttons were previously positioned at fixed pixel offsets.
    // Lays a fixed set of buttons in rows inside a host panel, sizing each to its own label so
    // none can clip, and wrapping to a new line when the row would overflow. Two passes: measure
    // every button first, then position by the real row heights, so rows never overlap even when
    // the font is larger (high DPI) than the nominal button height. Re-runs on resize/font change
    // so a runtime font or DPI change cannot reintroduce overlap.
    internal static void LayoutButtonRow(Panel host, int height, int topInset, int sideInset, int gap, Button[] leftAligned) {
        if (host == null || leftAligned == null) return;
        ArrangeButtonRow(host, height, topInset, sideInset, gap, leftAligned);
        if (!(host.Tag is string) || (string)host.Tag != "uibtnrow") {
            host.Tag = "uibtnrow";
            host.Layout += delegate { ArrangeButtonRow(host, height, topInset, sideInset, gap, leftAligned); };
            foreach (Button b in leftAligned) if (b != null) b.FontChanged += delegate { ArrangeButtonRow(host, height, topInset, sideInset, gap, leftAligned); };
        }
    }

    private static void ArrangeButtonRow(Panel host, int height, int topInset, int sideInset, int gap, Button[] leftAligned) {
        if (arranging) return;
        arranging = true;
        host.SuspendLayout();
        try {
            foreach (Control c in host.Controls.Cast<Control>().Where(c => c is Button).ToList()) host.Controls.Remove(c);

            // Measure each button from its text and font directly, so widths are correct even
            // before the control has a handle or has been laid out.
            List<Button> buttons = new List<Button>();
            List<int> widths = new List<int>();
            List<int> heights = new List<int>();
            foreach (Button b in leftAligned) {
                if (b == null) continue;
                b.AutoSize = false;
                b.Padding = new Padding(0);
                Size want = RequiredSize(b.Text, b.Font);
                b.Size = want;
                b.MinimumSize = want;
                int txtH = TextRenderer.MeasureText(b.Text, b.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;
                int h = Math.Max(want.Height, txtH + 12);
                host.Controls.Add(b);
                buttons.Add(b);
                widths.Add(want.Width);
                heights.Add(h);
            }

            int maxRight = Math.Max(80, host.Width - sideInset);
            int x = sideInset;
            int y = topInset;
            int rowHeight = 0;
            for (int i = 0; i < buttons.Count; i++) {
                int w = widths[i];
                int h = heights[i];
                if (x != sideInset && x + w > maxRight) {
                    y += rowHeight + gap;
                    x = sideInset;
                    rowHeight = 0;
                }
                buttons[i].Size = new Size(w, h);
                buttons[i].Location = new Point(x, y);
                if (h > rowHeight) rowHeight = h;
                x += w + gap;
            }

            int needed = y + rowHeight + topInset;
            if (host.Height != Math.Max(height, needed)) host.Height = Math.Max(height, needed);
        } finally {
            host.ResumeLayout(true);
            arranging = false;
        }
    }

    [ThreadStatic]
    private static bool arranging;
}

// Buttons that need per-theme hover, focus, and disabled colors are built here so the
// action bar and the header share one implementation instead of repeating the logic.
// Each button remembers its role, so a theme toggle can restyle it in place.
internal static class UiButtonFactory {
    internal enum Role { Primary, Danger, Success, Neutral, Outline }

    internal sealed class Roles {
        internal Role Kind = Role.Neutral;
        internal bool Outline { get { return Kind == Role.Outline || Kind == Role.Neutral; } }
    }

    internal static Button Filled(Control themeHost, Roles roles, UiTheme theme, string text, Font font, ToolTip tip, string tooltip) {
        Button btn = NewBase(theme, text, font, tip, tooltip);
        btn.Tag = roles.Kind;
        Restyle(btn, theme);
        Attach(btn, theme);
        return btn;
    }

    internal static Button Outline(Control themeHost, Roles roles, UiTheme theme, string text, Font font, ToolTip tip, string tooltip) {
        Button btn = NewBase(theme, text, font, tip, tooltip);
        btn.Tag = Role.Outline;
        Restyle(btn, theme);
        Attach(btn, theme);
        return btn;
    }

    // Re-applies the fill, text, hover, border, and disabled colors for the button's role and
    // the given theme. Called on creation and again on every theme change, which is what keeps
    // buttons from lingering on the previous theme's colors until a restart.
    internal static void Restyle(Button btn, UiTheme theme) {
        if (btn == null || theme == null) return;
        Role role = btn.Tag is Role ? (Role)btn.Tag : Role.Outline;
        if (!btn.Enabled) {
            btn.FlatAppearance.BorderSize = 0;
            btn.BackColor = theme.DisabledFill;
            btn.ForeColor = theme.DisabledText;
            return;
        }
        Color back, fore, hover, border;
        switch (role) {
            case Role.Primary:
                back = theme.AccentFill; fore = Color.White; hover = theme.AccentFillHover; border = theme.AccentFill; break;
            case Role.Danger:
                back = theme.DangerFill; fore = Color.White; hover = theme.DangerFillHover; border = theme.DangerFill; break;
            case Role.Success:
                back = theme.SuccessFill; fore = Color.White; hover = theme.SuccessFillHover; border = theme.SuccessFill; break;
            default:
                back = theme.NeutralFill; fore = theme.NeutralText; hover = theme.NeutralFillHover; border = theme.Border; break;
        }
        bool outline = role == Role.Outline || role == Role.Neutral;
        btn.FlatAppearance.BorderSize = outline ? 1 : 0;
        btn.FlatAppearance.BorderColor = border;
        btn.BackColor = back;
        btn.ForeColor = fore;
        btn.FlatAppearance.MouseOverBackColor = hover;
        btn.FlatAppearance.MouseDownBackColor = hover;
    }

    internal static bool IsOutline(Button btn) {
        Role role = btn != null && btn.Tag is Role ? (Role)btn.Tag : Role.Outline;
        return role == Role.Outline || role == Role.Neutral;
    }

    private static Button NewBase(UiTheme theme, string text, Font font, ToolTip tip, string tooltip) {
        Button btn = new Button {
            Text = text,
            Font = font,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderSize = 1;
        UiLayout.EnforceNoClip(btn);
        if (tip != null && !string.IsNullOrEmpty(tooltip)) tip.SetToolTip(btn, tooltip);
        return btn;
    }

    private static void Attach(Button btn, UiTheme theme) {
        btn.GotFocus += delegate { btn.FlatAppearance.BorderSize = 2; btn.FlatAppearance.BorderColor = theme.FocusRing; };
        btn.LostFocus += delegate { btn.FlatAppearance.BorderSize = IsOutline(btn) ? 1 : 0; btn.FlatAppearance.BorderColor = theme.Border; };
        btn.EnabledChanged += delegate { Restyle(btn, theme); };
    }
}

// What color a surface or label should take from the active theme. Controls carry this in
// their Tag when created, so a theme toggle recolors the whole tree without per-control code.
internal enum SurfaceRole {
    Inherit,   // a container that matches its parent (generic panel/spacer)
    Canvas,    // the window background behind the results
    Surface,   // toolbar, action bar, list background, cards
    Header,    // the top header band and its inner hosts
    Border,    // 1px separator lines
    TextPrimary,
    TextMuted,
    HeaderText,
    HeaderMuted,
    AccentText,
    DangerText,
    SuccessText,
    StatusLabel // the status label uses the status colors set through SetStatus
}

// Central re-theming walk. Every themed control is recolored from its SurfaceRole, and every
// button from its role, so nothing is left on the previous theme until a restart.
internal static class UiTheming {
    internal static void Apply(Control root, UiTheme theme, Func<Color, Color> mapStatus) {
        if (root == null || theme == null) return;
        ApplyOne(root, theme, mapStatus);
        foreach (Control child in root.Controls) Apply(child, theme, mapStatus);
    }

    private static void ApplyOne(Control c, UiTheme theme, Func<Color, Color> mapStatus) {
        Button btn = c as Button;
        if (btn != null) { UiButtonFactory.Restyle(btn, theme); return; }

        ListView lv = c as ListView;
        if (lv != null) { lv.BackColor = theme.Surface; lv.ForeColor = theme.TextPrimary; return; }

        TextBox tb = c as TextBox;
        if (tb != null) { tb.BackColor = theme.Surface; tb.ForeColor = theme.TextPrimary; return; }

        if (!(c.Tag is SurfaceRole)) {
            // Untagged labels follow their parent's background so text never floats on a wrong fill.
            Label plain = c as Label;
            if (plain != null && plain.Parent != null) plain.BackColor = plain.Parent.BackColor;
            return;
        }

        SurfaceRole role = (SurfaceRole)c.Tag;
        Color back = BackgroundFor(role, theme);
        if (back != Color.Empty) c.BackColor = back;
        Color fore = ForegroundFor(role, theme);
        if (fore != Color.Empty) {
            // A text-role label draws on its container, so its background must follow the
            // container, not stay on whatever color it was created with.
            if (c.Parent != null) c.BackColor = c.Parent.BackColor;
            c.ForeColor = fore;
        }
    }

    private static Color BackgroundFor(SurfaceRole role, UiTheme theme) {
        switch (role) {
            case SurfaceRole.Canvas: return theme.Canvas;
            case SurfaceRole.Surface: return theme.Surface;
            case SurfaceRole.Header: return theme.Header;
            case SurfaceRole.Border: return theme.Border;
            case SurfaceRole.StatusLabel: return theme.Surface;
            default: return Color.Empty;
        }
    }

    private static Color ForegroundFor(SurfaceRole role, UiTheme theme) {
        switch (role) {
            case SurfaceRole.TextPrimary: return theme.TextPrimary;
            case SurfaceRole.TextMuted: return theme.TextMuted;
            case SurfaceRole.HeaderText: return theme.HeaderText;
            case SurfaceRole.HeaderMuted: return theme.HeaderMuted;
            case SurfaceRole.AccentText: return theme.AccentText;
            case SurfaceRole.DangerText: return theme.DangerText;
            case SurfaceRole.SuccessText: return theme.SuccessText;
            default: return Color.Empty;
        }
    }
}
