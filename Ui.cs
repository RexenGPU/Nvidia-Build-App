using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NvidiaBuildApp;

static class Theme
{
    public static readonly Color Bg         = Color.FromArgb(0x18, 0x18, 0x18);
    public static readonly Color SidebarBg  = Color.FromArgb(0x12, 0x12, 0x12);
    public static readonly Color CardBg     = Color.FromArgb(0x23, 0x23, 0x23);
    public static readonly Color Hover      = Color.FromArgb(0x2A, 0x2A, 0x2A);
    public static readonly Color InputBg    = Color.FromArgb(0x26, 0x26, 0x26);
    public static readonly Color CodeBorder = Color.FromArgb(0x3A, 0x3A, 0x3A);
    public static readonly Color Text       = Color.FromArgb(0xEC, 0xEC, 0xEC);
    public static readonly Color TextDim    = Color.FromArgb(0x9B, 0x9B, 0x9B);
    public static readonly Color Accent     = Color.FromArgb(0x76, 0xB9, 0x00);   // vert NVIDIA
    public static readonly Color AccentText = Color.FromArgb(0x14, 0x1F, 0x02);
    public static readonly Color Error      = Color.FromArgb(0xFF, 0x7B, 0x72);
    public static readonly Color ErrorBg    = Color.FromArgb(0x3A, 0x22, 0x22);

    public static readonly Font Small      = new("Segoe UI", 9f);
    public static readonly Font SmallBold  = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font UI         = new("Segoe UI", 10.5f);
    public static readonly Font UIBold     = new("Segoe UI", 10.5f, FontStyle.Bold);
    public static readonly Font Title      = new("Segoe UI Semibold", 14f);

    public static System.Drawing.Drawing2D.GraphicsPath RoundPath(System.Drawing.Rectangle r, int rad)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        int d = Math.Max(2, rad * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void DarkTitleBar(Form f)
    {
        try
        {
            int v = 1;
            Win32.DwmSetWindowAttribute(f.Handle, 20, ref v, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch { }
    }
}

static class Win32
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
}

class RoundButton : Control, IButtonControl
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color Back { get; set; } = Theme.Accent;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color BackHover { get; set; } = Color.Empty;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color BackDown { get; set; } = Color.Empty;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color Border { get; set; } = Color.Empty;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color TextCol { get; set; } = Theme.AccentText;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Radius { get; set; } = 10;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public DialogResult DialogResult { get; set; }

    bool _hover, _down;

    public RoundButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = Theme.UIBold;
        Height = 38;
        BackColor = Theme.Bg;
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() => OnClick(EventArgs.Empty);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundPath(rect, Radius);

        Color back;
        if (!Enabled) back = Color.FromArgb(0x1E, 0x1E, 0x1E);
        else if (_down) back = BackDown.IsEmpty ? ControlPaint.Dark(Back, 0.05f) : BackDown;
        else if (_hover) back = BackHover.IsEmpty ? ControlPaint.Light(Back, 0.12f) : BackHover;
        else back = Back;

        using (var b = new SolidBrush(back)) g.FillPath(b, path);
        if (!Border.IsEmpty)
        {
            using var pen = new Pen(Border, 1.4f);
            g.DrawPath(pen, path);
        }

        var tc = Enabled ? TextCol : Theme.TextDim;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), tc,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}

/// <summary>Petite boite de dialogue sombre avec champ texte (renommage...).</summary>
class PromptForm : Form
{
    readonly TextBox _box;

    PromptForm(string title, string label, string value)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 170);
        BackColor = Theme.Bg;
        Font = Theme.UI;
        AutoScaleMode = AutoScaleMode.Dpi;

        var l = new Label { Text = label, AutoSize = true, ForeColor = Theme.Text, Location = new Point(16, 18) };
        _box = new TextBox
        {
            Location = new Point(16, 48),
            Size = new Size(428, 30),
            Text = value,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.UI,
        };
        var ok = new RoundButton { Text = Loc.S("ok"), Back = Theme.Accent, TextCol = Theme.AccentText, Location = new Point(250, 102), Size = new Size(92, 36) };
        ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = new RoundButton { Text = Loc.S("cancel"), Back = Theme.CardBg, BackHover = Theme.Hover, TextCol = Theme.Text, Border = Theme.CodeBorder, Location = new Point(352, 102), Size = new Size(92, 36) };
        cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { l, _box, ok, cancel });
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    public static string? Ask(IWin32Window owner, string title, string label, string value)
    {
        using var f = new PromptForm(title, label, value);
        return f.ShowDialog(owner) == DialogResult.OK ? f._box.Text.Trim() : null;
    }
}

/// <summary>Confirmation d'un appel d'outil MCP (l'IA peut executer des commandes locales).</summary>
class ToolConfirmForm : Form
{
    public enum Answer { Deny, Once, Always }

    public Answer Result { get; private set; } = Answer.Deny;

    ToolConfirmForm(string tool, string args)
    {
        Text = Loc.S("toolAskTitle");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(600, 460);
        BackColor = Theme.Bg;
        Font = Theme.UI;
        AutoScaleMode = AutoScaleMode.Dpi;

        var label = new Label
        {
            Text = string.Format(Loc.S("toolAskBody"), tool),
            ForeColor = Theme.Text,
            Location = new Point(18, 16),
            Size = new Size(564, 46),
            Font = Theme.UIBold,
        };

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Location = new Point(18, 70),
            Size = new Size(564, 296),
            BackColor = Theme.InputBg,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
            Text = args,
        };

        var hint = new Label
        {
            Text = Loc.S("toolAskHint"),
            ForeColor = Theme.TextDim,
            Location = new Point(18, 374),
            Size = new Size(564, 24),
            Font = Theme.Small,
        };

        var deny = new RoundButton
        {
            Text = Loc.S("toolDeny"), Back = Theme.CardBg, BackHover = Theme.ErrorBg,
            TextCol = Theme.Error, Border = Theme.CodeBorder,
            Location = new Point(18, 406), Size = new Size(150, 38)
        };
        deny.Click += (s, e) => { Result = Answer.Deny; Close(); };

        var always = new RoundButton
        {
            Text = Loc.S("toolAlways"), Back = Theme.CardBg, BackHover = Theme.Hover,
            TextCol = Theme.Text, Border = Theme.CodeBorder,
            Location = new Point(178, 406), Size = new Size(200, 38)
        };
        always.Click += (s, e) => { Result = Answer.Always; Close(); };

        var once = new RoundButton
        {
            Text = Loc.S("toolAllow"), Back = Theme.Accent, TextCol = Theme.AccentText,
            Location = new Point(388, 406), Size = new Size(194, 38)
        };
        once.Click += (s, e) => { Result = Answer.Once; Close(); };

        AcceptButton = once;
        CancelButton = deny;
        Controls.AddRange(new Control[] { label, box, hint, deny, always, once });
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitleBar(this); }

    public static Answer Ask(IWin32Window owner, string tool, string args)
    {
        using var f = new ToolConfirmForm(tool, args);
        f.ShowDialog(owner);
        return f.Result;
    }
}
