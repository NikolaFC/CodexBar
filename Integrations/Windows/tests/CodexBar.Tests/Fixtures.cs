namespace CodexBar.Tests;

/// <summary>
/// Payload shapes copied from the upstream provider schemas. Every value below is obviously
/// fabricated; no fixture in this project reads a real credential store.
/// </summary>
internal static class Fixtures
{
    /// <summary>
    /// Fabricated JWT whose payload is <c>{"exp":4102444800,"chatgpt_account_id":"acct_from_jwt"}</c>,
    /// so the expiry hint resolves far in the future and the account id is recoverable.
    /// </summary>
    public const string FakeCodexAccessToken =
        "header.eyJleHAiOjQxMDI0NDQ4MDAsImNoYXRncHRfYWNjb3VudF9pZCI6ImFjY3RfZnJvbV9qd3QifQ.signature";

    public const string FakeClaudeAccessToken = "sk-ant-oat01-fake-token-value";

    /// <summary>Codex <c>auth.json</c> with an OAuth token pair.</summary>
    public const string CodexAuthJson = """
    {
      "OPENAI_API_KEY": null,
      "tokens": {
        "access_token": "header.eyJleHAiOjQxMDI0NDQ4MDAsImNoYXRncHRfYWNjb3VudF9pZCI6ImFjY3RfZnJvbV9qd3QifQ.signature",
        "refresh_token": "rt-fake-refresh-value",
        "account_id": "acct_explicit_123",
        "id_token": "id.eyJlbWFpbCI6ImZha2VAZXhhbXBsZS5jb20ifQ.sig"
      },
      "last_refresh": "2026-10-14T08:00:00Z"
    }
    """;

    /// <summary>Codex <c>auth.json</c> in API-key mode, with no OAuth token pair.</summary>
    public const string CodexApiKeyOnlyAuthJson = """
    {
      "OPENAI_API_KEY": "sk-fake-api-key-not-a-login",
      "last_refresh": "2026-10-14T08:00:00Z"
    }
    """;

    /// <summary>Codex <c>auth.json</c> whose tokens object exists but has no access token.</summary>
    public const string CodexTokensWithoutAccessTokenJson = """
    {
      "OPENAI_API_KEY": "sk-fake-api-key-not-a-login",
      "tokens": {
        "refresh_token": "rt-fake-refresh-value"
      }
    }
    """;

    public const string ClaudeCredentialsJson = """
    {
      "claudeAiOauth": {
        "accessToken": "sk-ant-oat01-fake-token-value",
        "refreshToken": "sk-ant-ort01-fake-refresh-value",
        "expiresAt": 4102444800000,
        "scopes": ["user:profile", "user:inference"],
        "subscriptionType": "max",
        "rateLimitTier": "default_claude_max_20x"
      }
    }
    """;

    public const string ClaudeExpiredCredentialsJson = """
    {
      "claudeAiOauth": {
        "accessToken": "sk-ant-oat01-fake-token-value",
        "refreshToken": "sk-ant-ort01-fake-refresh-value",
        "expiresAt": 1700000000000,
        "scopes": ["user:inference"],
        "subscriptionType": "pro"
      }
    }
    """;

    public const string ClaudeMcpOnlyCredentialsJson = """
    {
      "mcpOAuth": {
        "server": "fake"
      }
    }
    """;

    /// <summary>Codex <c>wham/usage</c> response with both lanes plus model sub-limits.</summary>
    public const string CodexUsageJson = """
    {
      "account_id": "acct_explicit_123",
      "plan_type": "pro",
      "rate_limit": {
        "primary_window": { "used_percent": 28, "reset_at": 1765000000, "limit_window_seconds": 18000 },
        "secondary_window": { "used_percent": 59, "reset_at": 1765600000, "limit_window_seconds": 604800 }
      },
      "credits": { "remaining": 112.4 },
      "additional_rate_limits": [
        {
          "limit_name": "GPT-5.3-Codex-Spark",
          "metered_feature": "codex-spark",
          "rate_limit": {
            "primary_window": { "used_percent": 4, "reset_at": 1765000000, "limit_window_seconds": 18000 },
            "secondary_window": { "used_percent": 12, "reset_at": 1765600000, "limit_window_seconds": 604800 }
          }
        },
        {
          "limit_name": "Codex Mini",
          "metered_feature": "codex-mini",
          "rate_limit": {
            "primary_window": { "used_percent": 71, "reset_at": 1765000200, "limit_window_seconds": 604800 }
          }
        }
      ]
    }
    """;

    /// <summary>
    /// A plan that reports only a weekly window, in the primary slot. The label must follow the
    /// reported cadence instead of assuming a 5-hour session.
    /// </summary>
    public const string CodexWeeklyOnlyInPrimaryJson = """
    {
      "plan_type": "free",
      "rate_limit": {
        "primary_window": { "used_percent": 17, "reset_at": 1765600000, "limit_window_seconds": 604800 }
      }
    }
    """;

    /// <summary>Codex response with no usable window at all.</summary>
    public const string CodexNoWindowsJson = """
    {
      "plan_type": "plus",
      "rate_limit": {
        "primary_window": { "used_percent": null, "reset_at": 0 },
        "secondary_window": { "used_percent": "not-a-number" }
      }
    }
    """;

    /// <summary>Claude OAuth usage response with a scoped weekly limit and a routines lane.</summary>
    public const string ClaudeUsageJson = """
    {
      "five_hour": { "utilization": 12.5, "resets_at": "2026-10-15T12:00:00Z" },
      "seven_day": { "utilization": 40, "resets_at": "2026-10-18T00:00:00Z" },
      "seven_day_sonnet": { "utilization": 55, "resets_at": "2026-10-18T00:00:00Z" },
      "seven_day_opus": null,
      "limits": [
        {
          "kind": "weekly_scoped",
          "group": "weekly",
          "percent": 33,
          "resets_at": "2026-10-18T00:00:00Z",
          "scope": { "model": { "id": "claude-fable-4", "display_name": "Fable" } }
        },
        {
          "kind": "weekly_scoped",
          "group": "weekly",
          "percent": 9,
          "resets_at": "2026-10-18T00:00:00Z",
          "scope": { "model": { "id": "all-models", "display_name": "All models" } }
        },
        {
          "kind": "daily",
          "group": "daily",
          "percent": 1
        }
      ],
      "seven_day_routines": { "utilization": 5, "resets_at": "2026-10-16T00:00:00Z" },
      "extra_usage": { "is_enabled": false }
    }
    """;

    /// <summary>Claude response without model-scoped lanes: nothing may be invented as 0%.</summary>
    public const string ClaudeNoModelLanesJson = """
    {
      "five_hour": { "utilization": 8, "resets_at": "2026-10-15T12:00:00Z" },
      "seven_day": { "utilization": 21, "resets_at": "2026-10-18T00:00:00Z" }
    }
    """;

    public const string ClaudeNoWindowsJson = """
    {
      "five_hour": { "utilization": null, "resets_at": null },
      "seven_day": { "utilization": "n/a" }
    }
    """;
}
