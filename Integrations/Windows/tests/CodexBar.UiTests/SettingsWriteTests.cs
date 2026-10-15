using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using CodexBar.Windows.Services;

namespace CodexBar.UiTests;

/// <summary>
/// Regression cover for the settings write path. Toggling remaining/used used
/// to save synchronously on the UI thread, and two quick saves could finish out
/// of order and leave the older snapshot on disk.
/// </summary>
internal static class SettingsWriteTests
{
    public static void Register()
    {
        TestRunner.Add("a superseded settings write never reaches the disk", SupersededWriteIsDropped);
        TestRunner.Add("a write with no newer request still lands", SoleWriteLands);
        TestRunner.Add("demo settings never touch the disk", DemoWritesNothing);
        TestRunner.Add("credential discovery rejects a non-local path", DiscoveryRejectsNonLocalPath);
    }

    /// <summary>
    /// The same policy as the usage fingerprint: discovery must never surface a
    /// path that resolves to another machine, and must not stall on one.
    /// <c>CredentialDiscovery</c> is what the settings window calls, so a
    /// non-local answer here is what would hand a UNC path to the file probe.
    /// </summary>
    private static void DiscoveryRejectsNonLocalPath()
    {
        var original = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", @"\\attacker.invalid\share");

            // ResolvePath rejects a non-local environment directory itself and
            // falls back to the default profile, so the request may legitimately
            // resolve to a local path. What must never happen is a non-local
            // answer escaping onto the UI, or a network probe inside this call.
            var started = DateTime.UtcNow;
            var discovered = CredentialDiscovery.CodexAuthPath;
            var elapsed = DateTime.UtcNow - started;

            Check.True(
                discovered is null || Core.LocalPathProbe.IsLocal(discovered),
                $"discovery must never surface a non-local credential path (got '{discovered}')");
            Check.True(
                elapsed < TimeSpan.FromSeconds(2),
                $"a non-local CODEX_HOME must not be probed on the network (took {elapsed.TotalSeconds:0.00}s)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", original);
        }
    }

    private static void SupersededWriteIsDropped()
    {
        var writer = new SettingsWriter();
        var written = new ConcurrentQueue<string>();
        var blockingStarted = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);

        // Hold the writer so the two later requests are both queued behind one
        // in-flight write, which is the shape of two quick toggles during a
        // slow disk write.
        var blocking = writer.WriteAsync(() =>
        {
            blockingStarted.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            written.Enqueue("blocker");
            return null;
        });

        Check.True(blockingStarted.Wait(TimeSpan.FromSeconds(10)), "the writer never started the blocking write");

        var older = writer.WriteAsync(() =>
        {
            written.Enqueue("old");
            return null;
        });

        var newer = writer.WriteAsync(() =>
        {
            written.Enqueue("new");
            return null;
        });

        release.Set();
        blocking.GetAwaiter().GetResult();
        older.GetAwaiter().GetResult();
        newer.GetAwaiter().GetResult();

        var sequence = written.ToArray();
        Check.False(
            sequence.Contains("old", StringComparer.Ordinal),
            $"the superseded write must not land after the newer one (wrote: {string.Join(", ", sequence)})");
        Check.Equal(
            1,
            sequence.Count(entry => string.Equals(entry, "new", StringComparison.Ordinal)),
            "the newest settings snapshot must be written exactly once");
    }

    private static void SoleWriteLands()
    {
        var writer = new SettingsWriter();
        var count = 0;

        var result = writer.WriteAsync(() =>
        {
            Interlocked.Increment(ref count);
            return null;
        });

        Check.True(result.GetAwaiter().GetResult() is null, "a sole write should report success");
        Check.Equal(1, Volatile.Read(ref count), "a sole write must not be dropped as superseded");
    }

    private static void DemoWritesNothing()
    {
        var service = new SettingsService(demoMode: true);

        Check.True(service.IsEphemeral, "demo settings must stay in memory");
        Check.True(service.FilePath is null, "demo settings must not resolve a file path");
        Check.True(
            service.SaveAsync(new Core.AppSettings()).GetAwaiter().GetResult() is null,
            "an asynchronous demo save must succeed without writing");
        Check.True(
            service.Save(new Core.AppSettings()) is null,
            "a synchronous demo save must succeed without writing");
    }
}
