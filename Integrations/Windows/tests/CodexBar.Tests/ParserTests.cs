using System.Text.Json;
using CodexBar.Core;

namespace CodexBar.Tests;

internal static class ParserTests
{
    public static void Register()
    {
        TestHarness.Add("codex: parses both lanes and model sub-limits", CodexParsesBothLanes);
        TestHarness.Add("codex: labels follow the reported cadence, not the slot", CodexLabelsFollowCadence);
        TestHarness.Add("codex: clamps out-of-range percentages", CodexClampsPercentages);
        TestHarness.Add("codex: skips non-numeric and missing windows", CodexSkipsUnusableWindows);
        TestHarness.Add("codex: absurd limit_window_seconds does not overflow", CodexHugeWindowSeconds);
        TestHarness.Add("codex: maps plan labels", CodexMapsPlans);
        TestHarness.Add("codex: resets are UTC", CodexResetIsUtc);
        TestHarness.Add("claude: parses session, weekly, model and routines lanes", ClaudeParsesLanes);
        TestHarness.Add("claude: never invents a missing model lane as 0%", ClaudeDoesNotInventLanes);
        TestHarness.Add("claude: ignores non-weekly and all-models scoped limits", ClaudeIgnoresOtherScopedLimits);
        TestHarness.Add("parsers: invalid JSON is rejected", InvalidJsonRejected);
    }

    private static void CodexParsesBothLanes()
    {
        var parsed = CodexUsageParser.Parse(Fixtures.CodexUsageJson);

        Check.Equal(5, parsed.Windows.Count, "window count");
        Check.Equal("5小时", parsed.Windows[0].Name, "primary label");
        Check.Close(28, parsed.Windows[0].UsedPercent, "primary usage");
        Check.Equal("7天", parsed.Windows[1].Name, "secondary label");
        Check.Close(59, parsed.Windows[1].UsedPercent, "secondary usage");
        Check.Equal("Codex Spark 5-hour", parsed.Windows[2].Name, "spark short label");
        Check.Equal("Codex Spark Weekly", parsed.Windows[3].Name, "spark weekly label");
        Check.Equal("Codex Mini", parsed.Windows[4].Name, "named extra limit label");
        Check.Close(71, parsed.Windows[4].UsedPercent, "named extra limit usage");
        Check.Equal("Pro 20x", parsed.Plan, "plan label");
        Check.Equal("acct_explicit_123", parsed.AccountId, "account id");
    }

    private static void CodexLabelsFollowCadence()
    {
        var parsed = CodexUsageParser.Parse(Fixtures.CodexWeeklyOnlyInPrimaryJson);

        Check.Equal(1, parsed.Windows.Count, "window count");
        Check.Equal("7天", parsed.Windows[0].Name, "weekly window must not be labelled as a session");
        Check.Close(17, parsed.Windows[0].UsedPercent, "usage");
    }

