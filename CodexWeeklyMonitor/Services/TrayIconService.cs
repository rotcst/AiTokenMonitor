using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;

namespace CodexWeeklyMonitor.Services;

internal interface ITrayIconService : IDisposable
{
    event EventHandler? ShowRequested;

    event EventHandler? RefreshRequested;

    event EventHandler? ExitRequested;

    bool Visible { get; set; }

    string ToolTipText { get; set; }

    void UpdateMenuStatus(string codexStatus, string claudeStatus);
}

/// <summary>
/// Owns the notification-area icon and its native menu. A WPF <c>ContextMenu</c> opened from a
/// WinForms <see cref="Forms.NotifyIcon"/> has no foreground WPF host and therefore cannot reliably
/// observe clicks in other applications. Keeping the menu attached to <see cref="Forms.NotifyIcon.ContextMenuStrip"/>
/// lets Windows and WinForms provide the expected outside-click dismissal through <c>AutoClose</c>.
/// </summary>
internal sealed class TrayIconService : ITrayIconService
{
    // Reference palette (Uiverse by Na3ar-17): gradient card, muted text, blue hover, purple language
    // group, red exit — mirroring the WPF window menu so both right-click surfaces match.
    private static readonly Color MenuBackground = Color.FromArgb(0x24, 0x28, 0x32);
    private static readonly Color MenuBackgroundBottom = Color.FromArgb(0x25, 0x1C, 0x28);
    private static readonly Color MenuBorder = Color.FromArgb(0x42, 0x43, 0x4A);
    private static readonly Color MenuText = Color.FromArgb(0x7E, 0x85, 0x90);
    private static readonly Color MenuMutedText = Color.FromArgb(0x7E, 0x85, 0x90);
    private static readonly Color MenuHoverText = Color.FromArgb(0xFF, 0xFF, 0xFF);
    private static readonly Color MenuHover = Color.FromArgb(0x53, 0x53, 0xFF);
    private static readonly Color MenuPurpleText = Color.FromArgb(0xBD, 0x89, 0xFF);
    private static readonly Color MenuPurpleHover = Color.FromArgb(0x38, 0x2D, 0x47);
    private static readonly Color MenuRedHover = Color.FromArgb(0x8E, 0x2A, 0x2A);
    private const uint MenuBorderColorRef = 0x004A4342;

    // Icon ids stored on each item's Tag so the renderer can draw a matching glyph.
    private const string IconShow = "show";
    private const string IconRefresh = "refresh";
    private const string IconGlobe = "globe";
    private const string IconExit = "exit";

    private readonly DrawingIcon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _refreshItem;
    private readonly Forms.ToolStripMenuItem _codexStatusItem;
    private readonly Forms.ToolStripMenuItem _claudeStatusItem;
    private readonly Forms.ToolStripMenuItem _languageItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly ModernMenuRenderer _menuRenderer;
    private readonly Font _menuFont;
    private readonly Font _boldMenuFont;
    private bool _disposed;

