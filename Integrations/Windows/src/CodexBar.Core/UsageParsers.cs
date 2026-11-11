using System.Text.Json;
using CodexBar.Core.Internal;

namespace CodexBar.Core;

/// <summary>Parsed provider usage. Windows that the payload did not report are simply absent.</summary>
public sealed record ProviderUsageParseResult(
    string? Plan,
    string? AccountId,
    IReadOnlyList<UsageWindow> Windows);

/// <summary>One reported window before it is given a display name.</summary>
internal readonly record struct RawWindow(double UsedPercent, DateTimeOffset? ResetsAt, TimeSpan? Duration);

/// <summary>
/// Parser for <c>GET https://chatgpt.com/backend-api/wham/usage</c>. Mirrors the upstream
/// primary/secondary mapping plus the <c>additional_rate_limits</c> model sub-limits.
/// </summary>
public static class CodexUsageParser
{
    public const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
    public const string PrimaryWindowName = "主要窗口";
    public const string SecondaryWindowName = "次要窗口";
    public const string SessionWindowName = "5小时";
    public const string WeeklyWindowName = "7天";

    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeeklyDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// Upper bound for a believable <c>limit_window_seconds</c>. A larger value is treated as a
    /// missing cadence, which also keeps the seconds-to-<see cref="TimeSpan"/> conversion away
    /// from inputs it would otherwise overflow on.
    /// </summary>
    internal const long MaxWindowSeconds = 366L * 24 * 60 * 60;

