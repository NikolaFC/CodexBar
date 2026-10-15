namespace CodexBar.Windows.Services;

/// <summary>
/// Runs settings writes one at a time off the calling thread, and drops a write
/// that a newer request has already superseded. Toggling a setting twice in
/// quick succession therefore cannot let the slow older snapshot land on disk
/// after the newer one: the newest requested state is the state that gets
/// written.
/// </summary>
public sealed class SettingsWriter
{
    private readonly object _issueLock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _newest;

    /// <summary>
    /// Queues <paramref name="write"/> and returns the message the caller
    /// should show, or null when the write succeeded or was superseded.
    /// </summary>
    public async Task<string?> WriteAsync(
        Func<string?> write,
        CancellationToken cancellationToken = default)
    {
        int id;
        lock (_issueLock)
        {
            // Issuing under one lock keeps _newest at the highest issued id, so
            // the supersede check below can never be fooled by an older call
            // that happened to be counted last.
            id = ++_newest;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _newest) != id)
            {
                // A newer snapshot is queued or already written. It is a full
                // settings object built from this one, so nothing is lost by
                // skipping this older write.
                return null;
            }

            return await Task.Run(write, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
