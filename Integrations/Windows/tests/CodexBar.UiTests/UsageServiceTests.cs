using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexBar.Core;
using CodexBar.Windows.Services;

namespace CodexBar.UiTests;

/// <summary>
/// Regression cover for the cache-merge and cancellation bugs found during
/// review: a transient failure must keep the last good windows, anything that
/// means "different credentials" must clear them, parallel providers must not
/// swap data, and a superseded cycle must publish nothing.
/// </summary>
internal static class UsageServiceTests
{
    public static void Register()
    {
        TestRunner.Add("unavailable keeps last good windows and fetched time", UnavailableKeepsLastGood);
        TestRunner.Add("not-configured clears cached windows", NotConfiguredClears);
        TestRunner.Add("authentication-required clears cached windows", AuthenticationRequiredClears);
        TestRunner.Add("disabled provider clears cached windows", DisabledClears);
        TestRunner.Add("credential file metadata change clears cached windows", MetadataChangeClears);
        TestRunner.Add("switching credential path clears cached windows", PathSwitchClears);
        TestRunner.Add("two providers in parallel do not cross data", ParallelProvidersKeepDataSeparate);
        TestRunner.Add("cycle publishes the generation it was given", PublishesGeneration);
        TestRunner.Add("cancelled cycle publishes nothing", CancelledCyclePublishesNothing);
        TestRunner.Add("hostile credential paths are rejected without probing", HostilePathsAreRejected);
    }

    private static (UsageService Service, FakeUsageProvider Provider, SnapshotRecorder Recorder) Build(
        ProviderId providerId = ProviderId.Codex)
    {
        var fake = new FakeUsageProvider(providerId, (_, _) =>
            Task.FromResult(Fixtures.Ready(providerId, "Session", 10)));
        var service = new UsageService(new IUsageProvider[] { fake });
        var recorder = new SnapshotRecorder();
        service.SnapshotPublished += recorder.OnPublished;
        return (service, fake, recorder);
    }

    private static async Task RunCycleAsync(UsageService service, AppSettings settings, int generation = 1)
    {
        await service.RefreshAsync(settings, generation, CancellationToken.None).ConfigureAwait(false);
    }

    private static void UnavailableKeepsLastGood()
    {
        var directory = Scratch.NewDirectory("usage-merge");
        var credential = Scratch.WriteFile(Path.Combine(directory, "auth.json"), "{\"a\":1}");
        var settings = Fixtures.SettingsFor(ProviderId.Codex, credential);
        var (service, provider, recorder) = Build();

        var fetchedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        provider.Handler = (_, _) => Task.FromResult(
            Fixtures.Ready(ProviderId.Codex, "Weekly", 62, account: "acct-1", fetchedAt: fetchedAt));
        RunCycleAsync(service, settings).GetAwaiter().GetResult();

        provider.Handler = (_, _) => Task.FromResult(
            Fixtures.Failure(ProviderId.Codex, ProviderStatus.Unavailable, "network down"));
        RunCycleAsync(service, settings).GetAwaiter().GetResult();

        var last = recorder.Last;
        Check.Equal(ProviderStatus.Unavailable, last.Status, "status should be the new failure");
        Check.Equal("network down", last.Message, "the new error message should survive the merge");
        Check.Count(1, last.Windows, "the last good window list should be preserved");
        Check.Equal("Weekly", last.Windows[0].Name, "the preserved window should be the cached one");
        Check.Equal(fetchedAt, last.FetchedAt, "the cached fetch time should be preserved");
        Check.Equal("acct-1", last.Account, "the cached account should be preserved");

        service.Dispose();
    }

    private static void NotConfiguredClears() => ClearingStatus(ProviderStatus.NotConfigured, "not-configured");

    private static void AuthenticationRequiredClears()
        => ClearingStatus(ProviderStatus.AuthenticationRequired, "authentication-required");

    private static void ClearingStatus(ProviderStatus status, string label)
    {
        var directory = Scratch.NewDirectory($"usage-{label}");
        var credential = Scratch.WriteFile(Path.Combine(directory, "auth.json"), "{\"a\":1}");
        var settings = Fixtures.SettingsFor(ProviderId.Codex, credential);
        var (service, provider, recorder) = Build();

        provider.Handler = (_, _) => Task.FromResult(Fixtures.Ready(ProviderId.Codex, "Weekly", 62));
        RunCycleAsync(service, settings).GetAwaiter().GetResult();
        Check.Count(1, recorder.Last.Windows, "the ready cycle should have cached a window");

        provider.Handler = (_, _) => Task.FromResult(Fixtures.Failure(ProviderId.Codex, status, label));
        RunCycleAsync(service, settings).GetAwaiter().GetResult();

        Check.Equal(status, recorder.Last.Status, $"{label} status should be published");
        Check.Empty(recorder.Last.Windows, $"{label} must not keep windows from a previous credential");

        service.Dispose();
    }

