using CodexBar.Core;

namespace CodexBar.Windows.Services;

/// <summary>Deterministic demo data: no credentials, no network, no user config.</summary>
public sealed class DemoCodexProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.Codex;

    public Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        return Task.FromResult(new UsageSnapshot
        {
            Provider = ProviderId.Codex,
            Status = ProviderStatus.Ready,
            Plan = "Plus",
            Account = "demo@example.com",
            Source = "OAuth（演示）",
            FetchedAt = now,
            Windows = new[]
            {
                new UsageWindow { Name = "5 小时", UsedPercent = 38, ResetsAt = now.AddHours(2).AddMinutes(15) },
                new UsageWindow { Name = "每周", UsedPercent = 62, ResetsAt = now.AddDays(4).AddHours(3) },
            },
        });
    }
}

public sealed class DemoClaudeProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.Claude;

    public Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        return Task.FromResult(new UsageSnapshot
        {
            Provider = ProviderId.Claude,
            Status = ProviderStatus.Ready,
            Plan = "Pro",
            Account = "demo@example.com",
            Source = "OAuth（演示）",
            FetchedAt = now.AddMinutes(-1),
            Windows = new[]
            {
                new UsageWindow { Name = "5 小时", UsedPercent = 92, ResetsAt = now.AddHours(1).AddMinutes(5) },
                new UsageWindow { Name = "每周", UsedPercent = 41, ResetsAt = now.AddDays(5) },
            },
        });
    }
}
