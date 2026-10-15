using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexBar.Core.Internal;

namespace CodexBar.Core;

/// <summary>
/// Shared plumbing for the OAuth usage providers: credential outcome mapping, one bounded GET,
/// and fixed-text status mapping. Both providers are read-only and never refresh or publish
/// tokens back to the files a CLI owns.
/// </summary>
internal static class ProviderSupport
{
    public static UsageSnapshot Disabled(ProviderId id) => new()
    {
        Provider = id,
        Status = ProviderStatus.Disabled,
        Message = Messages.Disabled,
    };

    public static UsageSnapshot Unavailable(ProviderId id, string message, string? detail = null) => new()
    {
        Provider = id,
        Status = ProviderStatus.Unavailable,
        Message = message,
        Detail = detail,
    };

    public static UsageSnapshot NotConfigured(ProviderId id, string message) => new()
    {
        Provider = id,
        Status = ProviderStatus.NotConfigured,
        Message = message,
    };

    public static UsageSnapshot AuthenticationRequired(ProviderId id, string message) => new()
    {
        Provider = id,
        Status = ProviderStatus.AuthenticationRequired,
        Message = message,
    };

    /// <summary>Maps a non-OK credential outcome to a fixed, actionable message.</summary>
    public static UsageSnapshot FromCredentialOutcome(
        ProviderId id,
        CredentialOutcome outcome,
        string product,
        string hint)
    {
        var message = outcome switch
        {
            CredentialOutcome.Missing => Messages.MissingCredentials(product, hint),
            CredentialOutcome.Unreadable => Messages.UnreadableCredentials(product),
            CredentialOutcome.Malformed => Messages.CorruptCredentials(product, hint),
            CredentialOutcome.TooLarge => Messages.OversizeCredentials(product),
            CredentialOutcome.NotLocalPath => Messages.NotALocalPath(product),
            CredentialOutcome.ApiKeyOnly => Messages.ApiKeyOnly(product, hint),
            CredentialOutcome.McpOAuthOnly => Messages.ApiKeyOnly(product, hint),
            CredentialOutcome.MissingAccessToken => Messages.MissingAccessToken(product, hint),
            _ => Messages.InvalidResponse(product),
        };

        // Only a genuine sign-in problem is actionable by re-authenticating; everything else is
        // reported as unavailable so the tray does not nag for a login that is already present.
        var status = outcome switch
        {
            CredentialOutcome.Missing or
                CredentialOutcome.ApiKeyOnly or
                CredentialOutcome.McpOAuthOnly or
                CredentialOutcome.MissingAccessToken => ProviderStatus.NotConfigured,
            _ => ProviderStatus.Unavailable,
        };

        return new UsageSnapshot
        {
            Provider = id,
            Status = status,
            Message = message,
        };
    }

    /// <summary>
    /// Maps an HTTP response to a snapshot failure, or returns null when the caller should parse
    /// the body. The response body is never copied into user-visible text.
    /// </summary>
    public static UsageSnapshot? FromStatusCode(
        ProviderId id,
        string product,
        HttpStatusCode status,
        HttpResult result,
        string hint)
    {
        if (status == HttpStatusCode.OK)
        {
            return null;
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return AuthenticationRequired(id, Messages.Unauthorized(product, (int)status, hint));
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            return Unavailable(id, Messages.RateLimited(product, DescribeRetryAfter(result.Header("Retry-After"))));
        }

        if ((int)status is >= 500 and <= 599)
        {
            return Unavailable(id, Messages.ServerError(product, (int)status));
        }

        // A 3xx here means the endpoint redirected. Redirects stay disabled, and a redirected
        // request must not be followed with an attached bearer token.
        if ((int)status is >= 300 and <= 399)
        {
            return Unavailable(id, Messages.InvalidResponse(product), "端点返回重定向，已按安全策略拒绝跟随。");
        }

        return Unavailable(id, Messages.Unauthorized(product, (int)status, hint));
    }

