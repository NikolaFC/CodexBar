using System.Net;
using CodexBar.Core;

namespace CodexBar.Tests;

internal static class ClaudeProviderTests
{
    public static void Register()
    {
        TestHarness.AddAsync("claude: sends bearer, beta and query headers", SendsHeaders);
        TestHarness.AddAsync("claude: a 400 retries once without the optional query flag", RetriesPlainPath);
        TestHarness.AddAsync("claude: an expired token is reported without a request", Expired);
        TestHarness.AddAsync("claude: mcp-only credentials are reported as not configured", McpOnly);
        TestHarness.AddAsync("claude: corrupt credentials are reported, not thrown", CorruptCredentials);
        TestHarness.AddAsync("providers: caller cancellation propagates", CancellationPropagates);
        TestHarness.AddAsync("providers: cancellation during a request propagates", CancellationDuringFlightPropagates);
        TestHarness.AddAsync("providers: a hanging request becomes a timeout snapshot", TimeoutSnapshot);
        TestHarness.AddAsync("providers: network failure becomes Unavailable", NetworkFailure);
        TestHarness.AddAsync("providers: disabled providers are not fetched", DisabledProvider);
        TestHarness.AddAsync("providers: snapshots never contain token text", SnapshotsAreTokenFree);
    }

    private static async Task SendsHeaders()
    {
        var directory = TempWorkspace.NewDirectory("headers-claude");
        var path = TempWorkspace.WriteFile(directory, "credentials.json", Fixtures.ClaudeCredentialsJson);
        var (provider, handler, _) = ProviderTests.ClaudeUnderTest();
        handler.Enqueue(HttpStatusCode.OK, Fixtures.ClaudeUsageJson);

        var snapshot = await provider.FetchAsync(ProviderTests.ClaudeSettings(path));

        Check.Equal(ProviderStatus.Ready, snapshot.Status, "status");
        Check.Equal(5, snapshot.Windows.Count, "windows");
        var request = handler.Requests[0];
        Check.Equal(
            ClaudeUsageParser.UsageEndpointWithQuery,
            request.RequestUri!.AbsoluteUri,
            "the reset-inventory query flag is included");
        Check.Equal(
            $"Bearer {Fixtures.FakeClaudeAccessToken}",
            request.Headers.GetValues("Authorization").Single(),
            "bearer token");
        Check.Equal(
            ClaudeUsageProvider.BetaHeader,
            request.Headers.GetValues("anthropic-beta").Single(),
            "beta header");
        Check.Equal("max", snapshot.Plan, "plan from credentials");
    }

    private static async Task RetriesPlainPath()
    {
        var directory = TempWorkspace.NewDirectory("retry-claude");
        var path = TempWorkspace.WriteFile(directory, "credentials.json", Fixtures.ClaudeCredentialsJson);
        var (provider, handler, _) = ProviderTests.ClaudeUnderTest();
        handler.EnqueueStatus(HttpStatusCode.BadRequest);
        handler.Enqueue(HttpStatusCode.OK, Fixtures.ClaudeNoModelLanesJson);

        var snapshot = await provider.FetchAsync(ProviderTests.ClaudeSettings(path));

        Check.Equal(ProviderStatus.Ready, snapshot.Status, "the plain endpoint succeeds");
        Check.Equal(2, handler.SendCount, "exactly one retry");
        Check.Equal(
            ClaudeUsageParser.UsageEndpoint,
            handler.Requests[1].RequestUri!.AbsoluteUri,
            "the retry drops the optional query flag");
    }

    private static async Task Expired()
    {
        var directory = TempWorkspace.NewDirectory("expired-claude");
        var path = TempWorkspace.WriteFile(directory, "credentials.json", Fixtures.ClaudeExpiredCredentialsJson);
        var (provider, handler, _) = ProviderTests.ClaudeUnderTest();

        var snapshot = await provider.FetchAsync(ProviderTests.ClaudeSettings(path));

        Check.Equal(ProviderStatus.AuthenticationRequired, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "过期", "explains the token is stale");
        Check.Equal(0, handler.SendCount, "an expired token is not sent");
    }

    private static async Task McpOnly()
    {
        var directory = TempWorkspace.NewDirectory("mcp-claude");
        var path = TempWorkspace.WriteFile(directory, "credentials.json", Fixtures.ClaudeMcpOnlyCredentialsJson);
        var (provider, handler, _) = ProviderTests.ClaudeUnderTest();

        var snapshot = await provider.FetchAsync(ProviderTests.ClaudeSettings(path));

        Check.Equal(ProviderStatus.NotConfigured, snapshot.Status, "status");
        Check.Equal(0, handler.SendCount, "no request is made");
    }

