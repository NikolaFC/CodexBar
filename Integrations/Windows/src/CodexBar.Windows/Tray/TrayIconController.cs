using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using CodexBar.Windows.Services;
using CodexBar.Windows.ViewModels;
using Forms = System.Windows.Forms;

namespace CodexBar.Windows.Tray;

/// <summary>
/// Owns the WinForms <see cref="Forms.NotifyIcon"/>. Left click toggles the
/// panel, right click opens the menu, and the tooltip carries both providers'
/// remaining percentages. Every custom-drawn resource is disposed here.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly AppViewModel _viewModel;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly Action _toggleWindow;
    private readonly Action _openPanel;
    private readonly Action _refresh;
    private readonly Action _openSettings;
    private readonly Action _exit;

    public TrayIconController(
        AppViewModel viewModel,
        Action toggleWindow,
        Action openPanel,
        Action refresh,
        Action openSettings,
        Action exit)
    {
        _viewModel = viewModel;
        _toggleWindow = toggleWindow;
        _openPanel = openPanel;
        _refresh = refresh;
        _openSettings = openSettings;
        _exit = exit;

        _icon = CreateStatusIcon();
        _menu = BuildMenu();

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = TruncateForTooltip(_viewModel.BuildTooltip()),
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.MouseUp += OnMouseUp;
    }

    public void UpdateTooltip()
        => _notifyIcon.Text = TruncateForTooltip(_viewModel.BuildTooltip());

    public void ShowLowQuotaAlert(string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = "CodexBar 额度提醒";
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Warning;
            _notifyIcon.ShowBalloonTip(6000);
        }
        catch (Exception ex)
        {
            AppLog.Error("tray-balloon-failed", ex);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.MouseUp -= OnMouseUp;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            _toggleWindow();
        }
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
        };

        var openItem = new Forms.ToolStripMenuItem("打开面板");
        openItem.Click += (_, _) => _openPanel();

        var refreshItem = new Forms.ToolStripMenuItem("立即刷新");
        refreshItem.Click += (_, _) => _refresh();

        var settingsItem = new Forms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => _openSettings();

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => _exit();

        menu.Items.Add(openItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    /// <summary>Draws the two-bar status icon: green Codex, orange Claude.</summary>
    private static Icon CreateStatusIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var codexBrush = new SolidBrush(Color.FromArgb(255, 0x35, 0xC0, 0x7F));
            using var claudeBrush = new SolidBrush(Color.FromArgb(255, 0xE0, 0x8A, 0x45));
            using var trackBrush = new SolidBrush(Color.FromArgb(90, 0xFF, 0xFF, 0xFF));

            var track = new Rectangle(4, 5, 24, 22);
            FillRounded(graphics, trackBrush, track, 5);
            FillRounded(graphics, codexBrush, new Rectangle(7, 8, 8, 16), 3);
            FillRounded(graphics, claudeBrush, new Rectangle(17, 12, 8, 12), 3);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void FillRounded(Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    private static string TruncateForTooltip(string text)
        => text.Length <= 62 ? text : text[..62];

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
