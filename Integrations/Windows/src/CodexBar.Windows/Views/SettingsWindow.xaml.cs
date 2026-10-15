using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodexBar.Core;
using CodexBar.Windows.Services;
using Microsoft.Win32;

namespace CodexBar.Windows.Views;

public partial class SettingsWindow : Window
{
    private static readonly int[] RefreshMinuteOptions = { 1, 2, 5, 10, 30 };

    private readonly bool _demoMode;
    private readonly Func<AppSettings> _load;
    private bool _closed;
    private bool _saving;

    public SettingsWindow(bool demoMode, Func<AppSettings> load, Func<AppSettings, Task<string?>> save)
    {
        _demoMode = demoMode;
        _load = load;
        SaveHandler = save;

        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;

        foreach (var minutes in RefreshMinuteOptions)
        {
            RefreshIntervalBox.Items.Add($"{minutes} 分钟");
        }

        Populate();
        DemoHint.Visibility = demoMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Applies the edited settings. Returns null on success or a message to
    /// show, and may complete asynchronously; the caller only ever sees the
    /// result back on the dispatcher.
    /// </summary>
    public Func<AppSettings, Task<string?>> SaveHandler { get; }

    public AppSettings? Result { get; private set; }

    /// <summary>
    /// Root visual used by the headless screenshot renderer. This is the
    /// bordered surface, so the PNG gets the window background.
    /// </summary>
    public FrameworkElement RenderRoot => WindowSurface;

    private void Populate()
    {
        var settings = _load();

        CodexEnabledBox.IsChecked = settings.CodexEnabled;
        ClaudeEnabledBox.IsChecked = settings.ClaudeEnabled;
        NotifyLowQuotaBox.IsChecked = settings.NotifyAtLowQuota;
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;

        CodexPathBox.Text = settings.CodexAuthPath ?? string.Empty;
        ClaudePathBox.Text = settings.ClaudeCredentialsPath ?? string.Empty;

        var minutes = Math.Max(1, (int)Math.Round(settings.RefreshSeconds / 60.0));
        var selected = Array.IndexOf(RefreshMinuteOptions, minutes);
        RefreshIntervalBox.SelectedIndex = selected >= 0 ? selected : Array.IndexOf(RefreshMinuteOptions, 2);

        if (_demoMode)
        {
            CodexPathHint.Text = "演示模式不会读取本机凭据路径。";
            ClaudePathHint.Text = "演示模式不会读取本机凭据路径。";
        }
        else
        {
            // Probing the default credential paths is file-system I/O with no
            // timeout (a network share or a sleeping drive can stall it), so
            // the first paint must not wait for it. Show a placeholder and fill
            // the real answer in when the probe returns.
            CodexPathHint.Text = "正在检查本机凭据路径…";
            ClaudePathHint.Text = "正在检查本机凭据路径…";
            _ = PopulateCredentialHintsAsync();
        }

        StatusText.Text = _demoMode ? "演示模式：更改不会写入磁盘。" : string.Empty;
    }

    private async Task PopulateCredentialHintsAsync()
    {
        var hints = await Task.Run(
            () => (Codex: CredentialDiscovery.CodexAuthPath, Claude: CredentialDiscovery.ClaudeCredentialsPath))
            .ConfigureAwait(false);

        // Marshal explicitly instead of relying on the captured context: the
        // window may already be closed, and a control must never be touched
        // from a thread-pool thread.
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closed)
            {
                return;
            }

            CodexPathHint.Text = hints.Codex is { } codexDefault
                ? $"自动发现：{codexDefault}"
                : "未自动发现，请手动指定或在 CLI 中登录。";
            ClaudePathHint.Text = hints.Claude is { } claudeDefault
                ? $"自动发现：{claudeDefault}"
                : "未自动发现，请手动指定或在 CLI 中登录。";
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        // A pending probe callback checks this before touching the controls.
        _closed = true;
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_saving)
        {
            // A write is still in flight. Letting the dialog close now would
            // let that write finish after the user has moved on - reopening
            // settings or switching the panel's display mode - and overwrite
            // the newer state. Keep the modal open and say why.
            e.Cancel = true;
            StatusText.Text = "正在保存，请稍候…";
            return;
        }

        base.OnClosing(e);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1 && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnBrowseCodexClick(object sender, RoutedEventArgs e)
        => BrowseFor(CodexPathBox, "Codex 凭据 (auth.json)|auth.json|所有文件 (*.*)|*.*");

    private void OnBrowseClaudeClick(object sender, RoutedEventArgs e)
        => BrowseFor(ClaudePathBox, "Claude 凭据 (.credentials.json)|.credentials.json|所有文件 (*.*)|*.*");

    private void BrowseFor(TextBox target, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择凭据文件",
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };

        var current = target.Text?.Trim();
        if (!string.IsNullOrEmpty(current))
        {
            try
            {
                var directory = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    dialog.InitialDirectory = directory;
                }
            }
            catch (Exception)
            {
                // Ignore an unusable typed path and fall back to the default start folder.
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving)
        {
            // A save is already in flight; ignore the extra click instead of
            // queueing a second write.
            return;
        }

        _saving = true;
        StatusText.Text = _demoMode ? "正在应用…" : "正在保存…";
        try
        {
            var settings = BuildSettings();
            var error = await SaveHandler(settings);

            // OnClosing keeps the dialog open while _saving, so this is
            // belt-and-braces in case that ever changes.
            if (_closed)
            {
                return;
            }

            if (error is not null)
            {
                StatusText.Text = error;
                return;
            }

            Result = settings;
            StatusText.Text = _demoMode ? "已应用到本次演示会话。" : "已保存。";

            // Clear the flag first: this close is the intended one, so the
            // OnClosing guard must not cancel it.
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppLog.Error("settings-apply-failed", ex);
            if (!_closed)
            {
                StatusText.Text = "保存失败，请检查填写内容。";
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private AppSettings BuildSettings()
    {
        var current = _load();
        var minutes = RefreshMinuteOptions[Math.Max(0, RefreshIntervalBox.SelectedIndex)];

        return current with
        {
            RefreshSeconds = minutes * 60,
            CodexEnabled = CodexEnabledBox.IsChecked == true,
            ClaudeEnabled = ClaudeEnabledBox.IsChecked == true,
            NotifyAtLowQuota = NotifyLowQuotaBox.IsChecked == true,
            StartWithWindows = StartWithWindowsBox.IsChecked == true,
            CodexAuthPath = NormalizePath(CodexPathBox.Text),
            ClaudeCredentialsPath = NormalizePath(ClaudePathBox.Text),
        };
    }

    private static string? NormalizePath(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    internal static string FormatMinutes(int minutes)
        => minutes.ToString(CultureInfo.InvariantCulture);
}