    public static ProviderUsageParseResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Codex usage payload must be a JSON object.");
        }

        var windows = new List<UsageWindow>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        var rateLimit = JsonUtil.ObjectOrNull(root, "rate_limit", "rateLimit");

        if (ReadWindow(rateLimit, "primary_window", "primaryWindow") is { } primary)
        {
            windows.Add(Build(LabelFor(primary, PrimaryWindowName), primary));
        }

        if (ReadWindow(rateLimit, "secondary_window", "secondaryWindow") is { } secondary)
        {
            windows.Add(Build(LabelFor(secondary, SecondaryWindowName), secondary));
        }

        if (root.TryGetProperty("additional_rate_limits", out var additional) &&
            additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in additional.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                AppendAdditionalLimit(entry, windows, seenIds);
            }
        }

        var plan = JsonUtil.NonEmptyString(root, "plan_type", "planType");
        var accountId = JsonUtil.NonEmptyString(root, "account_id", "accountId");

        return new ProviderUsageParseResult(MapPlan(plan), accountId, windows);
    }

    /// <summary>
    /// Model-specific limits report utilization in the primary window and fall back to the
    /// secondary one. "Spark" entries get the upstream's stable ids and titles.
    /// </summary>
    private static void AppendAdditionalLimit(JsonElement entry, List<UsageWindow> windows, HashSet<string> seenIds)
    {
        var limitName = JsonUtil.NonEmptyString(entry, "limit_name", "limitName");
        var meteredFeature = JsonUtil.NonEmptyString(entry, "metered_feature", "meteredFeature");
        var rateLimit = JsonUtil.ObjectOrNull(entry, "rate_limit", "rateLimit");

        var primary = ReadWindow(rateLimit, "primary_window", "primaryWindow");
        var secondary = ReadWindow(rateLimit, "secondary_window", "secondaryWindow");

        var isSpark = (limitName ?? string.Empty).Contains("spark", StringComparison.OrdinalIgnoreCase) ||
            (meteredFeature ?? string.Empty).Contains("spark", StringComparison.OrdinalIgnoreCase);

        if (isSpark)
        {
            AppendSparkWindow(primary, fallbackToWeekly: false, windows, seenIds);
            AppendSparkWindow(secondary, fallbackToWeekly: true, windows, seenIds);
            return;
        }

        if ((primary ?? secondary) is not { } snapshot)
        {
            return;
        }

        var identity = JsonUtil.FirstNonEmpty(meteredFeature, limitName);
        var slug = JsonUtil.Slug(identity);
        if (string.IsNullOrEmpty(slug) || !seenIds.Add("codex-" + slug))
        {
            return;
        }

        var title = JsonUtil.FirstNonEmpty(limitName, meteredFeature) ?? "Codex extra limit";
        windows.Add(Build(title, snapshot));
    }

    private static void AppendSparkWindow(
        RawWindow? snapshot,
        bool fallbackToWeekly,
        List<UsageWindow> windows,
        HashSet<string> seenIds)
    {
        if (snapshot is not { } window)
        {
            return;
        }

        // A missing cadence falls back to the lane the value came from; a reported cadence wins.
        var minutes = window.Duration?.TotalMinutes ?? 0;
        var isWeekly = minutes >= 6 * 24 * 60 || (minutes <= 0 && fallbackToWeekly);
        var id = isWeekly ? "codex-spark-weekly" : "codex-spark";
        var title = isWeekly ? "Codex Spark Weekly" : "Codex Spark 5-hour";

        if (seenIds.Add(id))
        {
            windows.Add(Build(title, window, isWeekly ? WeeklyDuration : SessionDuration));
        }
    }

    /// <summary>
    /// Plan labels mirror the upstream share table so the tray shows "Pro 20x" rather than the
    /// raw API token. Unknown plan values are shown verbatim instead of being guessed.
    /// </summary>
    public static string? MapPlan(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType))
        {
            return null;
        }

        return planType.Trim().ToLowerInvariant() switch
        {
            "guest" => "Guest",
            "free" => "Free",
            "go" => "Go",
            "plus" or "plus plan" or "chatgpt plus" or "chatgpt-plus" or "chatgpt_plus" => "Plus",
            "pro" or "codex pro" => "Pro 20x",
            "prolite" or "pro_lite" or "pro-lite" or "pro lite" or "codex pro lite" => "Pro 5x",
            "self_serve_business_prolite" => "Business Pro Lite",
            "free_workspace" => "Free Workspace",
            "team" => "Team",
            "business" => "Business",
            "education" => "Education",
            "quorum" => "Quorum",
            "k12" => "K12",
            "enterprise" => "Enterprise",
            "edu" => "Edu",
            var other => other,
        };
    }

    internal static RawWindow? ReadWindow(JsonElement? parent, params string[] names)
    {
        if (parent is not { } container || JsonUtil.ObjectOrNull(container, names) is not { } window)
        {
            return null;
        }

        var used = JsonUtil.ClampPercent(JsonUtil.FiniteNumber(window, "used_percent", "usedPercent"));
        if (used is not { } percent)
        {
            return null;
        }

        var resetAt = JsonUtil.FromUnixSeconds(
            JsonUtil.Integer(window, "reset_at", "resetAt", "resets_at", "resetsAt"));

        TimeSpan? duration = null;
        if (JsonUtil.Integer(window, "limit_window_seconds", "limitWindowSeconds") is { } seconds &&
            seconds > 0 &&
            seconds <= MaxWindowSeconds)
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        return new RawWindow(percent, resetAt, duration);
    }

    /// <summary>
    /// Names a window from its reported cadence. The primary lane is not always a 5-hour session
    /// (Free-style plans can report only a weekly window), so the duration is what decides the
    /// label and <paramref name="fallback"/> is used only when the payload omits a cadence.
    /// </summary>
    internal static string LabelFor(RawWindow window, string fallback) =>
        window.Duration is { } duration ? DurationLabel(duration) : fallback;

    internal static string DurationLabel(TimeSpan duration)
    {
        var minutes = duration.TotalMinutes;

        if (minutes >= 1440 && Math.Abs(minutes % 1440) < 0.5)
        {
            return $"{(long)Math.Round(minutes / 1440)}天";
        }

        if (minutes >= 60 && Math.Abs(minutes % 60) < 0.5)
        {
            return $"{(long)Math.Round(minutes / 60)}小时";
        }

        return $"{(long)Math.Round(minutes)}分钟";
    }

    internal static UsageWindow Build(string name, RawWindow window, TimeSpan? fallbackDuration = null) => new()
    {
        Name = name,
        UsedPercent = window.UsedPercent,
        ResetsAt = window.ResetsAt,
        Duration = window.Duration ?? fallbackDuration,
    };
}

