using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CodexBar.Core;

namespace CodexBar.Windows.ViewModels;

/// <summary>Rendering state for one provider card (Codex or Claude).</summary>
public sealed class ProviderCardViewModel : ObservableObject
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    private UsageSnapshot? _snapshot;
    private DisplayMode _mode;
    private string _plan = string.Empty;
    private string _statusText = "等待刷新";
    private Brush _statusBrush = Brushes.Gray;
    private string _message = string.Empty;
    private string _freshnessText = string.Empty;
    private string _sourceText = string.Empty;
    private string _primaryPercentText = "--";
    private string _primaryCaption = string.Empty;

    public ProviderCardViewModel(ProviderId id, string title, Brush accent, DisplayMode mode)
    {
        Id = id;
        Title = title;
        AccentBrush = accent;
        _mode = mode;
    }

    public ProviderId Id { get; }

    public string Title { get; }

    public Brush AccentBrush { get; }

    public ObservableCollection<WindowRowViewModel> Windows { get; } = new();

    public ObservableCollection<CardActionViewModel> Actions { get; } = new();

    public string Plan
    {
        get => _plan;
        private set
        {
            if (SetProperty(ref _plan, value))
            {
                RaisePropertyChanged(nameof(HasPlan));
            }
        }
    }

    public bool HasPlan => !string.IsNullOrWhiteSpace(_plan);

    public string Account { get; private set; } = string.Empty;

    public bool HasAccount => !string.IsNullOrWhiteSpace(Account);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                RaisePropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(_message);

    public string FreshnessText
    {
        get => _freshnessText;
        private set => SetProperty(ref _freshnessText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        private set => SetProperty(ref _sourceText, value);
    }

    public string PrimaryPercentText
    {
        get => _primaryPercentText;
        private set => SetProperty(ref _primaryPercentText, value);
    }

    public string PrimaryCaption
    {
        get => _primaryCaption;
        private set => SetProperty(ref _primaryCaption, value);
    }

    public bool HasWindows => Windows.Count > 0;

    public bool IsEmpty => Windows.Count == 0;

    public bool HasActions => Actions.Count > 0;

    public bool IsRefreshing { get; set; }

    public ProviderStatus Status => _snapshot?.Status ?? ProviderStatus.Unavailable;

    public void SetMode(DisplayMode mode)
    {
        _mode = mode;
        foreach (var row in Windows)
        {
            row.SetMode(mode);
        }

        UpdatePrimary();
    }

    public void Apply(UsageSnapshot snapshot)
    {
        _snapshot = snapshot;

        Plan = string.IsNullOrWhiteSpace(snapshot.Plan) ? string.Empty : snapshot.Plan!;
        Account = snapshot.Account ?? string.Empty;
        RaisePropertyChanged(nameof(Account));
        RaisePropertyChanged(nameof(HasAccount));

        Windows.Clear();
        foreach (var window in snapshot.Windows)
        {
            if (string.IsNullOrWhiteSpace(window.Name))
            {
                continue;
            }

            Windows.Add(new WindowRowViewModel(
                WindowRowViewModel.LocalizeName(window.Name),
                window.UsedPercent,
                window.ResetsAt,
                AccentBrush,
                _mode));
        }

        RaisePropertyChanged(nameof(HasWindows));
        RaisePropertyChanged(nameof(IsEmpty));

        Message = BuildMessage(snapshot);
        StatusText = DescribeStatus(snapshot);
        StatusBrush = StatusColor(snapshot);
        SourceText = string.IsNullOrWhiteSpace(snapshot.Source) ? string.Empty : $"来源 {snapshot.Source}";
        UpdateFreshness();
        UpdatePrimary();
    }

    public void SetActions(IEnumerable<CardActionViewModel> actions)
    {
        Actions.Clear();
        foreach (var action in actions)
        {
            Actions.Add(action);
        }

        RaisePropertyChanged(nameof(HasActions));
    }

    public void Tick()
    {
        UpdateFreshness();
        foreach (var row in Windows)
        {
            row.Tick();
        }
    }

    private void UpdateFreshness()
    {
        var fetchedAt = _snapshot?.FetchedAt;
        if (fetchedAt is null)
        {
            FreshnessText = string.Empty;
            return;
        }

        var age = DateTimeOffset.Now - fetchedAt.Value;
        var label = age < TimeSpan.FromSeconds(60)
            ? "刚刚更新"
            : age < TimeSpan.FromHours(1)
                ? $"{Math.Max(1, (int)age.TotalMinutes)} 分钟前更新"
                : age < TimeSpan.FromDays(1)
                    ? $"{Math.Max(1, (int)age.TotalHours)} 小时前更新"
                    : $"{Math.Max(1, (int)age.TotalDays)} 天前更新";

        if (Status == ProviderStatus.Unavailable && Windows.Count > 0 && age >= StaleAfter)
        {
            label += " · 旧数据";
        }

        FreshnessText = label;
    }

    private void UpdatePrimary()
    {
        if (Windows.Count == 0)
        {
            PrimaryPercentText = "--";
            PrimaryCaption = _mode == DisplayMode.Used ? "已用" : "剩余";
            return;
        }

        var primary = Windows.OrderByDescending(window => window.UsedPercent).First();
        var value = _mode == DisplayMode.Used ? primary.UsedPercent : primary.RemainingPercent;
        PrimaryPercentText = WindowRowViewModel.FormatPercent(value);
        PrimaryCaption = $"{primary.Name} · {(_mode == DisplayMode.Used ? "已用" : "剩余")}";
    }

    private static string BuildMessage(UsageSnapshot snapshot)
    {
        var status = snapshot.Status;
        if (status == ProviderStatus.Ready)
        {
            return snapshot.Windows.Count == 0 ? "服务已返回数据，但当前没有可显示的额度窗口。" : string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.Message))
        {
            parts.Add(snapshot.Message!);
        }

        switch (status)
        {
            case ProviderStatus.NotConfigured:
                parts.Add("未找到凭据文件，可在设置中指定路径或先在本机完成 CLI 登录。");
                break;
            case ProviderStatus.AuthenticationRequired:
                parts.Add("凭据已过期。请重新运行 CLI 登录命令，CodexBar 只读取凭据，不会自动刷新令牌。");
                break;
            case ProviderStatus.Unavailable:
                parts.Add(snapshot.Windows.Count > 0
                    ? "最近一次刷新失败，显示的是上次成功获取的数据。"
                    : "无法连接到用量服务，请检查网络后重试。");
                break;
            case ProviderStatus.Disabled:
                parts.Add("该服务当前已在设置中关闭。");
                break;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Detail))
        {
            parts.Add(snapshot.Detail!);
        }

        return string.Join(" ", parts);
    }

    private static string DescribeStatus(UsageSnapshot snapshot) => snapshot.Status switch
    {
        ProviderStatus.Ready => "正常",
        ProviderStatus.Unavailable => snapshot.Windows.Count > 0 ? "数据可能已过期" : "暂不可用",
        ProviderStatus.AuthenticationRequired => "需要重新登录",
        ProviderStatus.NotConfigured => "未配置",
        ProviderStatus.Disabled => "已关闭",
        _ => "未知",
    };

    private static Brush StatusColor(UsageSnapshot snapshot) => snapshot.Status switch
    {
        ProviderStatus.Ready => (Brush)Application.Current.Resources["AccentOkBrush"],
        ProviderStatus.Unavailable => snapshot.Windows.Count > 0
            ? (Brush)Application.Current.Resources["AccentWarnBrush"]
            : (Brush)Application.Current.Resources["AccentDangerBrush"],
        ProviderStatus.AuthenticationRequired => (Brush)Application.Current.Resources["AccentDangerBrush"],
        ProviderStatus.NotConfigured => (Brush)Application.Current.Resources["AccentWarnBrush"],
        ProviderStatus.Disabled => (Brush)Application.Current.Resources["TextMutedBrush"],
        _ => (Brush)Application.Current.Resources["TextMutedBrush"],
    };
}