    private static void DisabledClears()
    {
        var directory = Scratch.NewDirectory("usage-disabled");
        var credential = Scratch.WriteFile(Path.Combine(directory, "auth.json"), "{\"a\":1}");
        var settings = Fixtures.SettingsFor(ProviderId.Codex, credential);
        var (service, provider, recorder) = Build();

        provider.Handler = (_, _) => Task.FromResult(Fixtures.Ready(ProviderId.Codex, "Weekly", 62));
        RunCycleAsync(service, settings).GetAwaiter().GetResult();

        var disabled = settings with { CodexEnabled = false };
        RunCycleAsync(service, disabled).GetAwaiter().GetResult();

        Check.Equal(ProviderStatus.Disabled, recorder.Last.Status, "a disabled provider should report Disabled");
        Check.Empty(recorder.Last.Windows, "disabling must drop cached windows");
        Check.Equal(1, provider.CallCount, "a disabled provider must not be queried");

        service.Dispose();
    }

    private static void MetadataChangeClears()
    {
        var directory = Scratch.NewDirectory("usage-metadata");
        var credential = Path.Combine(directory, "auth.json");
        Scratch.WriteFile(credential, "{\"a\":1}");
        var settings = Fixtures.SettingsFor(ProviderId.Codex, credential);
        var (service, provider, recorder) = Build();

        provider.Handler = (_, _) => Task.FromResult(Fixtures.Ready(ProviderId.Codex, "Weekly", 62));
        RunCycleAsync(service, settings).GetAwaiter().GetResult();
        Check.Count(1, recorder.Last.Windows, "the ready cycle should have cached a window");

        // A rewritten credential file (different length and timestamp) means a
        // different account may now be logged in.
        Scratch.WriteFile(credential, "{\"a\":2,\"b\":\"rotated\"}");
        File.SetLastWriteTimeUtc(credential, DateTime.UtcNow.AddMinutes(5));

        provider.Handler = (_, _) => Task.FromResult(
            Fixtures.Failure(ProviderId.Codex, ProviderStatus.Unavailable, "network down"));
        RunCycleAsync(service, settings).GetAwaiter().GetResult();

        Check.Empty(recorder.Last.Windows, "a changed credential file must not reuse cached windows");

        service.Dispose();
    }

    private static void PathSwitchClears()
    {
        var directory = Scratch.NewDirectory("usage-path-switch");
        var first = Scratch.WriteFile(Path.Combine(directory, "one", "auth.json"), "{\"a\":1}");
        var second = Scratch.WriteFile(Path.Combine(directory, "two", "auth.json"), "{\"a\":2}");
        var (service, provider, recorder) = Build();

        provider.Handler = (_, _) => Task.FromResult(Fixtures.Ready(ProviderId.Codex, "Weekly", 62));
        RunCycleAsync(service, Fixtures.SettingsFor(ProviderId.Codex, first)).GetAwaiter().GetResult();
        Check.Count(1, recorder.Last.Windows, "the ready cycle should have cached a window");

        provider.Handler = (_, _) => Task.FromResult(
            Fixtures.Failure(ProviderId.Codex, ProviderStatus.Unavailable, "network down"));
        RunCycleAsync(service, Fixtures.SettingsFor(ProviderId.Codex, second)).GetAwaiter().GetResult();

        Check.Empty(recorder.Last.Windows, "a different credential path must not reuse cached windows");

        service.Dispose();
    }

    private static void ParallelProvidersKeepDataSeparate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var codex = new FakeUsageProvider(ProviderId.Codex, async (_, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return Fixtures.Ready(ProviderId.Codex, "CodexWeekly", 11, account: "codex-acct");
        });

        var claude = new FakeUsageProvider(ProviderId.Claude, async (_, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return Fixtures.Ready(ProviderId.Claude, "ClaudeWeekly", 22, account: "claude-acct");
        });

        var service = new UsageService(new IUsageProvider[] { codex, claude });
        var recorder = new SnapshotRecorder();
        service.SnapshotPublished += recorder.OnPublished;

        var settings = new AppSettings { CodexEnabled = true, ClaudeEnabled = true };
        var cycle = service.RefreshAsync(settings, 1, CancellationToken.None);

        // Both providers are now parked on the same gate, so their completions
        // interleave around one shared publish path.
        gate.SetResult();
        cycle.GetAwaiter().GetResult();

        Check.Equal(2, recorder.Count, "both providers should publish exactly one snapshot");

        var codexSnapshot = recorder.Entries.Single(entry => entry.Snapshot.Provider == ProviderId.Codex).Snapshot;
        var claudeSnapshot = recorder.Entries.Single(entry => entry.Snapshot.Provider == ProviderId.Claude).Snapshot;

