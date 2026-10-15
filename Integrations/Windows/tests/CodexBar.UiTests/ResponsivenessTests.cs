using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CodexBar.Core;
using CodexBar.Windows.Services;

namespace CodexBar.UiTests;

/// <summary>
/// Regression cover for a synchronous provider prefix blocking the dispatcher.
/// A refresh is started from a real STA dispatcher thread, exactly the way the
/// tray app starts one, and the fake provider spends its synchronous prefix
/// sleeping the way a cold credential read or connection setup does. A
/// dispatcher heartbeat timer shows whether the message loop kept running:
/// while the prefix ran on the calling thread the loop delivered nothing, so
/// input, painting, and dragging all stalled for the same wall-clock time.
/// </summary>
internal static class ResponsivenessTests
{
    private const int ProviderPrefixMilliseconds = 600;
    private const int HeartbeatMilliseconds = 25;

    public static void Register()
    {
        TestRunner.Add(
            "a refresh started on the UI thread does not block the message loop",
            RefreshDoesNotBlockMessageLoop);
    }

    private static void RefreshDoesNotBlockMessageLoop()
    {
        var watch = Stopwatch.StartNew();
        var prefixStart = 0L;
        var prefixEnd = 0L;

        // Synchronous prefix: this is the shape of a credential read plus a
        // cold connection attempt, and the shape of the defect.
        var fake = new FakeUsageProvider(ProviderId.Codex, (_, _) =>
        {
            Volatile.Write(ref prefixStart, watch.ElapsedTicks);
            Thread.Sleep(ProviderPrefixMilliseconds);
            Volatile.Write(ref prefixEnd, watch.ElapsedTicks);
            return Task.FromResult(Fixtures.Ready(ProviderId.Codex, "Weekly", 10));
        });

        var service = new UsageService(new IUsageProvider[] { fake });
        var settings = new AppSettings { CodexEnabled = true, ClaudeEnabled = false };
        var tickTimes = new List<long>();
        var callDuration = TimeSpan.Zero;

        var completed = RunOnDispatcherThread(
            TimeSpan.FromSeconds(20),
            body: () =>
            {
                var started = watch.Elapsed;
                var task = service.RefreshAsync(settings, 1, CancellationToken.None);
                callDuration = watch.Elapsed - started;
                return task;
            },
            onHeartbeat: () => tickTimes.Add(watch.ElapsedTicks),
            out var threadError);

        Check.True(completed, "the dispatcher thread did not finish the refresh within the timeout");
        if (threadError is not null)
        {
            throw new AssertionFailedException($"the refresh threw: {threadError}");
        }

        Check.Equal(1, fake.CallCount, "the fake provider should have been queried exactly once");

        // The call itself must hand the provider work to another thread. Held
        // for the whole prefix before the fix (600 ms), a few milliseconds after.
        Check.True(
            callDuration < TimeSpan.FromMilliseconds(250),
            $"starting a refresh held the UI thread for {callDuration.TotalMilliseconds:0} ms " +
            $"(provider prefix is {ProviderPrefixMilliseconds} ms); the provider must run off the UI thread");

        var sleptFrom = Volatile.Read(ref prefixStart);
        var sleptTo = Volatile.Read(ref prefixEnd);
        var beatsDuringPrefix = 0;
        foreach (var tick in tickTimes)
        {
            if (tick >= sleptFrom && tick <= sleptTo)
            {
                beatsDuringPrefix++;
            }
        }

        // Printed as evidence: the numbers are the point of this check.
        Console.WriteLine(
            $"        measured: UI-thread hold {callDuration.TotalMilliseconds:0} ms, " +
            $"heartbeat ticks during the {ProviderPrefixMilliseconds} ms provider prefix {beatsDuringPrefix}");

        // 600 ms at a 25 ms heartbeat is ~24 beats. Requiring a handful keeps
        // the check stable on a loaded machine while still failing hard when
        // the loop was frozen for the whole prefix (0 beats).
        Check.True(
            beatsDuringPrefix >= 5,
            $"the UI heartbeat delivered {beatsDuringPrefix} ticks during the " +
            $"{ProviderPrefixMilliseconds} ms provider prefix; the message loop was blocked");

        service.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated STA thread that owns a WPF
    /// dispatcher and a heartbeat timer, the same arrangement the tray app has.
    /// The body starts only after the heartbeat has proven it is ticking.
    /// </summary>
    private static bool RunOnDispatcherThread(
        TimeSpan timeout,
        Func<Task> body,
        Action onHeartbeat,
        out Exception? error)
    {
        Exception? captured = null;
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var heartbeat = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(HeartbeatMilliseconds),
            };

            var warmupTicks = 0;
            var running = false;
            heartbeat.Tick += (_, _) =>
            {
                if (!running && ++warmupTicks < 3)
                {
                    return;
                }

                if (!running)
                {
                    running = true;
                    dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            await body();
                        }
                        catch (Exception ex)
                        {
                            captured = ex;
                        }
                        finally
                        {
                            heartbeat.Stop();
                            dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                        }
                    }));
                    return;
                }

                onHeartbeat();
            };
            heartbeat.Start();

            Dispatcher.Run();
            done.Set();
        })
        {
            IsBackground = true,
            Name = "codexbar-ui-heartbeat",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var finished = done.Wait(timeout);
        done.Dispose();
        error = captured;
        return finished;
    }
}
