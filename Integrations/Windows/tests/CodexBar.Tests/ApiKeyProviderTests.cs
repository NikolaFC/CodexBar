using System.Net;
using System.Text.Json;
using CodexBar.Core;

namespace CodexBar.Tests;

internal static class ApiKeyProviderTests
{
    private const string FakeKimiKey = "sk-kimi-test-not-a-real-key-0001";
    private const string FakeOpenCodeKey = "oc_sk_test_not_a_real_key_0002";

    // Shapes captured from the live endpoints (values replaced).
    private const string KimiUsageJson = """
        {
          "usage": { "limit": "100", "used": "100", "resetTime": "2026-11-07T09:51:39.293472Z" },
          "limits": [
            {
              "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
              "detail": { "limit": "100", "remaining": "100", "resetTime": "2026-11-07T08:51:39.293472Z" }
            }
          ],
          "usages": {
            "limit_5h": { "used_ratio": 0, "reset_time": "2026-11-07T08:51:38Z" },
            "limit_7d": { "used_ratio": 1, "reset_time": "2026-11-07T09:51:38Z" }
          }
        }
        """;

    private const string OpenCodeGoUsageJson = """
        {
          "usage": {
            "rolling": { "status": "ok", "percent": 0, "resetsAt": "2026-11-07T08:25:27.000Z" },
            "weekly": { "status": "ok", "percent": 4, "resetsAt": "2026-11-09T00:00:00.000Z" },
            "monthly": { "status": "ok", "percent": 4, "resetsAt": "2026-11-27T22:38:46.000Z" }
          }
        }
        """;

    public static void Register()
    {
        TestHarness.Add("kimi: parses the live payload shape", KimiParsesLiveShape);
        TestHarness.Add("kimi: a counter wins only when it reports more for the same period", KimiCounterOverridesRatio);
        TestHarness.Add("kimi: counters alone still produce windows", KimiCountersOnly);
        TestHarness.Add("kimi: unusable counters are skipped, plan and month pool mapped", KimiSkipsBadCounters);
        TestHarness.Add("opencode go: parses the live payload shape as direct percentages", OpenCodeParsesLiveShape);
        TestHarness.Add("opencode go: rolling window is required", OpenCodeRequiresRolling);
        TestHarness.Add("opencode go: resetInSec, used/limit and epoch fallbacks", OpenCodeFallbacks);
        TestHarness.Add("api keys: environment beats the env file, upstream name beats alias", KeysEnvironmentFirst);
        TestHarness.Add("api keys: env file parsing keeps only wanted names", KeysEnvFileParsing);
        TestHarness.Add("api keys: missing and non-local env files", KeysEnvFileProblems);
        TestHarness.Add("api keys: fingerprint tracks the key without containing it", KeysFingerprint);
        TestHarness.AddAsync("kimi: disabled provider makes no request", KimiDisabled);
        TestHarness.AddAsync("kimi: missing key is NotConfigured without a request", KimiMissingKey);
        TestHarness.AddAsync("kimi: sends the bearer key to the fixed endpoint", KimiSendsBearer);
        TestHarness.AddAsync("opencode go: 401 asks for a valid key and never echoes it", OpenCodeUnauthorized);
        TestHarness.AddAsync("opencode go: key from the env file is used", OpenCodeFromEnvFile);
        TestHarness.Add("settings: api-key provider fields round-trip and default off", SettingsRoundTrip);
    }

    private static ApiKeyLoad ResolveFirst(ProviderId id, AppSettings settings, Func<string, string?> env) =>
        ApiKeyResolver.ResolveAll(id, settings, env)[0];

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static void KimiParsesLiveShape()
    {
        var parsed = KimiUsageParser.Parse(KimiUsageJson);

        Check.Equal(2, parsed.Windows.Count, "window count");
        Check.Equal("5小时", parsed.Windows[0].Name, "session label");
        Check.Close(0, parsed.Windows[0].UsedPercent, "session usage");
        Check.Equal(TimeSpan.FromHours(5), parsed.Windows[0].Duration, "session duration");
        Check.Equal("7天", parsed.Windows[1].Name, "weekly label");
        Check.Close(100, parsed.Windows[1].UsedPercent, "weekly usage");
        Check.Equal(
            new DateTimeOffset(2026, 11, 7, 9, 51, 38, TimeSpan.Zero),
            parsed.Windows[1].ResetsAt,
            "weekly reset from the ratio pool");
        Check.Null(parsed.Plan, "no membership in payload");
    }

