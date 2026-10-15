using System.Net;
using CodexBar.Core;

namespace CodexBar.Tests;

internal static class ProviderTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(250);

    public static void Register()
    {
        TestHarness.AddAsync("codex: missing credentials are reported without a request", CodexMissingCredentials);
        TestHarness.AddAsync("codex: api-key-only auth.json is not used as a bearer token", CodexApiKeyOnly);
        TestHarness.AddAsync("codex: tokens without access_token never fall back to the api key", CodexMissingAccessToken);
        TestHarness.AddAsync("codex: corrupt credentials are reported, not thrown", CodexCorruptCredentials);
        TestHarness.AddAsync("codex: sends bearer and account headers", CodexSendsHeaders);
        TestHarness.AddAsync("codex: parses a successful response into a ready snapshot", CodexSuccess);
        TestHarness.AddAsync("codex: no windows means Unavailable", CodexNoWindows);
        TestHarness.AddAsync("codex: 401 asks for a new login", CodexUnauthorized);
        TestHarness.AddAsync("codex: 429 surfaces the retry hint", CodexRateLimited);
        TestHarness.AddAsync("codex: 500 is Unavailable", CodexServerError);
        TestHarness.AddAsync("codex: a redirect is refused", CodexRedirectRefused);
        TestHarness.AddAsync("codex: an oversized response is rejected", CodexOversizeRejected);
        TestHarness.AddAsync("codex: an expired token is reported without a request", CodexExpired);
    }

    internal static AppSettings CodexSettings(string authPath) => new() { CodexAuthPath = authPath };

    internal static AppSettings ClaudeSettings(string credentialsPath) =>
        new() { ClaudeCredentialsPath = credentialsPath };

    internal static (CodexUsageProvider Provider, FakeHttpHandler Handler, HttpClient Client) CodexUnderTest(
        TimeSpan? timeout = null)
    {
        var handler = new FakeHttpHandler();
        var client = new HttpClient(handler);
        return (new CodexUsageProvider(client, timeout), handler, client);
    }

    internal static (ClaudeUsageProvider Provider, FakeHttpHandler Handler, HttpClient Client) ClaudeUnderTest(
        TimeSpan? timeout = null)
    {
        var handler = new FakeHttpHandler();
        var client = new HttpClient(handler);
        return (new ClaudeUsageProvider(client, timeout), handler, client);
    }

    private static async Task CodexMissingCredentials()
    {
        var directory = TempWorkspace.NewDirectory("missing-codex");
        var path = Path.Combine(directory, "auth.json");
        var (provider, handler, _) = CodexUnderTest();

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.NotConfigured, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "未找到", "actionable message");
        Check.Equal(0, handler.SendCount, "no request is made without credentials");
    }

    private static async Task CodexApiKeyOnly()
    {
        var directory = TempWorkspace.NewDirectory("api-key-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexApiKeyOnlyAuthJson);
        var (provider, handler, _) = CodexUnderTest();

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.NotConfigured, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "API Key", "explains that an API key is not a login");
        Check.Equal(0, handler.SendCount, "an API key must never be sent to the usage endpoint");
    }

    private static async Task CodexMissingAccessToken()
    {
        var directory = TempWorkspace.NewDirectory("no-access-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexTokensWithoutAccessTokenJson);
        var (provider, handler, _) = CodexUnderTest();

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.NotConfigured, snapshot.Status, "status");
        Check.Equal(0, handler.SendCount, "no request is made");
    }

    private static async Task CodexCorruptCredentials()
    {
        var directory = TempWorkspace.NewDirectory("corrupt-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", "{ this is not json");
        var (provider, handler, _) = CodexUnderTest();

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "损坏", "actionable message");
        Check.Equal(0, handler.SendCount, "no request is made");
    }

    private static async Task CodexSendsHeaders()
    {
        var directory = TempWorkspace.NewDirectory("headers-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = CodexUnderTest();
        handler.Enqueue(HttpStatusCode.OK, Fixtures.CodexUsageJson);

        await provider.FetchAsync(CodexSettings(path));

        Check.Equal(1, handler.SendCount, "one request");
        var request = handler.Requests[0];
        Check.Equal(CodexUsageParser.UsageEndpoint, request.RequestUri!.AbsoluteUri, "fixed official endpoint");
        Check.Equal(
            $"Bearer {Fixtures.FakeCodexAccessToken}",
            request.Headers.GetValues("Authorization").Single(),
            "bearer token");
        Check.Equal(
            "acct_explicit_123",
            request.Headers.GetValues("ChatGPT-Account-Id").Single(),
            "account header");
    }

    private static async Task CodexSuccess()
    {
        var directory = TempWorkspace.NewDirectory("success-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = CodexUnderTest();
        handler.Enqueue(HttpStatusCode.OK, Fixtures.CodexUsageJson);

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.Ready, snapshot.Status, "status");
        Check.Equal(ProviderId.Codex, snapshot.Provider, "provider");
        Check.Equal(5, snapshot.Windows.Count, "windows");
        Check.Equal("Pro 20x", snapshot.Plan, "plan");
        Check.Equal("acct_explicit_123", snapshot.Account, "account");
    }

    private static async Task CodexNoWindows()
    {
        var directory = TempWorkspace.NewDirectory("nowindows-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = CodexUnderTest();
        handler.Enqueue(HttpStatusCode.OK, Fixtures.CodexNoWindowsJson);

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "未返回任何有效的额度窗口", "actionable message");
    }

    private static async Task CodexUnauthorized()
    {
        var directory = TempWorkspace.NewDirectory("401-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = CodexUnderTest();
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""");

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.AuthenticationRequired, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "401", "reports the status");
        Check.DoesNotContain(snapshot.Message!, "unauthorized", "the raw body is not echoed");
    }

    private static async Task CodexRateLimited()
    {
        var directory = TempWorkspace.NewDirectory("429-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = CodexUnderTest();
        handler.Enqueue(
            HttpStatusCode.TooManyRequests,
            """{"error":"slow down"}""",
            ("Retry-After", "120"));

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "429", "reports the status");
        Check.Contains(snapshot.Message!, "2 分钟", "renders the retry hint");
    }

    private static async Task CodexServerError()
    {
        var directory = TempWorkspace.NewDirectory("500-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = CodexUnderTest();
        handler.Enqueue(HttpStatusCode.InternalServerError, """{"error":"boom"}""");

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "500", "reports the status");
    }

    private static async Task CodexRedirectRefused()
    {
        var directory = TempWorkspace.NewDirectory("302-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = CodexUnderTest();
        handler.EnqueueStatus(HttpStatusCode.Found);

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Equal(1, handler.SendCount, "the redirect target is never requested");
        Check.Contains(snapshot.Detail!, "重定向", "explains the refusal");
    }

    private static async Task CodexOversizeRejected()
    {
        var directory = TempWorkspace.NewDirectory("oversize-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = CodexUnderTest();

        // A valid-looking prefix followed by enough filler to cross the response size cap.
        var body = Fixtures.CodexUsageJson + new string(' ', 5 * 1024 * 1024);
        handler.EnqueueRaw(HttpStatusCode.OK, body);

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.False(snapshot.Windows.Count > 0, "a truncated body is never parsed as success");
    }

    private static async Task CodexExpired()
    {
        var directory = TempWorkspace.NewDirectory("expired-codex");
        const string expired = """
        {
          "tokens": {
            "access_token": "header.eyJleHAiOjEwMDAwMDAwMDB9.signature",
            "refresh_token": "rt-fake",
            "account_id": "acct_expired"
          }
        }
        """;
        var path = TempWorkspace.WriteFile(directory, "auth.json", expired);
        var (provider, handler, _) = CodexUnderTest();

        var snapshot = await provider.FetchAsync(CodexSettings(path));

        Check.Equal(ProviderStatus.AuthenticationRequired, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "过期", "explains the token is stale");
        Check.Equal(0, handler.SendCount, "an expired token is not sent");
    }
}