/// <summary>
/// Parser for <c>GET https://api.anthropic.com/api/oauth/usage</c>. Missing model lanes stay
/// missing: a payload without <c>seven_day_sonnet</c> never turns into a 0% Sonnet window.
/// </summary>
public static class ClaudeUsageParser
{
    /// <summary>The plain endpoint. The live client appends the query flag below.</summary>
    public const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>
    /// The endpoint as the upstream client actually calls it. The <c>cedar_ember</c> flag asks the
    /// service for the reset-inventory payload; requests without it are retried only as a fallback.
    /// </summary>
    public const string UsageEndpointWithQuery = UsageEndpoint + "?cedar_ember=1";

    public const string SessionWindowName = "5小时";
    public const string WeeklyWindowName = "7天";

    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeeklyDuration = TimeSpan.FromDays(7);

    public static ProviderUsageParseResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Claude usage payload must be a JSON object.");
        }

        var windows = new List<UsageWindow>();
        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);

        if (ReadWindow(root, "five_hour", "fiveHour") is { } session)
        {
            windows.Add(CodexUsageParser.Build(SessionWindowName, session, SessionDuration));
        }

        if (ReadWindow(root, "seven_day", "sevenDay") is { } weekly)
        {
            windows.Add(CodexUsageParser.Build(WeeklyWindowName, weekly, WeeklyDuration));
        }

        AppendFlatModelWindow(root, "seven_day_sonnet", "seven_day_sonnet", "Sonnet", windows, seenSlugs);
        AppendFlatModelWindow(root, "seven_day_opus", "seven_day_opus", "Opus", windows, seenSlugs);
        AppendScopedWeeklyLimits(root, windows, seenSlugs);

        if (ReadWindow(root, "seven_day_routines", "sevenDayRoutines") is { } routines)
        {
            windows.Add(CodexUsageParser.Build("Daily Routines", routines, WeeklyDuration));
        }

        return new ProviderUsageParseResult(null, null, windows);
    }

    private static void AppendFlatModelWindow(
        JsonElement root,
        string propertyName,
        string slugKey,
        string title,
        List<UsageWindow> windows,
        HashSet<string> seenSlugs)
    {
        if (ReadWindow(root, propertyName) is not { } window)
        {
            return;
        }

        if (JsonUtil.Slug(slugKey) is { Length: > 0 } slug && !seenSlugs.Add(slug))
        {
            return;
        }

        windows.Add(CodexUsageParser.Build(title, window, WeeklyDuration));
    }

    /// <summary>
    /// Maps <c>limits[]</c> entries the way the upstream scoped-limit mapper does: only
    /// <c>group == "weekly"</c> with <c>kind == "weekly_scoped"</c>, skipping the catch-all
    /// "all models" scope, deduplicated by model identity.
    /// </summary>
    private static void AppendScopedWeeklyLimits(
        JsonElement root,
        List<UsageWindow> windows,
        HashSet<string> seenSlugs)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!string.Equals(JsonUtil.NonEmptyString(entry, "group"), "weekly", StringComparison.Ordinal) ||
                !string.Equals(JsonUtil.NonEmptyString(entry, "kind"), "weekly_scoped", StringComparison.Ordinal))
            {
                continue;
            }

            var percent = JsonUtil.ClampPercent(JsonUtil.FiniteNumber(entry, "percent"));
            var scope = JsonUtil.ObjectOrNull(entry, "scope");
            var model = scope is { } scopeValue ? JsonUtil.ObjectOrNull(scopeValue, "model") : null;
            var modelName = model is { } modelValue
                ? JsonUtil.NonEmptyString(modelValue, "display_name", "displayName")
                : null;

            if (percent is not { } used || string.IsNullOrWhiteSpace(modelName))
            {
                continue;
            }

            var modelId = model is { } idValue ? JsonUtil.NonEmptyString(idValue, "id") : null;
            if (IsAllModelsScope(modelId, modelName))
            {
                continue;
            }

            var slug = JsonUtil.Slug(modelId ?? modelName);
            if (string.IsNullOrEmpty(slug) || !seenSlugs.Add(slug))
            {
                continue;
            }

            windows.Add(new UsageWindow
            {
                Name = $"{modelName} only",
                UsedPercent = used,
                ResetsAt = JsonUtil.ParseIso8601(JsonUtil.NonEmptyString(entry, "resets_at", "resetsAt")),
                Duration = WeeklyDuration,
            });
        }
    }

    private static bool IsAllModelsScope(string? modelId, string modelName)
    {
        if (string.Equals(JsonUtil.Slug(modelName), "all-models", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var idSlug = JsonUtil.Slug(modelId);
        return string.Equals(idSlug, "all-models", StringComparison.Ordinal) ||
            idSlug.EndsWith("-all-models", StringComparison.Ordinal);
    }

    internal static RawWindow? ReadWindow(JsonElement? parent, params string[] names)
    {
        if (parent is not { } container || JsonUtil.ObjectOrNull(container, names) is not { } window)
        {
            return null;
        }

        var used = JsonUtil.ClampPercent(JsonUtil.FiniteNumber(window, "utilization", "percent"));
        if (used is not { } percent)
        {
            return null;
        }

        return new RawWindow(percent, JsonUtil.ParseIso8601(JsonUtil.NonEmptyString(window, "resets_at", "resetsAt")), null);
    }

    /// <summary>
    /// Best-effort plan label from the credentials, matching the upstream tier keywords.
    /// </summary>
    public static string? MapPlan(string? subscriptionType, string? rateLimitTier)
    {
        if (!string.IsNullOrWhiteSpace(subscriptionType))
        {
            return subscriptionType.Trim();
        }

        if (string.IsNullOrWhiteSpace(rateLimitTier))
        {
            return null;
        }

        var tier = rateLimitTier.Trim().ToLowerInvariant();
        if (tier.Contains("max", StringComparison.Ordinal))
        {
            return "Max";
        }

        if (tier.Contains("pro", StringComparison.Ordinal))
        {
            return "Pro";
        }

        if (tier.Contains("team", StringComparison.Ordinal))
        {
            return "Team";
        }

        if (tier.Contains("enterprise", StringComparison.Ordinal))
        {
            return "Enterprise";
        }

        return rateLimitTier.Trim();
    }
}

/// <summary>
/// Parser for <c>GET https://api.kimi.com/coding/v1/usages</c> (Kimi Code API key). Mirrors the
/// upstream <c>KimiUsageSnapshot</c>: ratio pools (<c>usages.limit_5h/limit_7d</c>) are preferred,
/// the request counters (<c>usage</c>, <c>limits[]</c>) fill in when a pool is absent or reports
/// less usage for the same period.
/// </summary>
public static class KimiUsageParser
{
    public const string UsageEndpoint = "https://api.kimi.com/coding/v1/usages";
    public const string SessionWindowName = "5小时";
    public const string WeeklyWindowName = "7天";
    public const string MonthlyWindowName = "月度总额";

    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeeklyDuration = TimeSpan.FromDays(7);

    public static ProviderUsageParseResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Kimi usage payload must be a JSON object.");
        }

        var pools = JsonUtil.ObjectOrNull(root, "usages");
        var windows = new List<UsageWindow>();

        TimeSpan? rateDuration = null;
        RawWindow? rateCounts = null;
        if (root.TryGetProperty("limits", out var limits) &&
            limits.ValueKind == JsonValueKind.Array &&
            limits.GetArrayLength() > 0 &&
            limits[0].ValueKind == JsonValueKind.Object)
        {
            var first = limits[0];
            rateDuration = WindowDuration(JsonUtil.ObjectOrNull(first, "window"));
            rateCounts = Counts(JsonUtil.ObjectOrNull(first, "detail"), rateDuration ?? SessionDuration);
        }

        var session = Resolve(
            Ratio(pools, "limit_5h", SessionDuration),
            rateDuration is null || rateDuration == SessionDuration ? rateCounts : null);
        if (session is null && rateCounts is { } other)
        {
            // A counter for a different cadence is still a real limit; keep its own label.
            session = other;
        }

        if (session is { } sessionWindow)
        {
            var name = sessionWindow.Duration is { } d && d != SessionDuration
                ? CodexUsageParser.DurationLabel(d)
                : SessionWindowName;
            windows.Add(CodexUsageParser.Build(name, sessionWindow, SessionDuration));
        }

        var weeklyCounts = Counts(JsonUtil.ObjectOrNull(root, "usage"), WeeklyDuration);
        if (Resolve(Ratio(pools, "limit_7d", WeeklyDuration), weeklyCounts) is { } weekly)
        {
            windows.Add(CodexUsageParser.Build(WeeklyWindowName, weekly, WeeklyDuration));
        }

        if (Ratio(pools, "limit_month_total", null) is { } monthly)
        {
            windows.Add(CodexUsageParser.Build(MonthlyWindowName, monthly));
        }

        return new ProviderUsageParseResult(MapPlan(root), null, windows);
    }

    /// <summary>
    /// Prefers the ratio pool; a counter for the same period wins only when it reports more usage
    /// and both reset clocks agree (the two sources differ by about a second and a half).
    /// </summary>
    private static RawWindow? Resolve(RawWindow? ratio, RawWindow? counts)
    {
        if (ratio is not { } pool)
        {
            return counts;
        }

        if (counts is { } counter &&
            counter.UsedPercent > pool.UsedPercent &&
            counter.ResetsAt is { } counterReset &&
            pool.ResetsAt is { } poolReset &&
            Math.Abs((counterReset - poolReset).TotalSeconds) <= 2)
        {
            return counter;
        }

        return pool;
    }

    private static RawWindow? Ratio(JsonElement? pools, string name, TimeSpan? duration)
    {
        if (pools is not { } container || JsonUtil.ObjectOrNull(container, name) is not { } pool)
        {
            return null;
        }

        if (JsonUtil.FiniteNumber(pool, "used_ratio", "usedRatio") is not { } ratio || ratio < 0)
        {
            return null;
        }

        return new RawWindow(
            Math.Min(1, ratio) * 100,
            JsonUtil.ParseIso8601(JsonUtil.NonEmptyString(pool, "reset_time", "resetTime")),
            duration);
    }

    /// <summary>
    /// Request counters. <c>used</c> is authoritative; <c>remaining</c> must describe a valid
    /// balance. Unusable counters are skipped instead of becoming a fabricated 0%.
    /// </summary>
    private static RawWindow? Counts(JsonElement? detail, TimeSpan duration)
    {
        if (detail is not { } value ||
            JsonUtil.FiniteNumber(value, "limit") is not { } limit ||
            limit <= 0)
        {
            return null;
        }

        double? used = JsonUtil.FiniteNumber(value, "used") is { } u && u >= 0 ? u : null;
        if (used is null &&
            JsonUtil.FiniteNumber(value, "remaining") is { } remaining &&
            remaining >= 0 && remaining <= limit)
        {
            used = limit - remaining;
        }

        if (used is not { } count)
        {
            return null;
        }

        var reset = JsonUtil.ParseIso8601(
            JsonUtil.NonEmptyString(value, "resetTime", "resetAt", "reset_time", "reset_at"));
        return new RawWindow(Math.Clamp(count / limit * 100, 0, 100), reset, duration);
    }

    private static TimeSpan? WindowDuration(JsonElement? window)
    {
        if (window is not { } value ||
            JsonUtil.Integer(value, "duration") is not { } amount ||
            amount <= 0)
        {
            return null;
        }

        var minutes = JsonUtil.NonEmptyString(value, "timeUnit", "time_unit") switch
        {
            "TIME_UNIT_MINUTE" => amount,
            "TIME_UNIT_HOUR" => amount * 60,
            "TIME_UNIT_DAY" => amount * 24 * 60,
            _ => -1,
        };

        return minutes is > 0 and <= CodexUsageParser.MaxWindowSeconds / 60
            ? TimeSpan.FromMinutes(minutes)
            : null;
    }

    /// <summary>Membership level names from the official V1 goods catalog; unknown levels stay verbatim.</summary>
    private static string? MapPlan(JsonElement root)
    {
        var user = JsonUtil.ObjectOrNull(root, "user");
        var membership = user is { } u ? JsonUtil.ObjectOrNull(u, "membership") : null;
        var level = membership is { } m ? JsonUtil.NonEmptyString(m, "level") : null;
        if (level is null || level == "LEVEL_UNSPECIFIED")
        {
            return null;
        }

        var version = JsonUtil.NonEmptyString(root, "version");
        if (version is not null && version != "GOODS_VERSION_V1")
        {
            return level;
        }

        return level switch
        {
            "LEVEL_FREE" => "Adagio",
            "LEVEL_TRIAL" => "Andante",
            "LEVEL_BASIC" => "Moderato",
            "LEVEL_INTERMEDIATE" => "Allegretto",
            "LEVEL_ADVANCED" => "Allegro",
            _ => level,
        };
    }
}

