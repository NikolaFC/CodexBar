using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CodexBar.Core;

namespace CodexBar.Windows.ViewModels;

/// <summary>Root view model for the main panel.</summary>
public sealed class AppViewModel : ObservableObject
{
    private DisplayMode _mode;
    private string _statusLine = "正在启动…";
    private bool _isRefreshing;
    private bool _isDemo;
    private string _versionText = typeof(AppViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.1";

    public AppViewModel(bool demo, DisplayMode initialMode = DisplayMode.Remaining)
    {
        _isDemo = demo;
        _mode = initialMode;

        Cards = new ObservableCollection<ProviderCardViewModel>
        {
            new(ProviderId.Codex, "Codex", Accent("AccentCodexBrush"), initialMode),
            new(ProviderId.Claude, "Claude", Accent("AccentClaudeBrush"), initialMode),
        };

        RefreshCommand = new RelayCommand(() => RefreshRequested?.Invoke());
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());
        HideCommand = new RelayCommand(() => HideRequested?.Invoke());
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        SetRemainingModeCommand = new RelayCommand(() => SetMode(DisplayMode.Remaining));
        SetUsedModeCommand = new RelayCommand(() => SetMode(DisplayMode.Used));
    }

    public event Action? RefreshRequested;

    public event Action? OpenSettingsRequested;

    public event Action? HideRequested;

    public event Action? ExitRequested;

    public event Action<ProviderId>? OpenUsagePageRequested;

    public event Action<ProviderId>? LoginHelpRequested;

    /// <summary>Raised whenever the user switches between remaining and used.</summary>
    public event Action<DisplayMode>? ModeChanged;

    public ObservableCollection<ProviderCardViewModel> Cards { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand HideCommand { get; }

    public ICommand ExitCommand { get; }

    public ICommand SetRemainingModeCommand { get; }

    public ICommand SetUsedModeCommand { get; }

    /// <summary>Raised when the user picks the credential file for a provider.</summary>
    public bool IsDemo
    {
        get => _isDemo;
        private set => SetProperty(ref _isDemo, value);
    }

    public DisplayMode Mode => _mode;

    public bool IsRemainingMode => _mode == DisplayMode.Remaining;

    public bool IsUsedMode => _mode == DisplayMode.Used;

    public string StatusLine
    {
        get => _statusLine;
        private set => SetProperty(ref _statusLine, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RaisePropertyChanged(nameof(RefreshButtonTooltip));
            }
        }
    }

    public string RefreshButtonTooltip => IsRefreshing ? "正在刷新…" : "立即刷新";

    public string VersionText
    {
        get => _versionText;
        set => SetProperty(ref _versionText, value);
    }

    public void SetMode(DisplayMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        foreach (var card in Cards)
        {
            card.SetMode(mode);
        }

        RaisePropertyChanged(nameof(Mode));
        RaisePropertyChanged(nameof(IsRemainingMode));
        RaisePropertyChanged(nameof(IsUsedMode));
        ModeChanged?.Invoke(mode);
    }

    public void SetRefreshing(bool refreshing)
    {
        IsRefreshing = refreshing;
        if (refreshing)
        {
            StatusLine = "正在刷新…";
        }
    }

    public void SetStartupFailure(string message) => StatusLine = message;

    public void Apply(UsageSnapshot snapshot)
    {
        var card = Cards.FirstOrDefault(candidate => candidate.Id == snapshot.Provider);
        if (card is null)
        {
            return;
        }

        card.Apply(snapshot);
        card.SetActions(BuildActions(card));
        UpdateStatusLine();
    }

    public void Tick()
    {
        foreach (var card in Cards)
        {
            card.Tick();
        }

        UpdateStatusLine();
    }

    public string BuildTooltip()
    {
        var parts = new List<string>();
        foreach (var card in Cards)
        {
            var primary = card.PrimaryPercentText;
            if (primary == "--")
            {
                continue;
            }

            var label = _mode == DisplayMode.Used ? "已用" : "剩余";
            parts.Add($"{card.Title} {label} {primary}");
        }

        var text = parts.Count == 0 ? "CodexBar 正在获取额度…" : string.Join(" · ", parts);
        return text.Length > 62 ? text[..62] : text;
    }

    private void UpdateStatusLine()
    {
        if (IsRefreshing)
        {
            StatusLine = "正在刷新…";
            return;
        }

        var newest = Cards.Select(card => card.FreshnessText).Where(text => text.Length > 0).ToList();
        StatusLine = newest.Count == 0 ? "尚未获取数据" : $"最后更新 {string.Join(" / ", newest)}";
    }

    private List<CardActionViewModel> BuildActions(ProviderCardViewModel card)
    {
        var actions = new List<CardActionViewModel>();
        switch (card.Status)
        {
            case ProviderStatus.AuthenticationRequired:
                actions.Add(new CardActionViewModel(
                    "查看登录说明",
                    new RelayCommand(() => LoginHelpRequested?.Invoke(card.Id)),
                    prominent: true));
                actions.Add(new CardActionViewModel("打开设置", OpenSettingsCommand));
                break;
            case ProviderStatus.NotConfigured:
                actions.Add(new CardActionViewModel("打开设置", OpenSettingsCommand, prominent: true));
                actions.Add(new CardActionViewModel(
                    "查看登录说明",
                    new RelayCommand(() => LoginHelpRequested?.Invoke(card.Id))));
                break;
            case ProviderStatus.Disabled:
                actions.Add(new CardActionViewModel("启用该服务", OpenSettingsCommand, prominent: true));
                break;
            case ProviderStatus.Unavailable:
                actions.Add(new CardActionViewModel("立即重试", RefreshCommand, prominent: true));
                actions.Add(new CardActionViewModel(
                    "官方用量页面",
                    new RelayCommand(() => OpenUsagePageRequested?.Invoke(card.Id))));
                actions.Add(new CardActionViewModel("打开设置", OpenSettingsCommand));
                break;
            default:
                actions.Add(new CardActionViewModel(
                    "官方用量页面",
                    new RelayCommand(() => OpenUsagePageRequested?.Invoke(card.Id))));
                break;
        }

        return actions;
    }

    private static Brush Accent(string key) => (Brush)Application.Current.Resources[key];
}
