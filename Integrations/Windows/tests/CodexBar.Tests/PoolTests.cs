using System.Net;
using System.Text;
using CodexBar.Core;

namespace CodexBar.Tests;

internal static class PoolTests
{
    private const string KimiUsageJson = """
        {
          "usages": {
            "limit_5h": { "used_ratio": 0.1, "reset_time": "2026-11-07T08:51:38Z" },
            "limit_7d": { "used_ratio": 0.4, "reset_time": "2026-11-07T09:51:38Z" }
          }
        }
        """;

    public static void Register()
    {
        TestHarness.Add("pool: a single member passes through untouched", SingleMemberPassesThrough);
        TestHarness.Add("pool: members keep their windows, labelled and counted", MembersAreLabelled);
        TestHarness.Add("pool: a failed member is reported next to the ready ones", PartialFailure);
        TestHarness.Add("pool: no ready member keeps the most actionable status", NoReadyMember);
        TestHarness.Add("pool: headline is the account with the most headroom", SummaryPicksBestAccount);
        TestHarness.Add("api keys: suffixed siblings join the pool, settings-like values do not", SiblingsJoinPool);
        TestHarness.Add("api keys: the same key under two names is one account", DuplicateValuesCollapse);
        TestHarness.Add("api keys: environment siblings are discovered by name", EnvironmentSiblings);
        TestHarness.Add("hermes pool: only oauth access tokens are read", HermesPoolReader);
        TestHarness.AddAsync("kimi: two keys fetch two accounts into one card", KimiPool);
        TestHarness.AddAsync("codex: CLI and Hermes pool are merged and deduplicated by account", CodexPool);
        TestHarness.AddAsync("codex: an expired Hermes entry does not hide the working account", CodexExpiredPoolEntry);
        TestHarness.Add("codex: business prolite plan is mapped", BusinessPlanMapped);
        TestHarness.Add("settings: hermes auth path round-trips", HermesPathRoundTrip);
    }