/// <summary>
/// Parser for <c>GET https://opencode.ai/zen/go/v1/usage</c> (OpenCode Go API key). The rolling
/// 5-hour meter is required, weekly and monthly are optional; the API reports percentages
/// directly in 0..100, matching the upstream <c>parseAPIUsage</c> path.
/// </summary>
public static class OpenCodeGoUsageParser
{
    public const string UsageEndpoint = "https://opencode.ai/zen/go/v1/usage";
    public const string SessionWindowName = "5小时";
    public const string WeeklyWindowName = "每周";
    public const string MonthlyWindowName = "每月";

    private static readonly string[] PercentKeys =
    {
        "usagePercent", "usedPercent", "percentUsed", "percent", "usage_percent", "used_percent",
        "utilization", "utilizationPercent", "utilization_percent",
    };

    private static readonly string[] ResetInKeys =
    {
        "resetInSec", "resetInSeconds", "resetSeconds", "reset_sec", "reset_in_sec", "resetsInSec",
        "resetsInSeconds", "resetIn", "resetSec",
    };

    private static readonly string[] ResetAtKeys =
    {
        "resetAt", "resetsAt", "reset_at", "resets_at", "nextReset", "next_reset", "renewAt", "renew_at",
    };

    public static ProviderUsageParseResult Parse(string json, DateTimeOffset? now = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            JsonUtil.ObjectOrNull(root, "usage") is not { } usage ||
            JsonUtil.ObjectOrNull(usage, "rolling") is not { } rolling ||
            ReadWindow(rolling, now ?? DateTimeOffset.UtcNow) is not { } session)
        {
            throw new JsonException("OpenCode Go usage payload is missing the rolling window.");
        }

