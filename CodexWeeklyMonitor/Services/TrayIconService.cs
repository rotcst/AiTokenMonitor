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
    private static readonly Color MenuBackground = Color.FromArgb(0x20, 0x24, 0x2A);
    private static readonly Color MenuBorder = Color.FromArgb(0x34, 0x3A, 0x43);
    private static readonly Color MenuText = Color.FromArgb(0xF0, 0xF2, 0xF5);
    private static readonly Color MenuMutedText = Color.FromArgb(0x9A, 0xA3, 0xAD);
    private static readonly Color MenuHover = Color.FromArgb(0x2F, 0x34, 0x3C);
    private const uint MenuBorderColorRef = 0x00433A34;

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
        _refreshItem = CreateMenuItem(() => RefreshRequested?.Invoke(this, EventArgs.Empty));
        _codexStatusItem = CreateStatusItem();
        _claudeStatusItem = CreateStatusItem();
        _languageItem = new Forms.ToolStripMenuItem();
        _exitItem = CreateMenuItem(() => ExitRequested?.Invoke(this, EventArgs.Empty));

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
            ShowCheckMargin = true,
            ShowImageMargin = false,
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
            e.Graphics.Clear(MenuBackground);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(MenuBorder);
            var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, bounds);
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(MenuBackground);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var isOpenSubmenu = e.Item is Forms.ToolStripMenuItem menuItem && menuItem.DropDown.Visible;
            var color = ResolveMenuItemBackgroundForTesting(e.Item.Selected, isOpenSubmenu);
            var bounds = new Rectangle(2, 1, Math.Max(1, e.Item.Width - 4), Math.Max(1, e.Item.Height - 2));

            var oldSmoothingMode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedRectangle(bounds, radius: 4);
            using var brush = new SolidBrush(color);
            e.Graphics.FillPath(brush, path);
            e.Graphics.SmoothingMode = oldSmoothingMode;
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var scale = Math.Max(1f, e.Graphics.DpiX / 96f);
            var centerY = e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height / 2f);
            var startX = e.Item.ContentRectangle.Left - (14f * scale);
            using var pen = new Pen(MenuText, 1.5f * scale)
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
            using var pen = new Pen(MenuBorder);
            e.Graphics.DrawLine(pen, 8, y, Math.Max(8, e.Item.Width - 8), y);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? MenuText : MenuMutedText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled != false ? MenuText : MenuMutedText;
            base.OnRenderArrow(e);
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