    private static string Jwt(string accountId, long exp)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = "{\"exp\":" + exp + ",\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"" + accountId + "\"}}";
        return $"{B64("{}")}.{B64(payload)}.sig";
    }

    private static UsageSnapshot Ready(string plan, params (string Name, double Used)[] windows) => new()
    {
        Provider = ProviderId.Codex,
        Status = ProviderStatus.Ready,
        Plan = plan,
        Source = "OAuth",
        Windows = windows.Select(w => new UsageWindow { Name = w.Name, UsedPercent = w.Used }).ToArray(),
    };

    private static UsageSnapshot Failed(ProviderStatus status, string message) => new()
    {
        Provider = ProviderId.Codex,
        Status = status,
        Message = message,
    };

    private static void SingleMemberPassesThrough()
    {
        var only = Ready("Plus", ("7天", 10));
        var combined = AccountPool.Combine(ProviderId.Codex, new[] { new PoolMember("Plus", only) });
        Check.True(ReferenceEquals(only, combined), "same snapshot instance");
        Check.Null(AccountPool.Summarize(combined), "no pool summary for one account");
    }

    private static void MembersAreLabelled()
    {
        var combined = AccountPool.Combine(ProviderId.Codex, new[]
        {
            new PoolMember("Business", Ready("Business", ("5小时", 20), ("7天", 30))),
            new PoolMember("Business", Ready("Business", ("7天", 50))),
        });

        Check.Equal(ProviderStatus.Ready, combined.Status, "status");
        Check.Equal("2 个号", combined.Plan, "plan chip counts accounts");
        Check.Equal(3, combined.Windows.Count, "all windows kept");
        Check.Equal("5小时", combined.Windows[0].Name, "window name kept");
        Check.Equal("Business", combined.Windows[0].Account, "account recorded");
        Check.Equal("Business #2", combined.Windows[2].Account, "duplicate labels made unique");
        Check.Equal(2, combined.PoolAccounts.Count, "pool size");
    }

    private static void PartialFailure()
    {
        var combined = AccountPool.Combine(ProviderId.Codex, new[]
        {
            new PoolMember("A", Ready("A", ("7天", 30))),
            new PoolMember("B", Failed(ProviderStatus.AuthenticationRequired, "令牌已过期")),
        });

        Check.Equal(ProviderStatus.Ready, combined.Status, "one working account keeps the card ready");
        Check.Contains(combined.Message!, "B：令牌已过期", "failed member is named");
        var summary = AccountPool.Summarize(combined)!;
        Check.Equal(1, summary.Available, "only the working account is available");
        Check.Equal(2, summary.Total, "failed account still counts toward the pool");
    }

    private static void NoReadyMember()
    {
        var auth = AccountPool.Combine(ProviderId.Codex, new[]
        {
            new PoolMember("A", Failed(ProviderStatus.Unavailable, "网络")),
            new PoolMember("B", Failed(ProviderStatus.AuthenticationRequired, "过期")),
        });
        Check.Equal(ProviderStatus.AuthenticationRequired, auth.Status, "a login problem is the actionable one");

        var down = AccountPool.Combine(ProviderId.Codex, new[]
        {
            new PoolMember("A", Failed(ProviderStatus.Unavailable, "网络")),
            new PoolMember("B", Failed(ProviderStatus.NotConfigured, "未配置")),
        });
        Check.Equal(ProviderStatus.Unavailable, down.Status, "mixed failures are unavailable");
    }

    private static void SummaryPicksBestAccount()
    {
        var combined = AccountPool.Combine(ProviderId.Kimi, new[]
        {
            new PoolMember("KIMI_API_KEY", Ready("-", ("5小时", 0), ("7天", 100))),
            new PoolMember("KIMI_API_KEY_SEC", Ready("-", ("5小时", 10), ("7天", 36))),
        });

        var summary = AccountPool.Summarize(combined)!;
        Check.Equal("KIMI_API_KEY_SEC", summary.BestAccount, "the account that is not exhausted");
        Check.Equal("7天", summary.Binding.Name, "its tightest window");
        Check.Close(36, summary.Binding.UsedPercent, "binding usage");
        Check.Equal(1, summary.Available, "one exhausted, one available");
    }

    private static void SiblingsJoinPool()
    {
        var directory = TempWorkspace.NewDirectory("pool-siblings");
        var envFile = TempWorkspace.WriteFile(directory, ".env", string.Join("\n",
            "KIMI_API_KEY=sk-kimi-first-0000000000",
            "KIMI_API_KEY_SEC=sk-kimi-second-000000000",
            "KIMI_API_KEY_BASE_URL=https://api.kimi.com/coding",
            "KIMI_API_KEY_NOTE=short",
            "KIMI_API_KEYS=sk-kimi-not-a-sibling-000",
            "OTHER_KEY=sk-other-00000000000000"));

        var loads = ApiKeyResolver.ResolveAll(
            ProviderId.Kimi,
            new AppSettings { ApiKeyEnvFile = envFile },
            _ => null);

        Check.Equal(2, loads.Count, "two accounts");
        Check.Equal("KIMI_API_KEY", loads[0].Variable, "base name first");
        Check.Equal("KIMI_API_KEY_SEC", loads[1].Variable, "suffixed sibling");
    }

    private static void DuplicateValuesCollapse()
    {
        var loads = ApiKeyResolver.ResolveAll(
            ProviderId.Kimi,
            new AppSettings(),
            name => name is "KIMI_CODE_API_KEY" or "KIMI_API_KEY" ? "sk-kimi-same-value-0000" : null);

        Check.Equal(1, loads.Count, "one account");
        Check.Equal("KIMI_CODE_API_KEY", loads[0].Variable, "upstream name kept");
    }

    private static void EnvironmentSiblings()
    {
        var env = new Dictionary<string, string>
        {
            ["OPENCODE_API_KEY"] = "oc_sk_first_000000000000",
            ["OPENCODE_API_KEY_2"] = "oc_sk_second_00000000000",
        };
        var loads = ApiKeyResolver.ResolveAll(
            ProviderId.OpenCodeGo,
            new AppSettings(),
            name => env.GetValueOrDefault(name),
            env.Keys);

        Check.Equal(2, loads.Count, "sibling from the environment");
        Check.Equal("OPENCODE_API_KEY_2", loads[1].Variable, "sibling name");
    }

    private static string HermesAuthJson(params (string Label, string Type, string Token)[] entries)
    {
        var items = entries.Select(e =>
            $$"""{ "id": "x", "label": "{{e.Label}}", "auth_type": "{{e.Type}}", "access_token": "{{e.Token}}", "refresh_token": "never-read" }""");
        return $$"""
            {
              "version": 1,
              "providers": { "openai-codex": { "tokens": { "access_token": "ignored" } } },
              "credential_pool": {
                "openai-codex": [ {{string.Join(",", items)}} ],
                "anthropic": [ { "label": "other", "access_token": "not-codex" } ]
              }
            }
            """;
    }

    private static void HermesPoolReader()
    {
        var directory = TempWorkspace.NewDirectory("hermes-reader");
        var path = TempWorkspace.WriteFile(directory, "auth.json", HermesAuthJson(
            ("codex-2", "oauth", Jwt("acct_business", 4102444000)),
            ("key", "api_key", "sk-not-oauth")));

        var (outcome, accounts) = HermesCredentialPool.LoadCodex(path);
        Check.Equal(CredentialOutcome.Ok, outcome, "read");
        Check.Equal(1, accounts.Count, "api-key entries are skipped");
        Check.Equal("codex-2", accounts[0].Label, "label");
        Check.Equal("acct_business", accounts[0].Credentials.AccountId, "account id from the JWT");

        var broken = TempWorkspace.WriteFile(directory, "broken.json", "{ half");
        Check.Equal(CredentialOutcome.Malformed, HermesCredentialPool.LoadCodex(broken).Outcome, "half-written file");
    }

    private static async Task KimiPool()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, KimiUsageJson);
        handler.Enqueue(HttpStatusCode.OK, KimiUsageJson);
        var keys = new Dictionary<string, string>
        {
            ["KIMI_API_KEY"] = "sk-kimi-first-0000000000",
            ["KIMI_API_KEY_SEC"] = "sk-kimi-second-000000000",
        };
        var provider = new KimiUsageProvider(new HttpClient(handler), null, name => keys.GetValueOrDefault(name));

        var directory = TempWorkspace.NewDirectory("kimi-pool");
        var envFile = TempWorkspace.WriteFile(directory, ".env", "KIMI_API_KEY_SEC=sk-kimi-second-000000000\n");
        var snapshot = await provider.FetchAsync(new AppSettings { KimiEnabled = true, ApiKeyEnvFile = envFile });

        Check.Equal(2, handler.SendCount, "one request per key");
        var bearers = handler.Requests.Select(r => r.Headers.GetValues("Authorization").Single()).Order().ToArray();
        Check.Equal("Bearer sk-kimi-first-0000000000", bearers[0], "first key");
        Check.Equal("Bearer sk-kimi-second-000000000", bearers[1], "second key");
        Check.Equal(ProviderStatus.Ready, snapshot.Status, "ready");
        Check.Equal(4, snapshot.Windows.Count, "two windows per account");
        Check.Equal("KIMI_API_KEY_SEC", snapshot.PoolAccounts[1], "labelled by variable");
        Check.DoesNotContain(string.Join("|", snapshot.Windows.Select(w => w.Name)), "sk-kimi", "keys never shown");
    }

    private static async Task CodexPool()
    {
        var directory = TempWorkspace.NewDirectory("codex-pool");
        var cli = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var hermes = TempWorkspace.WriteFile(directory, "hermes-auth.json", HermesAuthJson(
            ("codex-2", "oauth", Jwt("acct_explicit_123", 4102444000)),
            ("device_code", "oauth", Jwt("acct_free", 4102444000))));
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixtures.CodexUsageJson);
        handler.Enqueue(HttpStatusCode.OK, Fixtures.CodexUsageJson);
        var provider = new CodexUsageProvider(new HttpClient(handler));

        var snapshot = await provider.FetchAsync(new AppSettings { CodexAuthPath = cli, HermesAuthPath = hermes });

        Check.Equal(2, handler.SendCount, "the CLI account and its pool copy are one account");
        var accounts = handler.Requests
            .Select(r => r.Headers.GetValues("ChatGPT-Account-Id").Single())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check.Equal("acct_explicit_123", accounts[0], "business account");
        Check.Equal("acct_free", accounts[1], "second account");
        Check.Equal(ProviderStatus.Ready, snapshot.Status, "ready");
        Check.Equal(2, snapshot.PoolAccounts.Count, "two accounts in the pool");
    }

    private static async Task CodexExpiredPoolEntry()
    {
        var directory = TempWorkspace.NewDirectory("codex-pool-expired");
        var cli = TempWorkspace.WriteFile(directory, "auth.json", Fixtures.CodexAuthJson);
        var hermes = TempWorkspace.WriteFile(directory, "hermes-auth.json", HermesAuthJson(
            ("device_code", "oauth", Jwt("acct_free", 1_700_000_000))));
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixtures.CodexUsageJson);
        var provider = new CodexUsageProvider(new HttpClient(handler));

        var snapshot = await provider.FetchAsync(new AppSettings { CodexAuthPath = cli, HermesAuthPath = hermes });

        Check.Equal(1, handler.SendCount, "no request with an expired token");
        Check.Equal(ProviderStatus.Ready, snapshot.Status, "working account keeps the card ready");
        Check.Contains(snapshot.Message!, "Hermes", "expired pool entry is explained");
    }

    private static void BusinessPlanMapped() =>
        Check.Equal("Business Pro Lite", CodexUsageParser.MapPlan("self_serve_business_prolite"), "plan label");

    private static void HermesPathRoundTrip()
    {
        var directory = TempWorkspace.NewDirectory("settings-hermes");
        var store = new SettingsStore(directory);
        store.Save(new AppSettings { HermesAuthPath = @"  C:\AI\Hermes\auth.json " });
        Check.Equal(@"C:\AI\Hermes\auth.json", store.Load().HermesAuthPath, "trimmed and kept");
    }
}
