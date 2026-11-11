using System.Text.Json;
using CodexBar.Core.Internal;

namespace CodexBar.Core;

/// <summary>Result of locating and parsing a credential source. Never carries token text.</summary>
public enum CredentialOutcome
{
    /// <summary>An OAuth access token was found.</summary>
    Ok,
    Missing,
    Unreadable,
    Malformed,
    TooLarge,
    NotLocalPath,
    /// <summary>Only a provider API key was present; OAuth quota endpoints need a login.</summary>
    ApiKeyOnly,
    /// <summary>Claude credentials held MCP OAuth state only, with no <c>claudeAiOauth</c>.</summary>
    McpOAuthOnly,
    MissingAccessToken,
}

/// <summary>
/// Codex OAuth credentials read from <c>auth.json</c>. The token is deliberately not part of the
/// public surface: <see cref="ToString"/> and any logging cannot leak it.
/// </summary>
public sealed class CodexCredentials
{
    internal CodexCredentials(string accessToken, string? accountId, DateTimeOffset? expiresAt)
    {
        AccessToken = accessToken;
        AccountId = accountId;
        ExpiresAt = expiresAt;
    }

    internal string AccessToken { get; }

    /// <summary>Workspace/account id sent as the <c>ChatGPT-Account-Id</c> header when present.</summary>
    public string? AccountId { get; }

    /// <summary>Best-effort expiry from the access token's JWT <c>exp</c> claim.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && now >= expiry;

    public override string ToString() =>
        $"{nameof(CodexCredentials)} {{ HasAccessToken = true, AccountId = {(AccountId is null ? "null" : "set")}, " +
        $"ExpiresAt = {ExpiresAt?.ToString("O") ?? "unknown"} }}";
}

public sealed class CodexCredentialLoad
{
    public required CredentialOutcome Outcome { get; init; }

    public required string Path { get; init; }

    public CodexCredentials? Credentials { get; init; }
}

/// <summary>
/// Read-only reader for <c>~/.codex/auth.json</c> (or <c>$CODEX_HOME/auth.json</c>). CodexBar never
/// refreshes, rewrites, or republishes these tokens; the Codex CLI remains their owner.
/// </summary>
public static class CodexCredentialsReader
{
    public const string HomeEnvironmentVariable = "CODEX_HOME";

