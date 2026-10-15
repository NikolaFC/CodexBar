using System.Collections.Concurrent;
using CodexBar.Core;

namespace CodexBar.Windows.Services;

/// <summary>
/// Fetches every enabled provider in parallel. Cycles are serialized by a
/// semaphore; a queued cycle waits for the running one and is abandoned if its
/// token is cancelled first.
///
/// Providers normally return <see cref="ProviderStatus.Unavailable"/> instead
/// of throwing, so <see cref="MergeWithCache"/> is what keeps the last good
/// numbers on screen. A failure is only merged when it belongs to the same
/// credential file, identified by a non-secret fingerprint (resolved path plus
/// file length and last-write time, never file contents) and the same account.
/// Anything else clears the cache so one account's usage can never be shown
/// under another's credentials.
/// </summary>
public sealed class UsageService : IDisposable
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly bool _probeCredentialFiles;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<ProviderId, CacheEntry> _cache = new();
    private volatile bool _disposed;

    /// <param name="probeCredentialFiles">
    /// False for demo runs: the fingerprint becomes a fixed value and no file
    /// metadata is read from the user profile.
    /// </param>
    public UsageService(IReadOnlyList<IUsageProvider> providers, bool probeCredentialFiles = true)
    {
        _providers = providers;
        _probeCredentialFiles = probeCredentialFiles;
    }

    /// <summary>Publishes a snapshot together with the refresh generation that produced it.</summary>
    public event Action<UsageSnapshot, int>? SnapshotPublished;

    public bool IsRefreshing { get; private set; }

    public async Task RefreshAsync(AppSettings settings, int generation, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var tasks = _providers
                .Select(provider => FetchOneAsync(provider, settings, generation, cancellationToken))
                .ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh or shutting down.
        }
        catch (Exception ex)
        {
            AppLog.Error("refresh-cycle-failed", ex);
        }
        finally
        {
            IsRefreshing = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the service stopped. The semaphore is deliberately left alone: a
    /// cancelled cycle may still run its <c>finally</c> block, and disposing
    /// the gate underneath it would turn a clean shutdown into an
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose() => _disposed = true;

    private async Task FetchOneAsync(
        IUsageProvider provider,
        AppSettings settings,
        int generation,
        CancellationToken cancellationToken)
    {
        var enabled = provider.Id == ProviderId.Codex ? settings.CodexEnabled : settings.ClaudeEnabled;
        if (!enabled)
        {
            _cache.TryRemove(provider.Id, out _);
            Publish(
                new UsageSnapshot
                {
                    Provider = provider.Id,
                    Status = ProviderStatus.Disabled,
                    Message = "已在设置中关闭。",
                },
                generation);
            return;
        }

        var fingerprint = Fingerprint(provider.Id, settings);

        UsageSnapshot snapshot;
        try
        {
            snapshot = await provider.FetchAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error(provider.Id == ProviderId.Codex ? "codex-fetch-threw" : "claude-fetch-threw", ex);
            snapshot = new UsageSnapshot
            {
                Provider = provider.Id,
                Status = ProviderStatus.Unavailable,
                Message = "无法获取额度数据。",
                Detail = $"错误类型 {ex.GetType().Name}",
            };
        }

        Publish(MergeWithCache(provider.Id, fingerprint, snapshot), generation);
    }

    private UsageSnapshot MergeWithCache(ProviderId provider, string fingerprint, UsageSnapshot snapshot)
    {
        switch (snapshot.Status)
        {
            case ProviderStatus.Ready:
                _cache[provider] = new CacheEntry(snapshot, fingerprint);
                return snapshot;

            case ProviderStatus.Unavailable:
                if (_cache.TryGetValue(provider, out var cached)
                    && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal)
                    && (snapshot.Account is null
                        || string.Equals(cached.Snapshot.Account, snapshot.Account, StringComparison.OrdinalIgnoreCase)))
                {
                    return snapshot with
                    {
                        Plan = snapshot.Plan ?? cached.Snapshot.Plan,
                        Account = snapshot.Account ?? cached.Snapshot.Account,
                        Source = snapshot.Source,
                        Windows = cached.Snapshot.Windows,
                        FetchedAt = cached.Snapshot.FetchedAt,
                    };
                }

                _cache.TryRemove(provider, out _);
                return snapshot;

            default:
                // NotConfigured / AuthenticationRequired / Disabled: the numbers
                // may belong to a credential we can no longer use.
                _cache.TryRemove(provider, out _);
                return snapshot;
        }
    }

    /// <summary>
    /// Non-secret identity of the credential file: resolved path, length, and
    /// last-write time. File contents, and therefore any token, are never read.
    /// </summary>
    private string Fingerprint(ProviderId provider, AppSettings settings)
    {
        if (!_probeCredentialFiles)
        {
            return provider == ProviderId.Codex ? "demo:codex" : "demo:claude";
        }

        try
        {
            var path = provider == ProviderId.Codex
                ? CodexCredentialsReader.ResolvePath(settings)
                : ClaudeCredentialsReader.ResolvePath(settings);

            // Validate before touching the path: a plain FileInfo on a UNC or
            // device path can dispatch an SMB request to a host the user did not
            // choose, and Normalize also rejects a UNC path produced when the
            // process working directory is a network share.
            if (!LocalPathProbe.IsLocal(path))
            {
                // Rejected paths still need to be distinguishable from each
                // other, otherwise switching from one non-local path to another
                // would look like "the same credential" and merge old windows.
                return $"not-local|{path}";
            }

            var full = LocalPathProbe.Normalize(path);
            var info = new FileInfo(full);
            return info.Exists
                ? $"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
                : $"{full}|absent";
        }
        catch (Exception)
        {
            // An unreadable path simply cannot be matched against a cached entry.
            return "unresolved";
        }
    }

    private void Publish(UsageSnapshot snapshot, int generation)
        => SnapshotPublished?.Invoke(snapshot, generation);

    private sealed record CacheEntry(UsageSnapshot Snapshot, string Fingerprint);
}