        var clock = now ?? DateTimeOffset.UtcNow;
        var windows = new List<UsageWindow>
        {
            CodexUsageParser.Build(SessionWindowName, session, TimeSpan.FromHours(5)),
        };

        if (JsonUtil.ObjectOrNull(usage, "weekly") is { } weeklyElement &&
            ReadWindow(weeklyElement, clock) is { } weekly)
        {
            windows.Add(CodexUsageParser.Build(WeeklyWindowName, weekly, TimeSpan.FromDays(7)));
        }

        if (JsonUtil.ObjectOrNull(usage, "monthly") is { } monthlyElement &&
            ReadWindow(monthlyElement, clock) is { } monthly)
        {
            windows.Add(CodexUsageParser.Build(MonthlyWindowName, monthly));
        }

        return new ProviderUsageParseResult(null, null, windows);
    }

    private static RawWindow? ReadWindow(JsonElement window, DateTimeOffset now)
    {
        var percent = JsonUtil.FiniteNumber(window, PercentKeys);
        if (percent is null &&
            JsonUtil.FiniteNumber(window, "used", "usage", "consumed", "count", "usedTokens") is { } used &&
            JsonUtil.FiniteNumber(window, "limit", "total", "quota", "max", "cap", "tokenLimit") is { } limit &&
            limit > 0)
        {
            percent = used / limit * 100;
        }

        if (JsonUtil.ClampPercent(percent) is not { } usedPercent)
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        if (JsonUtil.Integer(window, ResetInKeys) is { } seconds && seconds >= 0 &&
            seconds <= CodexUsageParser.MaxWindowSeconds)
        {
            resetsAt = now.AddSeconds(seconds);
        }
        else
        {
            foreach (var key in ResetAtKeys)
            {
                if (DateValue(window, key) is { } at)
                {
                    resetsAt = at;
                    break;
                }
            }
        }

        return new RawWindow(usedPercent, resetsAt, null);
    }

    /// <summary>ISO-8601 strings, or Unix seconds / milliseconds as numbers or numeric strings.</summary>
    private static DateTimeOffset? DateValue(JsonElement parent, string key)
    {
        if (JsonUtil.FiniteNumber(parent, key) is { } number)
        {
            if (number > 1_000_000_000_000)
            {
                return JsonUtil.FromUnixSeconds((long)(number / 1000));
            }

            if (number > 1_000_000_000)
            {
                return JsonUtil.FromUnixSeconds((long)number);
            }
        }

        return JsonUtil.ParseIso8601(JsonUtil.NonEmptyString(parent, key));
    }
}