        Check.Equal("codex-acct", codexSnapshot.Account, "the Codex snapshot should carry Codex's account");
        Check.Equal("CodexWeekly", codexSnapshot.Windows[0].Name, "the Codex snapshot should carry Codex's window");
        Check.Equal("claude-acct", claudeSnapshot.Account, "the Claude snapshot should carry Claude's account");
        Check.Equal(
            "ClaudeWeekly",
            claudeSnapshot.Windows[0].Name,
            "the Claude snapshot should carry Claude's window");

        service.Dispose();
    }

    private static void PublishesGeneration()
    {
        var directory = Scratch.NewDirectory("usage-generation");
        var credential = Scratch.WriteFile(Path.Combine(directory, "auth.json"), "{\"a\":1}");
        var settings = Fixtures.SettingsFor(ProviderId.Codex, credential);
        var (service, provider, recorder) = Build();

        provider.Handler = (_, _) => Task.FromResult(Fixtures.Ready(ProviderId.Codex, "Weekly", 62));
        RunCycleAsync(service, settings, generation: 7).GetAwaiter().GetResult();

        Check.Equal(7, recorder.LastEntry.Generation, "the published snapshot should carry its cycle's generation");

        service.Dispose();
    }

    private static void CancelledCyclePublishesNothing()
    {
        var directory = Scratch.NewDirectory("usage-cancel");
        var credential = Scratch.WriteFile(Path.Combine(directory, "auth.json"), "{\"a\":1}");
        var settings = Fixtures.SettingsFor(ProviderId.Codex, credential);
        var (service, provider, recorder) = Build();

        provider.Handler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return Fixtures.Ready(ProviderId.Codex, "Weekly", 62);
        };

        using var cancellation = new CancellationTokenSource();
        var cycle = service.RefreshAsync(settings, 1, cancellation.Token);
        cancellation.Cancel();
        cycle.GetAwaiter().GetResult();

        Check.Equal(0, recorder.Count, "a cancelled cycle must not publish a stale snapshot");

        // The service must stay usable: the cancelled cycle released the gate.
        provider.Handler = (_, _) => Task.FromResult(Fixtures.Ready(ProviderId.Codex, "Weekly", 5));
        RunCycleAsync(service, settings, generation: 2).GetAwaiter().GetResult();

        Check.Equal(1, recorder.Count, "the next cycle should publish normally after a cancellation");
        Check.Equal(2, recorder.LastEntry.Generation, "the fresh snapshot should carry the fresh generation");

        service.Dispose();
    }

    private static void HostilePathsAreRejected()
    {
        var hostile = new[]
        {
            @"\\attacker.invalid\share\auth.json",
            "//attacker.invalid/share/auth.json",
            "https://attacker.invalid/auth.json",
            @"\\.\pipe\auth.json",
        };

        foreach (var path in hostile)
        {
            Check.False(LocalPathProbe.IsLocal(path), $"'{path}' should not be treated as a local credential path");

            var (service, provider, recorder) = Build();
            provider.Handler = (_, _) => Task.FromResult(Fixtures.Ready(ProviderId.Codex, "Weekly", 62));

            var started = DateTime.UtcNow;
            RunCycleAsync(service, Fixtures.SettingsFor(ProviderId.Codex, path)).GetAwaiter().GetResult();
            var elapsed = DateTime.UtcNow - started;

            Check.Equal(
                ProviderStatus.Ready,
                recorder.Last.Status,
                $"a rejected path should still allow the provider cycle to complete ({path})");
            Check.True(
                elapsed < TimeSpan.FromSeconds(2),
                $"'{path}' must not be probed on the network (took {elapsed.TotalSeconds:0.00}s)");

            service.Dispose();
        }

        // Two different rejected paths must not look like the same credential,
        // or switching between them would merge unrelated windows.
        var (switchService, switchProvider, switchRecorder) = Build();
        switchProvider.Handler = (_, _) => Task.FromResult(Fixtures.Ready(ProviderId.Codex, "Weekly", 62));
        RunCycleAsync(switchService, Fixtures.SettingsFor(ProviderId.Codex, hostile[0])).GetAwaiter().GetResult();

        switchProvider.Handler = (_, _) => Task.FromResult(
            Fixtures.Failure(ProviderId.Codex, ProviderStatus.Unavailable, "network down"));
        RunCycleAsync(switchService, Fixtures.SettingsFor(ProviderId.Codex, hostile[1])).GetAwaiter().GetResult();

        Check.Empty(
            switchRecorder.Last.Windows,
            "switching between two rejected paths must not reuse cached windows");

        switchService.Dispose();
    }
}
