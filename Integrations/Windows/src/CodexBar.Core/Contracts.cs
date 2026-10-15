namespace CodexBar.Core;

public enum ProviderId { Codex, Claude }
public enum ProviderStatus { Ready, NotConfigured, AuthenticationRequired, Unavailable, Disabled }

public sealed record UsageWindow
{
    public required string Name { get; init; }
    public required double UsedPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record UsageSnapshot
{
    public required ProviderId Provider { get; init; }
    public required ProviderStatus Status { get; init; }
    public string? Plan { get; init; }
    public string? Account { get; init; }
    public string? Message { get; init; }
    public string? Detail { get; init; }
    public IReadOnlyList<UsageWindow> Windows { get; init; } = Array.Empty<UsageWindow>();
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Source { get; init; } = "OAuth";
}

public sealed record AppSettings
{
    public int RefreshSeconds { get; init; } = 120;
    public bool CodexEnabled { get; init; } = true;
    public bool ClaudeEnabled { get; init; } = true;
    public string? CodexAuthPath { get; init; }
    public string? ClaudeCredentialsPath { get; init; }
    public bool StartWithWindows { get; init; }
    public bool NotifyAtLowQuota { get; init; }
    public bool ShowRemaining { get; init; } = true;
}

public interface IUsageProvider
{
    ProviderId Id { get; }
    Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