    public TrayIconService()
    {
        _menuFont = new Font("Segoe UI Variable Text", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _boldMenuFont = new Font(_menuFont, FontStyle.Bold);
        _menuRenderer = new ModernMenuRenderer();

        _showItem = CreateMenuItem(() => ShowRequested?.Invoke(this, EventArgs.Empty));
        _showItem.Font = _boldMenuFont;
        _showItem.Tag = IconShow;
        _refreshItem = CreateMenuItem(() => RefreshRequested?.Invoke(this, EventArgs.Empty));
        _refreshItem.Tag = IconRefresh;
        _codexStatusItem = CreateStatusItem();
        _claudeStatusItem = CreateStatusItem();
        _languageItem = new Forms.ToolStripMenuItem { Tag = IconGlobe };
        _exitItem = CreateMenuItem(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        _exitItem.Tag = IconExit;

        foreach (var language in Loc.All)
        {
            var captured = language;
            var item = CreateMenuItem(() => Loc.SetLanguage(captured));
            item.Tag = language;
            _languageItem.DropDownItems.Add(item);
        }

        _menu = new Forms.ContextMenuStrip
        {
            AutoClose = true,
            AutoSize = true,
            BackColor = MenuBackground,
            ForeColor = MenuText,
            Font = _menuFont,
            MinimumSize = new Size(148, 0),
            Padding = new Forms.Padding(4),
            Renderer = _menuRenderer,
            ShowCheckMargin = false,
            ShowImageMargin = true,
        };
        _menu.Items.AddRange(
        [
            _showItem,
            _refreshItem,
            CreateSeparator(),
            _codexStatusItem,
            _claudeStatusItem,
            CreateSeparator(),
            _languageItem,
            CreateSeparator(),
            _exitItem,
        ]);
        ConfigureDropDown(_languageItem.DropDown);
        _languageItem.DropDown.Opening += DropDown_Opening;
        _menu.Opening += Menu_Opening;

        _icon = LoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = "AI TOKEN 用量监控",
            Visible = true,
        };
        _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;

        Loc.Changed += Loc_Changed;
        UpdateLocalizedMenu();
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? ExitRequested;

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public string ToolTipText
    {
        get => _notifyIcon.Text;
        set => _notifyIcon.Text = TrimNotifyText(value);
    }

    internal Forms.ContextMenuStrip MenuForTesting => _menu;

    internal bool MenuAttachedForTesting => ReferenceEquals(_notifyIcon.ContextMenuStrip, _menu);

    internal static Color MenuHoverColorForTesting => MenuHover;

    internal static Color ResolveMenuItemBackgroundForTesting(bool isSelected, bool isSubmenuOpen) =>
        isSelected || isSubmenuOpen ? MenuHover : MenuBackground;

    internal void RaiseExitRequested() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public void UpdateMenuStatus(string codexStatus, string claudeStatus)
    {
        _codexStatusItem.Text = codexStatus;
        _claudeStatusItem.Text = claudeStatus;
    }

    private Forms.ToolStripMenuItem CreateMenuItem(Action action)
    {
        var item = new Forms.ToolStripMenuItem
        {
            AutoSize = true,
            BackColor = MenuBackground,
            ForeColor = MenuText,
            Padding = new Forms.Padding(4, 1, 4, 1),
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static Forms.ToolStripMenuItem CreateStatusItem() => new()
    {
        AutoSize = true,
        BackColor = MenuBackground,
        Enabled = false,
        ForeColor = MenuMutedText,
        Padding = new Forms.Padding(4, 1, 4, 1),
    };

    private static Forms.ToolStripSeparator CreateSeparator() => new()
    {
        Margin = new Forms.Padding(6, 3, 6, 3),
    };

    private void ConfigureDropDown(Forms.ToolStripDropDown dropDown)
    {
        dropDown.AutoClose = true;
        dropDown.BackColor = MenuBackground;
        dropDown.ForeColor = MenuText;
        dropDown.Padding = new Forms.Padding(4);
        dropDown.Renderer = _menuRenderer;
        if (dropDown is Forms.ToolStripDropDownMenu menu)
        {
            menu.ShowCheckMargin = true;
            menu.ShowImageMargin = false;
        }
    }

    private void Menu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        UpdateLocalizedMenu();
        DwmWindowChrome.ApplyRoundedCorners(_menu.Handle, MenuBorderColorRef);
    }

    private static void DropDown_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is Forms.ToolStripDropDown dropDown)
        {
            DwmWindowChrome.ApplyRoundedCorners(dropDown.Handle, MenuBorderColorRef);
        }
    }

    private void Loc_Changed(object? sender, EventArgs e)
    {
        UpdateLocalizedMenu();
    }

    private void UpdateLocalizedMenu()
    {
        _showItem.Text = Loc.T("menu.showWindow");
        _refreshItem.Text = Loc.T("menu.refresh");
        _languageItem.Text = Loc.T("menu.language");
        _exitItem.Text = Loc.T("menu.exit");

        foreach (var item in _languageItem.DropDownItems.OfType<Forms.ToolStripMenuItem>())
        {
            if (item.Tag is not AppLanguage language)
            {
                continue;
            }

            item.Text = Loc.DisplayName(language);
            item.Checked = language == Loc.Current;
        }
    }

    private void NotifyIcon_MouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string TrimNotifyText(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "AI TOKEN 用量监控" : value.Trim();
        return text.Length <= 63 ? text : text[..63];
    }

    private static DrawingIcon LoadApplicationIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var icon = DrawingIcon.ExtractAssociatedIcon(processPath);
            if (icon is not null)
            {
                return icon;
            }
        }

