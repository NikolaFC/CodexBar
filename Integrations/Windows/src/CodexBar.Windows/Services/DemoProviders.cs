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

public sealed class DemoKimiProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.Kimi;

    public Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        UsageSnapshot Account(double session, double weekly, int days) => new()
        {
            Provider = ProviderId.Kimi,
            Status = ProviderStatus.Ready,
            Source = "API Key（演示）",
            FetchedAt = now,
            Windows = new[]
            {
                new UsageWindow { Name = "5小时", UsedPercent = session, ResetsAt = now.AddHours(3) },
                new UsageWindow { Name = "7天", UsedPercent = weekly, ResetsAt = now.AddDays(days) },
            },
        };

        // Two keys, one exhausted: shows how the pooled card headlines the account with headroom.
        return Task.FromResult(AccountPool.Combine(ProviderId.Kimi, new[]
        {
            new PoolMember("KIMI_API_KEY", Account(0, 100, 1)),
            new PoolMember("KIMI_API_KEY_SEC", Account(12, 57, 3)),
        }));
    }
}

public sealed class DemoOpenCodeGoProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.OpenCodeGo;

    public Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        return Task.FromResult(new UsageSnapshot
        {
            Provider = ProviderId.OpenCodeGo,
            Status = ProviderStatus.Ready,
            Source = "API Key（演示）",
            FetchedAt = now,
            Windows = new[]
            {
                new UsageWindow { Name = "5小时", UsedPercent = 8, ResetsAt = now.AddHours(4) },
                new UsageWindow { Name = "每周", UsedPercent = 24, ResetsAt = now.AddDays(2) },
                new UsageWindow { Name = "每月", UsedPercent = 15, ResetsAt = now.AddDays(20) },
            },
        });
    }
}

public static class DemoProviders
{
    public static IUsageProvider[] All => new IUsageProvider[]
    {
        new DemoCodexProvider(),
        new DemoClaudeProvider(),
        new DemoKimiProvider(),
        new DemoOpenCodeGoProvider(),
    };
}