    /// <summary>Renders a Retry-After header as a short hint without echoing arbitrary header text.</summary>
    private static string? DescribeRetryAfter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw.Trim(), out var seconds) && seconds is > 0 and <= 86400)
        {
            return seconds >= 60
                ? $"{seconds / 60} 分钟"
                : $"{seconds} 秒";
        }

        // Dates are not echoed verbatim; the message stays generic instead.
        return null;
    }

    /// <summary>
    /// Runs one request and converts transport failures into snapshots. A caller-initiated
    /// cancellation is rethrown as <see cref="OperationCanceledException"/> rather than being
    /// reported as an Unavailable provider.
    /// </summary>
    public static async Task<(UsageSnapshot? Failure, HttpResult? Result)> TrySendAsync(
        ProviderId id,
        string product,
        HttpTransport transport,
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = requestFactory();

        try
        {
            var result = await transport
                .SendAsync(request, timeout, cancellationToken)
                .ConfigureAwait(false);

            var failure = FromStatusCode(id, product, result.StatusCode, result, Messages.HintFor(product));
            return (failure, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked us to stop: propagate instead of turning it into a provider fault.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancellation that did not come from the caller is our own deadline.
            return (Unavailable(id, Messages.Timeout(product, (int)Math.Round(timeout.TotalSeconds))), null);
        }
        catch (ResponseTooLargeException)
        {
            return (Unavailable(id, Messages.InvalidResponse(product), "响应体积超过上限，已放弃解析。"), null);
        }
        catch (HttpRequestException)
        {
            return (Unavailable(id, Messages.NetworkError(product)), null);
        }
    }

    public static UsageSnapshot ParseFailure(ProviderId id, string product, Exception exception) => exception switch
    {
        JsonException => Unavailable(id, Messages.InvalidResponse(product)),
        _ => Unavailable(id, Messages.InvalidResponse(product)),
    };

    /// <summary>Builds a successful snapshot, or reports Unavailable when no window was usable.</summary>
    public static UsageSnapshot Success(
        ProviderId id,
        string product,
        ProviderUsageParseResult parsed,
        string? plan,
        string? account,
        string source)
    {
        if (parsed.Windows.Count == 0)
        {
            return new UsageSnapshot
            {
                Provider = id,
                Status = ProviderStatus.Unavailable,
                Plan = plan,
                Account = account,
                Message = Messages.NoWindows(product),
                Source = source,
            };
        }

        return new UsageSnapshot
        {
            Provider = id,
            Status = ProviderStatus.Ready,
            Plan = plan,
            Account = account,
            Windows = parsed.Windows,
            Source = source,
        };
    }
}

/// <summary>
/// Codex usage from the OpenAI OAuth endpoint. Reads <c>auth.json</c> only; token refresh stays
/// with the Codex CLI, so an expired token is reported and left untouched.
/// </summary>
public sealed class CodexUsageProvider : IUsageProvider, IDisposable
{
    public const string ProductName = "Codex";

    private readonly HttpClient? _client;
    private readonly TimeSpan _timeout;
    private HttpTransport? _transport;

    /// <param name="client">Caller-owned client, typically for tests. When null a private client with redirects disabled is used.</param>
    /// <param name="requestTimeout">Per-request deadline; defaults to 30 seconds.</param>
    public CodexUsageProvider(HttpClient? client = null, TimeSpan? requestTimeout = null)
    {
        _client = client;
        _timeout = requestTimeout ?? TimeSpan.FromSeconds(HttpDefaults.DefaultTimeoutSeconds);
    }

    public ProviderId Id => ProviderId.Codex;