        return (DrawingIcon)DrawingSystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loc.Changed -= Loc_Changed;
        _notifyIcon.Visible = false;
        _notifyIcon.MouseDoubleClick -= NotifyIcon_MouseDoubleClick;
        _notifyIcon.ContextMenuStrip = null;
        _notifyIcon.Dispose();
        _menu.Opening -= Menu_Opening;
        _languageItem.DropDown.Opening -= DropDown_Opening;
        _menu.Dispose();
        _boldMenuFont.Dispose();
        _menuFont.Dispose();
        _icon.Dispose();
    }

    private sealed class ModernMenuColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBackground;

        public override Color MenuBorder => TrayIconService.MenuBorder;

        public override Color MenuItemBorder => MenuHover;

        public override Color MenuItemSelected => MenuHover;

        public override Color MenuItemSelectedGradientBegin => MenuHover;

        public override Color MenuItemSelectedGradientEnd => MenuHover;

        public override Color ImageMarginGradientBegin => MenuBackground;

        public override Color ImageMarginGradientMiddle => MenuBackground;

        public override Color ImageMarginGradientEnd => MenuBackground;

        public override Color SeparatorDark => TrayIconService.MenuBorder;

        public override Color SeparatorLight => TrayIconService.MenuBorder;
    }

    private sealed class ModernMenuRenderer() : Forms.ToolStripProfessionalRenderer(new ModernMenuColorTable())
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            var bounds = e.ToolStrip.ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                e.Graphics.Clear(MenuBackground);
                return;
            }

            // The reference card's 139° top-left → bottom-right gradient.
            using var brush = new LinearGradientBrush(bounds, MenuBackground, MenuBackgroundBottom, 135f);
            e.Graphics.FillRectangle(brush, bounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(MenuBorder);
            var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, bounds);
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            // Intentionally empty: let the gradient background show through the image column.
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            var isOpenSubmenu = item is Forms.ToolStripMenuItem menuItem && menuItem.DropDown.Visible;
            if (!item.Enabled || (!item.Selected && !isOpenSubmenu))
            {
                return;
            }

            // A static 1px lift echoes the window menu's hover translate (WinForms can't ease it).
            var lift = item.Selected ? 1 : 0;
            var bounds = new Rectangle(
                3,
                Math.Max(0, 1 - lift),
                Math.Max(1, item.Width - 6),
                Math.Max(1, item.Height - 2));

            var oldSmoothingMode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedRectangle(bounds, radius: 5);
            using var brush = new SolidBrush(HoverColorFor(item));
            e.Graphics.FillPath(brush, path);
            e.Graphics.SmoothingMode = oldSmoothingMode;
        }

        protected override void OnRenderItemImage(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var scale = Math.Max(1f, e.Graphics.DpiX / 96f);
            var color = !e.Item.Enabled
                ? MenuMutedText
                : e.Item.Selected ? MenuHoverText : RestingTextColorFor(e.Item);
            var box = e.ImageRectangle;
            if (e.Item.Selected)
            {
                box = new Rectangle(box.X + 1, box.Y - 1, box.Width, box.Height);
            }

            DrawGlyph(e.Graphics, e.Item.Tag, box, color, scale);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var scale = Math.Max(1f, e.Graphics.DpiX / 96f);
            var centerY = e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height / 2f);
            var startX = e.Item.ContentRectangle.Left - (14f * scale);
            var color = e.Item.Selected ? MenuHoverText : RestingTextColorFor(e.Item);
            using var pen = new Pen(color, 1.5f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            e.Graphics.DrawLines(
                pen,
                [
                    new PointF(startX, centerY),
                    new PointF(startX + (3f * scale), centerY + (3f * scale)),
                    new PointF(startX + (8f * scale), centerY - (4f * scale)),
                ]);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(MenuBorder) { Width = 1.5f };
            e.Graphics.DrawLine(pen, 10, y, Math.Max(10, e.Item.Width - 10), y);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            var isOpenSubmenu = e.Item is Forms.ToolStripMenuItem mi && mi.DropDown.Visible;
            e.TextColor = !e.Item.Enabled
                ? MenuMutedText
                : (e.Item.Selected || isOpenSubmenu) ? MenuHoverText : RestingTextColorFor(e.Item);
            if (e.Item.Selected)
            {
                e.TextRectangle = new Rectangle(
                    e.TextRectangle.X + 1,
                    e.TextRectangle.Y - 1,
                    e.TextRectangle.Width,
                    e.TextRectangle.Height);
            }

            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled != false
                ? (e.Item?.Selected == true ? MenuHoverText : RestingTextColorFor(e.Item!))
                : MenuMutedText;
            base.OnRenderArrow(e);
        }

        // Blue for actions, purple for the language group, red for exit — matching the window menu.
        private static Color HoverColorFor(Forms.ToolStripItem item) => item.Tag switch
        {
            IconExit => MenuRedHover,
            IconGlobe => MenuPurpleHover,
            AppLanguage => MenuPurpleHover,
            _ => MenuHover,
        };

        private static Color RestingTextColorFor(Forms.ToolStripItem item) =>
            item.Tag is AppLanguage || (item.Tag as string) == IconGlobe ? MenuPurpleText : MenuText;

        private static void DrawGlyph(Graphics g, object? tag, Rectangle box, Color color, float scale)
        {
            if (tag is not string id)
            {
                return;
            }

            var oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 1.5f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            var s = Math.Min(box.Width, box.Height) - (3f * scale);
            if (s < 8f)
            {
                s = Math.Min(box.Width, box.Height);
            }

            var cx = box.Left + (box.Width / 2f);
            var cy = box.Top + (box.Height / 2f);
            var r = new RectangleF(cx - (s / 2f), cy - (s / 2f), s, s);

            switch (id)
            {
                case IconShow:
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                    g.DrawLine(pen, r.X, r.Y + (r.Height * 0.28f), r.Right, r.Y + (r.Height * 0.28f));
                    break;
                case IconRefresh:
                    var ar = RectangleF.Inflate(r, -s * 0.06f, -s * 0.06f);
                    g.DrawArc(pen, ar.X, ar.Y, ar.Width, ar.Height, 40, 260);
                    var rad = 40f * (float)Math.PI / 180f;
                    var ex = cx + (ar.Width / 2f * (float)Math.Cos(rad));
                    var ey = cy + (ar.Height / 2f * (float)Math.Sin(rad));
                    g.DrawLines(pen,
                    [
                        new PointF(ex - (4f * scale), ey),
                        new PointF(ex, ey),
                        new PointF(ex, ey - (4f * scale)),
                    ]);
                    break;
                case IconGlobe:
                    g.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height);
                    g.DrawLine(pen, r.X, cy, r.Right, cy);
                    g.DrawEllipse(pen, cx - (r.Width * 0.28f), r.Y, r.Width * 0.56f, r.Height);
                    break;
                case IconExit:
                    var pr = RectangleF.Inflate(r, -s * 0.04f, -s * 0.04f);
                    g.DrawArc(pen, pr.X, pr.Y, pr.Width, pr.Height, -60, 300);
                    g.DrawLine(pen, cx, r.Y - (s * 0.02f), cx, cy);
                    break;
            }

            g.SmoothingMode = oldMode;
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