    private static void KimiCounterOverridesRatio()
    {
        var parsed = KimiUsageParser.Parse("""
            {
              "usage": { "limit": "200", "used": "150", "resetTime": "2026-11-10T00:00:01Z" },
              "usages": { "limit_7d": { "used_ratio": 0.5, "reset_time": "2026-11-10T00:00:00Z" } }
            }
            """);
        Check.Close(75, parsed.Windows.Single().UsedPercent, "higher counter for the same reset wins");

        var distinct = KimiUsageParser.Parse("""
            {
              "usage": { "limit": "200", "used": "150", "resetTime": "2026-11-12T00:00:00Z" },
              "usages": { "limit_7d": { "used_ratio": 0.5, "reset_time": "2026-11-10T00:00:00Z" } }
            }
            """);
        Check.Close(50, distinct.Windows.Single().UsedPercent, "a counter on another clock does not override");
    }

    private static void KimiCountersOnly()
    {
        var parsed = KimiUsageParser.Parse("""
            {
              "usage": { "limit": 1000, "remaining": 250 },
              "limits": [ { "window": { "duration": 5, "timeUnit": "TIME_UNIT_HOUR" },
                            "detail": { "limit": "50", "used": "10" } } ]
            }
            """);

        Check.Equal(2, parsed.Windows.Count, "window count");
        Check.Equal("5小时", parsed.Windows[0].Name, "hour-unit window still labelled 5小时");
        Check.Close(20, parsed.Windows[0].UsedPercent, "session from used/limit");
        Check.Close(75, parsed.Windows[1].UsedPercent, "weekly from remaining");
    }

    private static void KimiSkipsBadCounters()
    {
        var parsed = KimiUsageParser.Parse("""
            {
              "usage": { "limit": "100", "remaining": "500" },
              "limits": [ { "window": { "duration": 1, "timeUnit": "TIME_UNIT_DAY" },
                            "detail": { "limit": "0", "used": "3" } } ],
              "usages": { "limit_month_total": { "used_ratio": 0.3, "reset_time": "2026-12-01T00:00:00Z" } },
              "user": { "membership": { "level": "LEVEL_INTERMEDIATE" } }
            }
            """);

        Check.Equal(1, parsed.Windows.Count, "only the month pool survives");
        Check.Equal("月度总额", parsed.Windows[0].Name, "month label");
        Check.Close(30, parsed.Windows[0].UsedPercent, "month usage");
        Check.Equal("Allegretto", parsed.Plan, "membership level mapped");
    }