    /// <summary>Explicit override &gt; <c>CODEX_HOME</c> &gt; <c>%USERPROFILE%\.codex\auth.json</c>.</summary>
    public static string ResolvePath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CodexAuthPath))
        {
            return settings.CodexAuthPath.Trim();
        }

        if (LocalFile.IsLocalPath(Environment.GetEnvironmentVariable(HomeEnvironmentVariable)) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HomeEnvironmentVariable)))
        {
            return Path.Combine(Environment.GetEnvironmentVariable(HomeEnvironmentVariable)!.Trim(), "auth.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex", "auth.json");
    }

    public static CodexCredentialLoad Load(AppSettings settings)
    {
        var path = ResolvePath(settings);

        if (!LocalFile.IsLocalPath(path))
        {
            return new CodexCredentialLoad { Outcome = CredentialOutcome.NotLocalPath, Path = path };
        }

        var read = LocalFile.TryReadBounded(path, out var bytes);
        switch (read)
        {
            case LocalReadResult.NotLocalPath:
                return new CodexCredentialLoad { Outcome = CredentialOutcome.NotLocalPath, Path = path };
            case LocalReadResult.Missing:
                return new CodexCredentialLoad { Outcome = CredentialOutcome.Missing, Path = path };
            case LocalReadResult.Unreadable:
                return new CodexCredentialLoad { Outcome = CredentialOutcome.Unreadable, Path = path };
            case LocalReadResult.TooLarge:
                return new CodexCredentialLoad { Outcome = CredentialOutcome.TooLarge, Path = path };
        }

        var parsed = Parse(LocalFile.DecodeUtf8(bytes));
        return new CodexCredentialLoad { Outcome = parsed.Outcome, Path = path, Credentials = parsed.Credentials };
    }

    /// <summary>
    /// Pure parser over the <c>auth.json</c> text. Returns <see cref="CredentialOutcome.ApiKeyOnly"/>
    /// when the file only holds <c>OPENAI_API_KEY</c>; an API key is never substituted for a missing
    /// OAuth access token.
    /// </summary>
    public static (CredentialOutcome Outcome, CodexCredentials? Credentials) Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (CredentialOutcome.Malformed, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (CredentialOutcome.Malformed, null);
            }

            var hasApiKey = !string.IsNullOrWhiteSpace(JsonUtil.NonEmptyString(root, "OPENAI_API_KEY"));
            var hasPat = !string.IsNullOrWhiteSpace(
                JsonUtil.NonEmptyString(root, "personal_access_token", "personalAccessToken"));

            if (JsonUtil.ObjectOrNull(root, "tokens") is { } tokens)
            {
                var accessToken = JsonUtil.NonEmptyString(tokens, "access_token", "accessToken");
                if (accessToken is null)
                {
                    return (hasApiKey ? CredentialOutcome.ApiKeyOnly : CredentialOutcome.MissingAccessToken, null);
                }

                var accountId = JsonUtil.FirstNonEmpty(
                    JsonUtil.NonEmptyString(tokens, "account_id", "accountId"),
                    AccountIdFromJwt(JsonUtil.NonEmptyString(tokens, "id_token", "idToken")),
                    AccountIdFromJwt(accessToken));

                return (
                    CredentialOutcome.Ok,
                    new CodexCredentials(accessToken, accountId, ExpirationFromJwt(accessToken)));
            }

            if (hasApiKey)
            {
                return (CredentialOutcome.ApiKeyOnly, null);
            }

            // Personal access tokens are a separate, unsupported v1 source; report a missing OAuth token.
            _ = hasPat;
            return (CredentialOutcome.MissingAccessToken, null);
        }
    }

    /// <summary>Best-effort <c>chatgpt_account_id</c> recovery from an OpenAI JWT payload.</summary>
    internal static string? AccountIdFromJwt(string? token)
    {
        if (TryReadJwtPayload(token) is not { } payload)
        {
            return null;
        }

        if (JsonUtil.NonEmptyString(payload, "chatgpt_account_id") is { } direct)
        {
            return direct;
        }

        if (JsonUtil.ObjectOrNull(payload, "https://api.openai.com/auth") is { } auth &&
            JsonUtil.NonEmptyString(auth, "chatgpt_account_id") is { } nested)
        {
            return nested;
        }

        if (payload.TryGetProperty("organizations", out var organizations) &&
            organizations.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in organizations.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    JsonUtil.NonEmptyString(entry, "id") is { } organizationId)
                {
                    return organizationId;
                }
            }
        }

        return null;
    }

    /// <summary>Best-effort scheduling hint from the JWT <c>exp</c> claim.</summary>
    internal static DateTimeOffset? ExpirationFromJwt(string? token)
    {
        if (TryReadJwtPayload(token) is not { } payload ||
            JsonUtil.Integer(payload, "exp") is not { } expiration)
        {
            return null;
        }

        // The upstream client only trusts plausible second-based timestamps.
        if (expiration < 1_000_000_000 || expiration > 4_102_444_800)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(expiration);
    }

    private static JsonElement? TryReadJwtPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(static part => part.Length == 0))
        {
            return null;
        }

        var encoded = parts[1].Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - (encoded.Length % 4)) % 4);

        try
        {
            var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var root = document.RootElement.Clone();
            document.Dispose();
            return root.ValueKind == JsonValueKind.Object ? root : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Claude OAuth credentials read from <c>~/.claude/.credentials.json</c> (or
/// <c>$CLAUDE_CONFIG_DIR/.credentials.json</c>). Read-only, same as the Codex reader.
/// </summary>
public sealed class ClaudeCredentials
{
    internal ClaudeCredentials(
        string accessToken,
        DateTimeOffset? expiresAt,
        string? subscriptionType,
        string? rateLimitTier)
    {
        AccessToken = accessToken;
        ExpiresAt = expiresAt;
        SubscriptionType = subscriptionType;
        RateLimitTier = rateLimitTier;
    }

    internal string AccessToken { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public string? SubscriptionType { get; }

    public string? RateLimitTier { get; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && now >= expiry;

    public override string ToString() =>
        $"{nameof(ClaudeCredentials)} {{ HasAccessToken = true, ExpiresAt = {ExpiresAt?.ToString("O") ?? "unknown"} }}";
}

public sealed class ClaudeCredentialLoad
{
    public required CredentialOutcome Outcome { get; init; }

    public required string Path { get; init; }

    public ClaudeCredentials? Credentials { get; init; }
}

public static class ClaudeCredentialsReader
{
    public const string ConfigDirectoryEnvironmentVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>Explicit override &gt; <c>CLAUDE_CONFIG_DIR</c> &gt; <c>%USERPROFILE%\.claude\.credentials.json</c>.</summary>
    public static string ResolvePath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ClaudeCredentialsPath))
        {
            return settings.ClaudeCredentialsPath.Trim();
        }

        var configured = Environment.GetEnvironmentVariable(ConfigDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && LocalFile.IsLocalPath(configured))
        {
            return Path.Combine(configured.Trim(), ".credentials.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", ".credentials.json");
    }

    public static ClaudeCredentialLoad Load(AppSettings settings)
    {
        var path = ResolvePath(settings);

        if (!LocalFile.IsLocalPath(path))
        {
            return new ClaudeCredentialLoad { Outcome = CredentialOutcome.NotLocalPath, Path = path };
        }

        var read = LocalFile.TryReadBounded(path, out var bytes);
        switch (read)
        {
            case LocalReadResult.NotLocalPath:
                return new ClaudeCredentialLoad { Outcome = CredentialOutcome.NotLocalPath, Path = path };
            case LocalReadResult.Missing:
                return new ClaudeCredentialLoad { Outcome = CredentialOutcome.Missing, Path = path };
            case LocalReadResult.Unreadable:
                return new ClaudeCredentialLoad { Outcome = CredentialOutcome.Unreadable, Path = path };
            case LocalReadResult.TooLarge:
                return new ClaudeCredentialLoad { Outcome = CredentialOutcome.TooLarge, Path = path };
        }

        var parsed = Parse(LocalFile.DecodeUtf8(bytes));
        return new ClaudeCredentialLoad { Outcome = parsed.Outcome, Path = path, Credentials = parsed.Credentials };
    }

    /// <summary>
    /// Pure parser over the credentials JSON. <c>expiresAt</c> is Unix milliseconds, matching the
    /// Claude CLI's on-disk format.
    /// </summary>
    public static (CredentialOutcome Outcome, ClaudeCredentials? Credentials) Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (CredentialOutcome.Malformed, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (CredentialOutcome.Malformed, null);
            }

            if (JsonUtil.ObjectOrNull(root, "claudeAiOauth") is { } oauth)
            {
                var accessToken = JsonUtil.NonEmptyString(oauth, "accessToken", "access_token");
                if (accessToken is null)
                {
                    return (CredentialOutcome.MissingAccessToken, null);
                }

                DateTimeOffset? expiresAt = null;
                if (JsonUtil.FiniteNumber(oauth, "expiresAt", "expires_at") is { } millis)
                {
                    var seconds = millis / 1000.0;
                    if (seconds is > -62_135_596_800 and < 253_402_300_799)
                    {
                        expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)millis);
                    }
                }

                return (
                    CredentialOutcome.Ok,
                    new ClaudeCredentials(
                        accessToken,
                        expiresAt,
                        JsonUtil.NonEmptyString(oauth, "subscriptionType", "subscription_type"),
                        JsonUtil.NonEmptyString(oauth, "rateLimitTier", "rate_limit_tier")));
            }

            if (JsonUtil.ObjectOrNull(root, "mcpOAuth") is not null)
            {
                return (CredentialOutcome.McpOAuthOnly, null);
            }

            if (!string.IsNullOrWhiteSpace(JsonUtil.NonEmptyString(root, "ANTHROPIC_API_KEY", "apiKey", "api_key")))
            {
                return (CredentialOutcome.ApiKeyOnly, null);
            }

            return (CredentialOutcome.MissingAccessToken, null);
        }
    }
}
