using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodexBar.Core;

namespace CodexBar.UiTests;

/// <summary>Provider whose response is supplied per call, with no network access.</summary>
internal sealed class FakeUsageProvider : IUsageProvider
{
    private int _calls;

    public FakeUsageProvider(ProviderId id, Func<AppSettings, CancellationToken, Task<UsageSnapshot>> handler)
    {
        Id = id;
        Handler = handler;
    }

    public ProviderId Id { get; }

    public Func<AppSettings, CancellationToken, Task<UsageSnapshot>> Handler { get; set; }

    public int CallCount => Volatile.Read(ref _calls);

    public Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Handler(settings, cancellationToken);
    }
}

/// <summary>Collects everything a UsageService publishes, in order.</summary>
internal sealed class SnapshotRecorder
{
    private readonly ConcurrentQueue<(UsageSnapshot Snapshot, int Generation)> _entries = new();

    public IReadOnlyList<(UsageSnapshot Snapshot, int Generation)> Entries => _entries.ToArray();

    public int Count => _entries.Count;

    public void OnPublished(UsageSnapshot snapshot, int generation) => _entries.Enqueue((snapshot, generation));

    public UsageSnapshot Last => _entries.ToArray()[^1].Snapshot;

    public (UsageSnapshot Snapshot, int Generation) LastEntry => _entries.ToArray()[^1];
}

internal static class Fixtures
{
    public static UsageSnapshot Ready(
        ProviderId provider,
        string windowName,
        double usedPercent,
        string? account = null,
        DateTimeOffset? fetchedAt = null)
        => new()
        {
            Provider = provider,
            Status = ProviderStatus.Ready,
            Plan = "Plus",
            Account = account,
            Source = "OAuth",
            FetchedAt = fetchedAt ?? DateTimeOffset.UtcNow,
            Windows = new[]
            {
                new UsageWindow { Name = windowName, UsedPercent = usedPercent },
            },
        };

    public static UsageSnapshot Failure(ProviderId provider, ProviderStatus status, string message)
        => new()
        {
            Provider = provider,
            Status = status,
            Message = message,
        };

    public static AppSettings SettingsFor(ProviderId provider, string? credentialPath)
        => provider == ProviderId.Codex
            ? new AppSettings { CodexAuthPath = credentialPath, ClaudeEnabled = false }
            : new AppSettings { ClaudeCredentialsPath = credentialPath, CodexEnabled = false };
}