    private static void OpenCodeParsesLiveShape()
    {
        var parsed = OpenCodeGoUsageParser.Parse(OpenCodeGoUsageJson);

        Check.Equal(3, parsed.Windows.Count, "window count");
        Check.Equal("5小时", parsed.Windows[0].Name, "rolling label");
        Check.Close(0, parsed.Windows[0].UsedPercent, "rolling usage");
        Check.Equal("每周", parsed.Windows[1].Name, "weekly label");
        Check.Close(4, parsed.Windows[1].UsedPercent, "4 means 4%, not a fraction");
        Check.Equal("每月", parsed.Windows[2].Name, "monthly label");
        Check.Equal(
            new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero),
            parsed.Windows[1].ResetsAt,
            "weekly reset");
    }

    private static void OpenCodeRequiresRolling()
    {
        var threw = false;
        try
        {
            OpenCodeGoUsageParser.Parse("""{ "usage": { "weekly": { "percent": 4 } } }""");
        }
        catch (JsonException)
        {
            threw = true;
        }

        Check.True(threw, "missing rolling window is a parse failure");
    }

    private static void OpenCodeFallbacks()
    {
        var now = new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);
        var parsed = OpenCodeGoUsageParser.Parse("""
            {
              "usage": {
                "rolling": { "used": 3, "limit": 12, "resetInSec": 3600 },
                "weekly": { "usagePercent": 250, "resetAt": 1794700800000 },
                "monthly": { "status": "ok" }
              }
            }
            """, now);

        Check.Equal(2, parsed.Windows.Count, "monthly without a value is skipped");
        Check.Close(25, parsed.Windows[0].UsedPercent, "used/limit fallback");
        Check.Equal(now.AddHours(1), parsed.Windows[0].ResetsAt, "relative reset");
        Check.Close(100, parsed.Windows[1].UsedPercent, "clamped");
        Check.Equal(DateTimeOffset.FromUnixTimeSeconds(1794700800), parsed.Windows[1].ResetsAt, "epoch milliseconds");
    }

    private static void KeysEnvironmentFirst()
    {
        var directory = TempWorkspace.NewDirectory("keys-env-first");
        var envFile = TempWorkspace.WriteFile(directory, ".env", "KIMI_CODE_API_KEY=from-file\n");
        var settings = new AppSettings { ApiKeyEnvFile = envFile };

        var load = ResolveFirst(
            ProviderId.Kimi,
            settings,
            Env(("KIMI_API_KEY", "alias-value"), ("KIMI_CODE_API_KEY", "upstream-value")));
        Check.Equal(ApiKeyOutcome.Ok, load.Outcome, "found");
        Check.Equal("KIMI_CODE_API_KEY", load.Variable, "upstream name first");
        Check.Equal("环境变量", load.Source, "from the environment");

        var fileLoad = ResolveFirst(ProviderId.Kimi, settings, Env());
        Check.Equal(envFile, fileLoad.Source, "falls back to the env file");
        Check.Equal("KIMI_CODE_API_KEY", fileLoad.Variable, "variable from file");
    }

    private static void KeysEnvFileParsing()
    {
        var directory = TempWorkspace.NewDirectory("keys-parse");
        var envFile = TempWorkspace.WriteFile(directory, ".env", string.Join("\n",
            "﻿# comment",
            "OTHER_SECRET=should-not-be-read",
            "OPENCODE_GO_API_KEY=first",
            "export OPENCODE_GO_API_KEY=\"second value\"",
            "  OPENCODE_API_KEY = ''",
            "BROKEN LINE",
            "\r"));
        var settings = new AppSettings { ApiKeyEnvFile = envFile };

        var load = ResolveFirst(ProviderId.OpenCodeGo, settings, Env());
        Check.Equal(ApiKeyOutcome.Ok, load.Outcome, "found");
        Check.Equal("OPENCODE_GO_API_KEY", load.Variable, "empty upstream name falls through to alias");

        var parsed = ApiKeyResolver.ParseDotEnv(
            File.ReadAllText(envFile),
            ApiKeyResolver.VariableNames(ProviderId.OpenCodeGo).ToArray());
        Check.Equal("second value", parsed["OPENCODE_GO_API_KEY"], "later assignment wins, quotes stripped");
        Check.False(parsed.ContainsKey("OTHER_SECRET"), "unrelated names are not kept");
        Check.False(parsed.ContainsKey("OPENCODE_API_KEY"), "empty value is ignored");

        var inline = ApiKeyResolver.ParseDotEnv("KIMI_API_KEY=abc # note\n", new[] { "KIMI_API_KEY" });
        Check.Equal("abc", inline["KIMI_API_KEY"], "inline comment stripped");
    }

    private static void KeysEnvFileProblems()
    {
        var none = ResolveFirst(ProviderId.Kimi, new AppSettings(), Env());
        Check.Equal(ApiKeyOutcome.Missing, none.Outcome, "no env, no file");
        Check.Null(none.Source, "no source");

        var directory = TempWorkspace.NewDirectory("keys-missing");
        var missing = ResolveFirst(
            ProviderId.Kimi,
            new AppSettings { ApiKeyEnvFile = Path.Combine(directory, "absent.env") },
            Env());
        Check.Equal(ApiKeyOutcome.Missing, missing.Outcome, "absent file");

        var unc = ResolveFirst(
            ProviderId.Kimi,
            new AppSettings { ApiKeyEnvFile = @"\\attacker\share\.env" },
            Env());
        Check.Equal(ApiKeyOutcome.EnvFileNotLocal, unc.Outcome, "UNC path refused");
    }

    private static void KeysFingerprint()
    {
        var settings = new AppSettings();
        var first = ResolveFirst(ProviderId.Kimi, settings, Env(("KIMI_CODE_API_KEY", FakeKimiKey)));
        var again = ResolveFirst(ProviderId.Kimi, settings, Env(("KIMI_CODE_API_KEY", FakeKimiKey)));
        var other = ResolveFirst(ProviderId.Kimi, settings, Env(("KIMI_CODE_API_KEY", FakeKimiKey + "x")));

        Check.Equal(first.Fingerprint, again.Fingerprint, "stable for the same key");
        Check.True(first.Fingerprint != other.Fingerprint, "changes with the key");
        Check.DoesNotContain(first.Fingerprint, FakeKimiKey, "never contains the key");
        Check.DoesNotContain(first.ToString(), FakeKimiKey, "ToString never contains the key");
    }

    private static (KimiUsageProvider Provider, FakeHttpHandler Handler) Kimi(Func<string, string?> env)
    {
        var handler = new FakeHttpHandler();
        return (new KimiUsageProvider(new HttpClient(handler), null, env), handler);
    }

    private static (OpenCodeGoUsageProvider Provider, FakeHttpHandler Handler) OpenCode(Func<string, string?> env)
    {
        var handler = new FakeHttpHandler();
        return (new OpenCodeGoUsageProvider(new HttpClient(handler), null, env), handler);
    }

    private static async Task KimiDisabled()
    {
        var (provider, handler) = Kimi(Env(("KIMI_CODE_API_KEY", FakeKimiKey)));

        var snapshot = await provider.FetchAsync(new AppSettings());

        Check.Equal(ProviderStatus.Disabled, snapshot.Status, "opt-in provider is off by default");
        Check.Equal(0, handler.SendCount, "no request");
    }

    private static async Task KimiMissingKey()
    {
        var (provider, handler) = Kimi(Env());

        var snapshot = await provider.FetchAsync(new AppSettings { KimiEnabled = true });

        Check.Equal(ProviderStatus.NotConfigured, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "KIMI_CODE_API_KEY", "names the variable to set");
        Check.Equal(0, handler.SendCount, "no request without a key");
    }

    private static async Task KimiSendsBearer()
    {
        var (provider, handler) = Kimi(Env(("KIMI_API_KEY", FakeKimiKey)));
        handler.Enqueue(HttpStatusCode.OK, KimiUsageJson);

        var snapshot = await provider.FetchAsync(new AppSettings { KimiEnabled = true });

        Check.Equal(1, handler.SendCount, "one request");
        var request = handler.Requests[0];
        Check.Equal(KimiUsageParser.UsageEndpoint, request.RequestUri!.AbsoluteUri, "fixed endpoint");
        Check.Equal($"Bearer {FakeKimiKey}", request.Headers.GetValues("Authorization").Single(), "bearer key");
        Check.Equal(ProviderStatus.Ready, snapshot.Status, "ready");
        Check.Equal(ProviderId.Kimi, snapshot.Provider, "provider id");
        Check.Contains(snapshot.Source, "KIMI_API_KEY", "source names the variable");
        Check.DoesNotContain(snapshot.Source, FakeKimiKey, "source never shows the key");
    }

    private static async Task OpenCodeUnauthorized()
    {
        var (provider, handler) = OpenCode(Env(("OPENCODE_API_KEY", FakeOpenCodeKey)));
        handler.Enqueue(HttpStatusCode.Unauthorized, $$"""{ "error": "bad key {{FakeOpenCodeKey}}" }""");

        var snapshot = await provider.FetchAsync(new AppSettings { OpenCodeGoEnabled = true });

        Check.Equal(ProviderStatus.AuthenticationRequired, snapshot.Status, "status");
        Check.Contains(snapshot.Message!, "API Key", "actionable");
        Check.DoesNotContain(snapshot.Message!, FakeOpenCodeKey, "body is never echoed");
        Check.DoesNotContain(snapshot.Detail ?? string.Empty, FakeOpenCodeKey, "detail never echoes");
    }

    private static async Task OpenCodeFromEnvFile()
    {
        var directory = TempWorkspace.NewDirectory("opencode-env-file");
        var envFile = TempWorkspace.WriteFile(directory, ".env", $"OPENCODE_GO_API_KEY={FakeOpenCodeKey}\n");
        var (provider, handler) = OpenCode(Env());
        handler.Enqueue(HttpStatusCode.OK, OpenCodeGoUsageJson);

        var snapshot = await provider.FetchAsync(
            new AppSettings { OpenCodeGoEnabled = true, ApiKeyEnvFile = envFile });

        Check.Equal(ProviderStatus.Ready, snapshot.Status, "ready");
        Check.Equal(OpenCodeGoUsageParser.UsageEndpoint, handler.Requests[0].RequestUri!.AbsoluteUri, "fixed endpoint");
        Check.Equal(
            $"Bearer {FakeOpenCodeKey}",
            handler.Requests[0].Headers.GetValues("Authorization").Single(),
            "key from file");
        Check.Equal(3, snapshot.Windows.Count, "windows");
    }

    private static void SettingsRoundTrip()
    {
        var directory = TempWorkspace.NewDirectory("settings-api-keys");
        var store = new SettingsStore(directory);

        var defaults = store.Load();
        Check.False(defaults.KimiEnabled, "kimi off by default");
        Check.False(defaults.OpenCodeGoEnabled, "opencode off by default");

        store.Save(new AppSettings
        {
            KimiEnabled = true,
            OpenCodeGoEnabled = true,
            ApiKeyEnvFile = @"  C:\keys\.env  ",
        });
        var loaded = store.Load();
        Check.True(loaded.KimiEnabled, "kimi enabled");
        Check.True(loaded.OpenCodeGoEnabled, "opencode enabled");
        Check.Equal(@"C:\keys\.env", loaded.ApiKeyEnvFile, "env file trimmed");

        var legacy = Path.Combine(directory, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, SettingsStore.FileName), """{ "RefreshSeconds": 300, "CodexEnabled": true }""");
        var old = new SettingsStore(legacy).Load();
        Check.False(old.KimiEnabled, "older settings files keep the new providers off");
        Check.Null(old.ApiKeyEnvFile, "no env file");
    }
}