    private static void CodexClampsPercentages()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window": { "used_percent": 150, "reset_at": 1765000000 },
            "secondary_window": { "used_percent": -12, "reset_at": 1765600000 }
          }
        }
        """;

        var parsed = CodexUsageParser.Parse(json);

        Check.Close(100, parsed.Windows[0].UsedPercent, "over-100 clamps to 100");
        Check.Close(0, parsed.Windows[1].UsedPercent, "negative clamps to 0");
        Check.Close(0, parsed.Windows[0].RemainingPercent, "remaining stays in range");
    }

    private static void CodexSkipsUnusableWindows()
    {
        var parsed = CodexUsageParser.Parse(Fixtures.CodexNoWindowsJson);

        Check.Equal(0, parsed.Windows.Count, "no windows may be fabricated");
        Check.Equal("Plus", parsed.Plan, "plan is still reported");
    }

    private static void CodexHugeWindowSeconds()
    {
        const string json = """
        {
          "rate_limit": {
            "primary_window": { "used_percent": 5, "reset_at": 1765000000, "limit_window_seconds": 999999999999 },
            "secondary_window": { "used_percent": 6, "reset_at": 1765600000, "limit_window_seconds": -4 }
          }
        }
        """;

        var parsed = CodexUsageParser.Parse(json);

        Check.Equal(2, parsed.Windows.Count, "windows survive an absurd cadence");
        Check.Null(parsed.Windows[0].Duration, "an unrealistic cadence is treated as absent");
        Check.Null(parsed.Windows[1].Duration, "a non-positive cadence is treated as absent");
        Check.Equal(CodexUsageParser.PrimaryWindowName, parsed.Windows[0].Name, "fallback label");
    }

    private static void CodexMapsPlans()
    {
        Check.Equal("Pro 20x", CodexUsageParser.MapPlan("pro"), "pro");
        Check.Equal("Pro 5x", CodexUsageParser.MapPlan("pro_lite"), "pro lite");
        Check.Equal("Plus", CodexUsageParser.MapPlan("chatgpt-plus"), "plus alias");
        Check.Equal("Free", CodexUsageParser.MapPlan("free"), "free");
        Check.Null(CodexUsageParser.MapPlan(null), "absent plan");
        Check.Equal("unheard-of", CodexUsageParser.MapPlan("unheard-of"), "unknown plans pass through verbatim");
    }

    private static void CodexResetIsUtc()
    {
        var parsed = CodexUsageParser.Parse(Fixtures.CodexUsageJson);
        var reset = parsed.Windows[0].ResetsAt;

        Check.NotNull(reset, "reset timestamp");
        Check.Equal(TimeSpan.Zero, reset!.Value.Offset, "reset offset is UTC");
        Check.Equal(DateTimeOffset.FromUnixTimeSeconds(1765000000), reset.Value, "reset value");
    }

    private static void ClaudeParsesLanes()
    {
        var parsed = ClaudeUsageParser.Parse(Fixtures.ClaudeUsageJson);

        Check.Equal(5, parsed.Windows.Count, "window count");
        Check.Equal("5小时", parsed.Windows[0].Name, "session lane");
        Check.Close(12.5, parsed.Windows[0].UsedPercent, "session usage");
        Check.Equal("7天", parsed.Windows[1].Name, "weekly lane");
        Check.Close(40, parsed.Windows[1].UsedPercent, "weekly usage");
        Check.Equal("Sonnet", parsed.Windows[2].Name, "sonnet lane");
        Check.Close(55, parsed.Windows[2].UsedPercent, "sonnet usage");
        Check.Equal("Fable only", parsed.Windows[3].Name, "scoped weekly lane");
        Check.Close(33, parsed.Windows[3].UsedPercent, "scoped weekly usage");
        Check.Equal("Daily Routines", parsed.Windows[4].Name, "routines lane");
        Check.Equal(TimeSpan.Zero, parsed.Windows[3].ResetsAt!.Value.Offset, "scoped reset is UTC");
    }

    private static void ClaudeDoesNotInventLanes()
    {
        var parsed = ClaudeUsageParser.Parse(Fixtures.ClaudeNoModelLanesJson);

        Check.Equal(2, parsed.Windows.Count, "only reported lanes are present");
        Check.False(
            parsed.Windows.Any(window => window.Name is "Sonnet" or "Opus"),
            "a missing model lane must not become a 0% window");
    }

    private static void ClaudeIgnoresOtherScopedLimits()
    {
        var parsed = ClaudeUsageParser.Parse(Fixtures.ClaudeUsageJson);

        Check.False(
            parsed.Windows.Any(window => window.Name.Contains("All models", StringComparison.Ordinal)),
            "the catch-all scope is not a limit");

        // The `kind: "daily"` entry carries percent 1; it must not become a window even though the
        // legitimate "Daily Routines" lane has a similar name.
        Check.False(
            parsed.Windows.Any(window => Math.Abs(window.UsedPercent - 1) < 0.0001),
            "a non-weekly limit entry must not be surfaced");
    }

    private static void InvalidJsonRejected()
    {
        Check.ThrowsAsync<JsonException>(
            () => Task.FromResult(CodexUsageParser.Parse("{ not json")),
            "malformed Codex payload").GetAwaiter().GetResult();

        Check.ThrowsAsync<JsonException>(
            () => Task.FromResult(ClaudeUsageParser.Parse("[]")),
            "non-object Claude payload").GetAwaiter().GetResult();
    }
}
