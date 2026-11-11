using System;
using System.Collections.Generic;
using CodexBar.Core;
using CodexBar.Windows.Services;

namespace CodexBar.UiTests;

/// <summary>
/// The low-quota alert must fire once per crossing: rising past 90% alerts,
/// staying above it stays quiet, dropping below re-arms, and the flag is only
/// cleared by crossing back down or by a genuine reset.
/// </summary>
internal static class LowQuotaNotifierTests
{
    public static void Register()
    {
        TestRunner.Add("crossing 90% alerts exactly once", CrossingAlertsOnce);
        TestRunner.Add("dropping below 90% re-arms the alert", DroppingBelowRearms);
        TestRunner.Add("a reset re-arms the alert above the threshold", ResetRearms);
        TestRunner.Add("reaching exactly 90% alerts", BoundaryAlerts);
        TestRunner.Add("each window is tracked separately", WindowsTrackedSeparately);
    }

    private static UsageSnapshot Snapshot(double usedPercent, DateTimeOffset? resetsAt)
        => new()
        {
            Provider = ProviderId.Claude,
            Status = ProviderStatus.Ready,
            Windows = new[]
            {
                new UsageWindow { Name = "Session", UsedPercent = usedPercent, ResetsAt = resetsAt },
            },
        };

    private static void CrossingAlertsOnce()
    {
        var notifier = new LowQuotaNotifier();
        var reset = DateTimeOffset.UtcNow.AddHours(2);

        Check.Empty(notifier.Evaluate(Snapshot(50, reset)), "50% should not alert");

        var alerts = notifier.Evaluate(Snapshot(92, reset));
        Check.Count(1, alerts, "crossing 90% should alert once");
        Check.True(alerts[0].Contains("Claude", StringComparison.Ordinal), "the alert should name the provider");

        Check.Empty(notifier.Evaluate(Snapshot(95, reset)), "staying above 90% must not alert again");
        Check.Empty(notifier.Evaluate(Snapshot(99, reset)), "still above 90% must stay quiet");
    }

    private static void DroppingBelowRearms()
    {
        var notifier = new LowQuotaNotifier();
        var reset = DateTimeOffset.UtcNow.AddHours(2);

        Check.Count(1, notifier.Evaluate(Snapshot(95, reset)), "the first crossing should alert");
        Check.Empty(notifier.Evaluate(Snapshot(95, reset)), "a repeat above the threshold should be quiet");
        Check.Empty(notifier.Evaluate(Snapshot(40, reset)), "dropping below should not itself alert");
        Check.Count(1, notifier.Evaluate(Snapshot(91, reset)), "re-crossing should alert again");
    }

    private static void ResetRearms()
    {
        var notifier = new LowQuotaNotifier();
        var firstReset = DateTimeOffset.UtcNow.AddHours(1);
        var secondReset = firstReset.AddHours(5);

        Check.Count(1, notifier.Evaluate(Snapshot(93, firstReset)), "the first window should alert");
        Check.Empty(notifier.Evaluate(Snapshot(93, firstReset)), "the same window should stay quiet");
        Check.Count(1, notifier.Evaluate(Snapshot(93, secondReset)), "a new reset time should alert again");
        Check.Empty(notifier.Evaluate(Snapshot(93, secondReset)), "the new window should then stay quiet");
    }

    private static void BoundaryAlerts()
    {
        var notifier = new LowQuotaNotifier();
        Check.Count(
            1,
            notifier.Evaluate(Snapshot(LowQuotaNotifier.Threshold, null)),
            "exactly the threshold counts as a crossing");
        Check.Empty(notifier.Evaluate(Snapshot(89.9, null)), "just below the threshold is not a crossing");
    }

    private static void WindowsTrackedSeparately()
    {
        var notifier = new LowQuotaNotifier();
        var snapshot = new UsageSnapshot
        {
            Provider = ProviderId.Codex,
            Status = ProviderStatus.Ready,
            Windows = new[]
            {
                new UsageWindow { Name = "Session", UsedPercent = 95 },
                new UsageWindow { Name = "Weekly", UsedPercent = 20 },
            },
        };

        var alerts = notifier.Evaluate(snapshot);
        Check.Count(1, alerts, "only the window above the threshold should alert");
        Check.True(alerts[0].Contains("Session", StringComparison.Ordinal), "the alert should name the window");

        var both = new UsageSnapshot
        {
            Provider = ProviderId.Codex,
            Status = ProviderStatus.Ready,
            Windows = new[]
            {
                new UsageWindow { Name = "Session", UsedPercent = 95 },
                new UsageWindow { Name = "Weekly", UsedPercent = 97 },
            },
        };

        Check.Count(
            1,
            notifier.Evaluate(both),
            "only the newly crossing window should alert, not the one already alerted");
    }
}
