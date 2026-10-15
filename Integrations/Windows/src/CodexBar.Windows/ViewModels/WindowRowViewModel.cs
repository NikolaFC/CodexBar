using System.Windows;
using System.Windows.Media;

namespace CodexBar.Windows.ViewModels;

/// <summary>One quota window (5 小时 / 每周 / …) inside a provider card.</summary>
public sealed class WindowRowViewModel : ObservableObject
{
    private readonly string _name;
    private readonly double _usedPercent;
    private DateTimeOffset? _resetsAt;
    private DisplayMode _mode;

    public WindowRowViewModel(string name, double usedPercent, DateTimeOffset? resetsAt, Brush accent, DisplayMode mode)
    {
        _name = name;
        _usedPercent = Math.Clamp(usedPercent, 0, 100);
        _resetsAt = resetsAt;
        AccentBrush = accent;
        _mode = mode;
    }

    public string Name => _name;

    public Brush AccentBrush { get; }

    public double UsedPercent => _usedPercent;

    public double RemainingPercent => Math.Clamp(100 - _usedPercent, 0, 100);

    public double BarPercent => _mode == DisplayMode.Used ? _usedPercent : RemainingPercent;

    /// <summary>Bar tint: accent normally, amber past 75% used, red past 90% used.</summary>
    public Brush BarBrush => _usedPercent >= 90
        ? (Brush)System.Windows.Application.Current.Resources["AccentDangerBrush"]
        : _usedPercent >= 75
            ? (Brush)System.Windows.Application.Current.Resources["AccentWarnBrush"]
            : AccentBrush;

    public string PercentText => _mode == DisplayMode.Used
        ? $"已用 {FormatPercent(_usedPercent)}"
        : $"还剩 {FormatPercent(RemainingPercent)}";

    public string DetailText
    {
        get
        {
            var counterpart = _mode == DisplayMode.Used
                ? $"还剩 {FormatPercent(RemainingPercent)}"
                : $"已用 {FormatPercent(_usedPercent)}";
            var reset = ResetCountdownText(_resetsAt, DateTimeOffset.Now);
            return reset.Length == 0 ? counterpart : $"{counterpart} · {reset}";
        }
    }

    public void SetMode(DisplayMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        RaisePropertyChanged(nameof(BarPercent));
        RaisePropertyChanged(nameof(PercentText));
        RaisePropertyChanged(nameof(DetailText));
    }

    public void Tick()
    {
        RaisePropertyChanged(nameof(DetailText));
    }

    internal static string FormatPercent(double value)
    {
        var rounded = Math.Round(value, value is > 0 and < 1 ? 1 : 0, MidpointRounding.AwayFromZero);
        return rounded % 1 == 0 ? $"{(int)rounded}%" : $"{rounded:0.#}%";
    }

    /// <summary>Maps the core parsers' English window names into the panel's Chinese labels.</summary>
    internal static string LocalizeName(string name) => name switch
    {
        "Session" => "5 小时会话",
        "Weekly" => "每周",
        "Daily Routines" => "每日例程",
        "Sonnet" => "Sonnet 每周",
        "Opus" => "Opus 每周",
        _ => name,
    };

    internal static string ResetCountdownText(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null)
        {
            return string.Empty;
        }

        var delta = resetsAt.Value - now;
        if (delta <= TimeSpan.Zero)
        {
            return "即将重置";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{Math.Max(1, (int)Math.Round(delta.TotalMinutes))} 分钟后重置";
        }

        if (delta.TotalHours < 24)
        {
            var hours = (int)delta.TotalHours;
            var minutes = delta.Minutes;
            return minutes == 0 ? $"{hours} 小时后重置" : $"{hours} 小时 {minutes} 分后重置";
        }

        return $"{Math.Max(1, (int)Math.Round(delta.TotalDays))} 天后重置";
    }
}
