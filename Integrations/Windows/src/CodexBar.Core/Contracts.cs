namespace CodexBar.Core;

public enum ProviderId { Codex, Claude, Kimi, OpenCodeGo }
public enum ProviderStatus { Ready, NotConfigured, AuthenticationRequired, Unavailable, Disabled }

public sealed record UsageWindow
{
    public required string Name { get; init; }
    public required double UsedPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>Pool member this window belongs to; null for a single-account provider.</summary>
    public string? Account { get; init; }

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

    /// <summary>Labels of every pool member when the provider has more than one account.</summary>
    public IReadOnlyList<string> PoolAccounts { get; init; } = Array.Empty<string>();
}

public sealed record AppSettings
{
    public int RefreshSeconds { get; init; } = 120;
    public bool CodexEnabled { get; init; } = true;
    public bool ClaudeEnabled { get; init; } = true;

    /// <summary>API-key providers are opt-in, matching the upstream default for new providers.</summary>
    public bool KimiEnabled { get; init; }

    public bool OpenCodeGoEnabled { get; init; }
    public string? CodexAuthPath { get; init; }
    public string? ClaudeCredentialsPath { get; init; }

    /// <summary>
    /// Optional Hermes <c>auth.json</c>. Its <c>credential_pool.openai-codex</c> entries are read
    /// (never written) as additional Codex accounts next to the CLI login.
    /// </summary>
    public string? HermesAuthPath { get; init; }

    /// <summary>
    /// Optional dotenv-style file consulted for API keys after the process environment. Only the
    /// named key variables are read from it; the file itself is never written.
    /// </summary>
    public string? ApiKeyEnvFile { get; init; }

    public bool StartWithWindows { get; init; }
    public bool NotifyAtLowQuota { get; init; }
    public bool ShowRemaining { get; init; } = true;
}

public interface IUsageProvider
{
    ProviderId Id { get; }
    Task<UsageSnapshot> FetchAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Display names and enablement for every provider, so callers never branch on two ids.</summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderId> All { get; } =
        new[] { ProviderId.Codex, ProviderId.Claude, ProviderId.Kimi, ProviderId.OpenCodeGo };

    public static string DisplayName(ProviderId id) => id switch
    {
        ProviderId.Codex => "Codex",
        ProviderId.Claude => "Claude",
        ProviderId.Kimi => "Kimi Code",
        ProviderId.OpenCodeGo => "OpenCode Go",
        _ => id.ToString(),
    };

    public static bool IsEnabled(this AppSettings settings, ProviderId id) => id switch
    {
        ProviderId.Codex => settings.CodexEnabled,
        ProviderId.Claude => settings.ClaudeEnabled,
        ProviderId.Kimi => settings.KimiEnabled,
        ProviderId.OpenCodeGo => settings.OpenCodeGoEnabled,
        _ => false,
    };
}
