using System.Windows;
using System.Windows.Threading;
using CodexBar.Core;
using CodexBar.Windows.Services;
using CodexBar.Windows.Tray;
using CodexBar.Windows.ViewModels;
using CodexBar.Windows.Views;

namespace CodexBar.Windows;

public partial class App : Application
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private AppOptions _options = new();
    private SettingsService? _settings;
    private AppViewModel? _viewModel;
    private UsageService? _usageService;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIconController? _tray;
    private SingleInstanceGuard? _instanceGuard;
    private LowQuotaNotifier? _notifier;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _tickTimer;
    private CancellationTokenSource? _shutdown;
    private CancellationTokenSource? _refresh;
    private int _refreshGeneration;
    private AppSettings _currentSettings = new();
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _options = AppOptions.Parse(e.Args);
        if (_options.IsDemo || _options.IsHeadlessRender)
        {
            // Demo and smoke runs must not create log files either.
            AppLog.DisableForDemo();
        }

        if (_options.IsHeadlessRender)
        {
            Shutdown(ScreenshotRenderer.Run(_options));
            return;
        }

        _shutdown = new CancellationTokenSource();
        _instanceGuard = SingleInstanceGuard.Acquire(OnActivationRequested, _options.IsDemo);
        if (!_instanceGuard.IsPrimary)
        {
            _instanceGuard.Dispose();
            _instanceGuard = null;
            Shutdown(0);
            return;
        }

        _settings = new SettingsService(_options.IsDemo);
        _currentSettings = _settings.Load();
        _viewModel = new AppViewModel(_options.IsDemo, _currentSettings.ShowRemaining ? DisplayMode.Remaining : DisplayMode.Used);
        _viewModel.RefreshRequested += RequestRefresh;
        _viewModel.OpenSettingsRequested += ShowSettings;
        _viewModel.HideRequested += HidePanel;
        _viewModel.ExitRequested += ExitApplication;
        _viewModel.OpenUsagePageRequested += provider => UsageLinks.OpenUrl(UsageLinks.UsagePage(provider));
        _viewModel.LoginHelpRequested += ShowLoginHelp;
        _viewModel.ModeChanged += OnDisplayModeChanged;

        _notifier = new LowQuotaNotifier();
        _usageService = new UsageService(CreateProviders(), probeCredentialFiles: !_options.IsDemo);
        _usageService.SnapshotPublished += OnSnapshotPublished;

        _mainWindow = new MainWindow(_viewModel);
        _mainWindow.Closing += OnMainWindowClosing;

        _tray = new TrayIconController(
            _viewModel,
            TogglePanel,
            ShowPanel,
            RequestRefresh,
            ShowSettings,
            ExitApplication);

        StartTimers();

        if (!_options.Background)
        {
            ShowPanel();
        }

        RequestRefresh();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _refreshTimer?.Stop();
        _tickTimer?.Stop();
        // Cancel only. Cancelling lets an in-flight cycle unwind on its own and
        // release its own token source; disposing either source here could throw
        // ObjectDisposedException into that cycle.
        _shutdown?.Cancel();
        _refresh?.Cancel();
        _tray?.Dispose();
        _usageService?.Dispose();
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    private IReadOnlyList<IUsageProvider> CreateProviders()
    {
        if (_options.IsDemo)
        {
            return new IUsageProvider[] { new DemoCodexProvider(), new DemoClaudeProvider() };
        }

        return new IUsageProvider[] { new CodexUsageProvider(), new ClaudeUsageProvider() };
    }

    private void StartTimers()
    {
        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval() };
        _refreshTimer.Tick += (_, _) => RequestRefresh();
        _refreshTimer.Start();

        _tickTimer = new DispatcherTimer { Interval = TickInterval };
        _tickTimer.Tick += (_, _) =>
        {
            _viewModel?.Tick();
            _tray?.UpdateTooltip();
        };
        _tickTimer.Start();
    }

    private TimeSpan RefreshInterval()
    {
        var seconds = SettingsStore.ClampRefreshSeconds(_currentSettings.RefreshSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Starts a refresh cycle, cancelling any cycle still running. The stale
    /// cycle's snapshots are dropped by generation, so a result produced with
    /// the previous settings can never overwrite newer state.
    /// </summary>
    private void RequestRefresh()
    {
        if (_usageService is null || _viewModel is null || _shutdown is null || _shutdown.IsCancellationRequested)
        {
            return;
        }

        _refresh?.Cancel();
        // Cancel the previous cycle but leave disposal to that cycle's own
        // finally: an in-flight HttpClient may still be registering on the
        // token, and disposing it underneath would throw ObjectDisposedException.
        var source = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _refresh = source;
        var generation = ++_refreshGeneration;

        _ = RefreshAsync(generation, source);
    }

    private async Task RefreshAsync(int generation, CancellationTokenSource source)
    {
        try
        {
            if (_usageService is null || _viewModel is null)
            {
                return;
            }

            _viewModel.SetRefreshing(true);
            try
            {
                await _usageService.RefreshAsync(_currentSettings, generation, source.Token);
            }
            catch (Exception ex)
            {
                AppLog.Error("refresh-dispatch-failed", ex);
            }
            finally
            {
                // This runs on the UI thread: the await above captured the
                // dispatcher's synchronization context and never used
                // ConfigureAwait(false) in this method.
                if (generation == _refreshGeneration)
                {
                    _viewModel.SetRefreshing(false);
                    _viewModel.Tick();
                    _tray?.UpdateTooltip();

                    if (ReferenceEquals(source, _refresh))
                    {
                        _refresh = null;
                    }
                }
                // Otherwise a newer cycle already owns the UI state.
            }
        }
        finally
        {
            // Only this cycle owns its source, and by now every task that could
            // have registered on the token has finished. Releasing the previous
            // cycle inside RequestRefresh would race an in-flight request.
            source.Dispose();
        }
    }

    private void OnSnapshotPublished(UsageSnapshot snapshot, int generation)
    {
        if (_viewModel is null || _tray is null)
        {
            return;
        }

        // The usage service completes on thread-pool threads, so every view
        // model mutation has to be marshalled back to the UI thread.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSnapshotPublished(snapshot, generation));
            return;
        }

        if (generation != _refreshGeneration)
        {
            return;
        }

        _viewModel.Apply(snapshot);
        _tray.UpdateTooltip();

        if (_currentSettings.NotifyAtLowQuota && _notifier is not null)
        {
            foreach (var alert in _notifier.Evaluate(snapshot))
            {
                _tray.ShowLowQuotaAlert(alert);
            }
        }
    }

    private void OnActivationRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ShowPanel();
            RequestRefresh();
        });
    }

    private void TogglePanel()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (_mainWindow.IsVisible)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    private void ShowPanel()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void HidePanel()
    {
        if (_mainWindow is { IsVisible: true })
        {
            _mainWindow.Hide();
        }
    }

    private void ShowSettings()
    {
        if (_settings is null || _mainWindow is null)
        {
            return;
        }

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_options.IsDemo, () => _currentSettings, SaveSettings)
        {
            Owner = _mainWindow.IsVisible ? _mainWindow : null,
        };

        _settingsWindow = window;
        window.ShowDialog();
        _settingsWindow = null;
    }

    private string? SaveSettings(AppSettings settings)
    {
        if (_settings is null)
        {
            return "设置服务不可用。";
        }

        if (!_settings.IsEphemeral)
        {
            var startupError = StartupRegistration.Apply(settings.StartWithWindows);
            if (startupError is not null)
            {
                return startupError;
            }
        }

        var error = _settings.Save(settings);
        if (error is not null)
        {
            if (!_settings.IsEphemeral)
            {
                // Keep the registry in step with the settings that are still on disk.
                StartupRegistration.Apply(_currentSettings.StartWithWindows);
            }

            return error;
        }

        _currentSettings = settings;

        _viewModel?.SetMode(settings.ShowRemaining ? DisplayMode.Remaining : DisplayMode.Used);

        if (_refreshTimer is not null)
        {
            _refreshTimer.Interval = RefreshInterval();
        }

        RequestRefresh();
        return null;
    }

    private void ShowLoginHelp(ProviderId provider)
    {
        MessageBox.Show(
            _mainWindow!,
            UsageLinks.LoginInstructions(provider),
            provider == ProviderId.Codex ? "Codex 登录说明" : "Claude 登录说明",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// Persists the remaining/used choice so reopening settings (or the app)
    /// does not silently flip the panel back to the other mode.
    /// </summary>
    private void OnDisplayModeChanged(DisplayMode mode)
    {
        if (_settings is null)
        {
            return;
        }

        var showRemaining = mode == DisplayMode.Remaining;
        if (_currentSettings.ShowRemaining == showRemaining)
        {
            return;
        }

        _currentSettings = _currentSettings with { ShowRemaining = showRemaining };
        _settings.Save(_currentSettings);
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        // Closing the panel hides it; the tray icon keeps the app alive.
        e.Cancel = true;
        HidePanel();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _mainWindow?.Close();
        Shutdown(0);
    }
}