    private static async Task CorruptCredentials()
    {
        var directory = TempWorkspace.NewDirectory("corrupt-claude");
        var path = TempWorkspace.WriteFile(directory, "credentials.json", "{ broken");
        var (provider, handler, _) = ProviderTests.ClaudeUnderTest();

        var snapshot = await provider.FetchAsync(ProviderTests.ClaudeSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "损坏", "actionable message");
        Check.Equal(0, handler.SendCount, "no request is made");
    }

    private static async Task CancellationPropagates()
    {
        var directory = TempWorkspace.NewDirectory("cancel-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = ProviderTests.CodexUnderTest();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Check.ThrowsAsync<OperationCanceledException>(
            () => provider.FetchAsync(ProviderTests.CodexSettings(path), source.Token),
            "a pre-cancelled request throws instead of reporting Unavailable");

        Check.Equal(0, handler.SendCount, "nothing was sent");
    }

    private static async Task CancellationDuringFlightPropagates()
    {
        var directory = TempWorkspace.NewDirectory("cancel-flight-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = ProviderTests.CodexUnderTest();
        handler.EnqueueHang();
        using var source = new CancellationTokenSource();

        var task = provider.FetchAsync(ProviderTests.CodexSettings(path), source.Token);
        await Task.Delay(50);
        source.Cancel();

        await Check.ThrowsAsync<OperationCanceledException>(() => task, "in-flight cancellation propagates");
    }

    private static async Task TimeoutSnapshot()
    {
        var directory = TempWorkspace.NewDirectory("timeout-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = ProviderTests.CodexUnderTest(TimeSpan.FromMilliseconds(250));
        handler.EnqueueHang();

        var snapshot = await provider.FetchAsync(ProviderTests.CodexSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "超时", "reports a timeout rather than a cancellation");
    }

    private static async Task NetworkFailure()
    {
        var directory = TempWorkspace.NewDirectory("network-codex");
        var path = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var (provider, handler, _) = ProviderTests.CodexUnderTest();
        handler.EnqueueFailure(new HttpRequestException("simulated DNS failure"));

        var snapshot = await provider.FetchAsync(ProviderTests.CodexSettings(path));

        Check.Equal(ProviderStatus.Unavailable, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "网络", "actionable message");
        Check.DoesNotContain(snapshot.Message!, "simulated", "the exception text is not surfaced");
    }

    private static async Task DisabledProvider()
    {
        var (provider, handler, _) = ProviderTests.CodexUnderTest();

        var snapshot = await provider.FetchAsync(new AppSettings { CodexEnabled = false });

        Check.Equal(ProviderStatus.Disabled, snapshot.Status, "status");
        Check.Equal(0, handler.SendCount, "a disabled provider makes no request");
    }

    private static async Task SnapshotsAreTokenFree()
    {
        var directory = TempWorkspace.NewDirectory("tokenfree");
        var codexPath = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var claudePath = TempWorkspace.WriteFile(directory, "credentials.json", Fixtures.ClaudeCredentialsJson);

        var (codex, codexHandler, _) = ProviderTests.CodexUnderTest();
        codexHandler.Enqueue(HttpStatusCode.OK, Fixtures.CodexUsageJson);
        var codexSnapshot = await codex.FetchAsync(ProviderTests.CodexSettings(codexPath));

        var (claude, claudeHandler, _) = ProviderTests.ClaudeUnderTest();
        claudeHandler.Enqueue(HttpStatusCode.OK, Fixtures.ClaudeUsageJson);
        var claudeSnapshot = await claude.FetchAsync(ProviderTests.ClaudeSettings(claudePath));

        foreach (var snapshot in new[] { codexSnapshot, claudeSnapshot })
        {
            var rendered = string.Join(
                '|',
                snapshot.Status,
                snapshot.Plan,
                snapshot.Account,
                snapshot.Message,
                snapshot.Detail,
                snapshot.Source,
                string.Join(',', snapshot.Windows.Select(w => $"{w.Name}:{w.UsedPercent}")));

            Check.DoesNotContain(rendered, Fixtures.FakeCodexAccessToken, "no Codex token in the snapshot");
            Check.DoesNotContain(rendered, Fixtures.FakeClaudeAccessToken, "no Claude token in the snapshot");
            Check.DoesNotContain(rendered, "rt-fake-refresh-value", "no Codex refresh token in the snapshot");
            Check.DoesNotContain(rendered, "sk-ant-ort01", "no Claude refresh token in the snapshot");
        }
    }
}
