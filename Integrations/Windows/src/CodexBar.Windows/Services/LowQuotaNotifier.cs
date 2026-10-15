using CodexBar.Core;

namespace CodexBar.Windows.Services;

/// <summary>
/// Fires once when a quota window crosses the 90% used threshold. The flag is
/// cleared when the window resets, so the alert can fire again next cycle.
/// </summary>
public sealed class LowQuotaNotifier
{
    public const double Threshold = 90;

    private readonly Dictionary<string, NotificationState> _states = new();

    public IReadOnlyList<string> Evaluate(UsageSnapshot snapshot)
    {
        var alerts = new List<string>();

        foreach (var window in snapshot.Windows)
        {
            var key = $"{snapshot.Provider}:{window.Name}";
            var above = window.UsedPercent >= Threshold;
            var resetChanged = _states.TryGetValue(key, out var existing)
                               && existing.ResetsAt != window.ResetsAt;

            if (!_states.TryGetValue(key, out var state) || resetChanged)
            {
                state = new NotificationState(window.ResetsAt);
                _states[key] = state;
            }

            state.ResetsAt = window.ResetsAt;
            if (resetChanged)
            {
                state.Notified = false;
            }

            if (above && !state.Notified)
            {
                state.Notified = true;
                var label = snapshot.Provider == ProviderId.Codex ? "Codex" : "Claude";
                alerts.Add($"{label} · {window.Name} 额度已用 {Math.Round(window.UsedPercent)}%");
            }
            else if (!above)
            {
                state.Notified = false;
            }
        }

        return alerts;
    }

    private sealed class NotificationState
    {
        public NotificationState(DateTimeOffset? resetsAt) => ResetsAt = resetsAt;

        public DateTimeOffset? ResetsAt { get; set; }

        public bool Notified { get; set; }
    }
}