    public async Task<UsageSnapshot> FetchAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!settings.CodexEnabled)
        {
            return ProviderSupport.Disabled(Id);
        }

        var load = CodexCredentialsReader.Load(settings);
        if (load.Outcome != CredentialOutcome.Ok || load.Credentials is not { } credentials)
        {
            return ProviderSupport.FromCredentialOutcome(
                Id,
                load.Outcome,
                ProductName,
                Messages.HintFor(ProductName));
        }

        if (credentials.IsExpired(DateTimeOffset.UtcNow))
        {
            return ProviderSupport.AuthenticationRequired(
                Id,
                Messages.Expired(ProductName, Messages.HintFor(ProductName)));
        }

        _transport ??= HttpTransport.Create(_client);

        var (failure, result) = await ProviderSupport.TrySendAsync(
            Id,
            ProductName,
            _transport,
            () => BuildRequest(credentials),
            _timeout,
            cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        try
        {
            var parsed = CodexUsageParser.Parse(result!.Body);
            return ProviderSupport.Success(
                Id,
                ProductName,
                parsed,
                parsed.Plan,
                credentials.AccountId,
                "OAuth");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return ProviderSupport.ParseFailure(Id, ProductName, ex);
        }
    }

    private static HttpRequestMessage BuildRequest(CodexCredentials credentials)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, CodexUsageParser.UsageEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credentials.AccessToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "CodexBar-Windows");

        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
        }

        return request;
    }

    public void Dispose()
    {
        _transport?.Dispose();
        _transport = null;
    }
}

/// <summary>
/// Claude usage from the Anthropic OAuth endpoint. Reads <c>.credentials.json</c> only and never
/// refreshes the account's shared OAuth token.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider, IDisposable
{
    public const string ProductName = "Claude";

    /// <summary>The OAuth usage endpoint requires this beta header, matching the upstream client.</summary>
    public const string BetaHeader = "oauth-2025-04-20";

    private readonly HttpClient? _client;
    private readonly TimeSpan _timeout;
    private HttpTransport? _transport;

    /// <param name="client">Caller-owned client, typically for tests. When null a private client with redirects disabled is used.</param>
    /// <param name="requestTimeout">Per-request deadline; defaults to 30 seconds.</param>
    public ClaudeUsageProvider(HttpClient? client = null, TimeSpan? requestTimeout = null)
    {
        _client = client;
        _timeout = requestTimeout ?? TimeSpan.FromSeconds(HttpDefaults.DefaultTimeoutSeconds);
    }

    public ProviderId Id => ProviderId.Claude;

    public async Task<UsageSnapshot> FetchAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!settings.ClaudeEnabled)
        {
            return ProviderSupport.Disabled(Id);
        }

        var load = ClaudeCredentialsReader.Load(settings);
        if (load.Outcome != CredentialOutcome.Ok || load.Credentials is not { } credentials)
        {
            return ProviderSupport.FromCredentialOutcome(
                Id,
                load.Outcome,
                ProductName,
                Messages.HintFor(ProductName));
        }

        if (credentials.IsExpired(DateTimeOffset.UtcNow))
        {
            return ProviderSupport.AuthenticationRequired(
                Id,
                Messages.Expired(ProductName, Messages.HintFor(ProductName)));
        }

        _transport ??= HttpTransport.Create(_client);

        var (failure, result) = await ProviderSupport.TrySendAsync(
            Id,
            ProductName,
            _transport,
            () => BuildRequest(credentials, ClaudeUsageParser.UsageEndpointWithQuery),
            _timeout,
            cancellationToken).ConfigureAwait(false);

        // The reset-inventory flag is optional; the upstream client retries the plain path when the
        // service rejects the query form, and so do we — once, with the same credential.
        if (failure is not null && result?.StatusCode == HttpStatusCode.BadRequest)
        {
            (failure, result) = await ProviderSupport.TrySendAsync(
                Id,
                ProductName,
                _transport,
                () => BuildRequest(credentials, ClaudeUsageParser.UsageEndpoint),
                _timeout,
                cancellationToken).ConfigureAwait(false);
        }

        if (failure is not null)
        {
            return failure;
        }

        try
        {
            var parsed = ClaudeUsageParser.Parse(result!.Body);
            var plan = ClaudeUsageParser.MapPlan(credentials.SubscriptionType, credentials.RateLimitTier);
            return ProviderSupport.Success(Id, ProductName, parsed, plan, null, "OAuth");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return ProviderSupport.ParseFailure(Id, ProductName, ex);
        }
    }

    internal static HttpRequestMessage BuildRequest(ClaudeCredentials credentials, string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credentials.AccessToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.1.0 (external, cli)");
        request.Content = new StringContent(string.Empty);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    public void Dispose()
    {
        _transport?.Dispose();
        _transport = null;
    }
}
