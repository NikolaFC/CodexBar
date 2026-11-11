using System.Threading;

namespace CodexBar.Windows.Services;

/// <summary>
/// Enforces one running instance. A second launch signals the first through a
/// named event so the existing window can be brought forward, then exits.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\CodexBar.Windows.SingleInstance";
    private const string EventName = @"Local\CodexBar.Windows.ShowWindow";


    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle? showEvent, RegisteredWaitHandle? registration)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _registration = registration;
    }

    /// <summary>True when this is the first instance and owns the activation event.</summary>
    public bool IsPrimary => _showEvent is not null;

    /// <summary>
    /// Acquires the single-instance slot. Demo runs use a separate slot so a
    /// demo instance never blocks, or is woken by, the real tray app.
    /// </summary>
    public static SingleInstanceGuard Acquire(Action onActivationRequested, bool demoMode = false)
    {
        var suffix = demoMode ? ".Demo" : string.Empty;
        var mutexName = MutexName + suffix;
        var eventName = EventName + suffix;

        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance(eventName);
            return new SingleInstanceGuard(mutex, null, null);
        }

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            showEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    onActivationRequested();
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);

        return new SingleInstanceGuard(mutex, showEvent, registration);
    }

    private static void SignalExistingInstance(string eventName)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                }
            }
        }
        catch (Exception)
        {
            // A missing event just means the other instance is shutting down.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration?.Unregister(null);
        _showEvent?.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was not owned by this thread; nothing to release.
        }

        _mutex.Dispose();
    }
}
