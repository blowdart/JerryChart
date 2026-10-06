// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Data;
using JerryChart.Monitor;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JerryChart.Tests;

/// <summary>Verifies processing freshness and independent heartbeat lifecycle without network calls.</summary>
[TestClass]
public sealed class ProcessingStatusTests
{
    /// <summary>Verifies exact TTL boundaries, every active phase, and durable terminal states.</summary>
    [TestMethod]
    public void FreshnessDoesNotInferRunningFromTerminalOrMissingActivity()
    {
        DateTimeOffset now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        foreach (string phase in new[] { "archive", "live", "backfill-running", "retrying" })
        {
            var activity = new WorkerActivity(phase, phase, false, now, now, now, null);
            Assert.IsTrue(activity.Evaluate(now.AddSeconds(59)).IsRunning);
            Assert.AreEqual(phase, activity.Evaluate(now.AddSeconds(59)).State);
            Assert.IsFalse(activity.Evaluate(now.AddSeconds(60)).IsRunning);
            Assert.AreEqual("stale", activity.Evaluate(now.AddSeconds(60)).State);
            Assert.AreEqual("stale", (activity with { HeartbeatAt = null }).Evaluate(now).State);
            Assert.AreEqual("stale", activity.Evaluate(now.AddSeconds(-1)).State);
        }

        foreach (string phase in new[] { "not-started", "completed", "stopped", "failure" })
        {
            var activity = new WorkerActivity(phase, phase, false, null, null, null, null);
            Assert.AreEqual(phase, activity.Evaluate(now.AddDays(1)).State);
            Assert.IsFalse(activity.Evaluate(now).IsRunning);
        }
    }

    /// <summary>Verifies failures are logged, later ticks recover, ticks do not overlap, and cancellation is awaited.</summary>
    /// <returns>A task representing deterministic heartbeat testing.</returns>
    [TestMethod]
    public async Task HeartbeatFailuresRecoverWithoutOverlappingOrOutlivingCancellation()
    {
        var clock = new ProcessingTestClock();
        var logger = new RecordingLogger();
        using var shutdown = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int concurrent = 0;
        int maximumConcurrent = 0;
        Task loop = ProcessingActivity.RunHeartbeatAsync(async token =>
        {
            int current = Interlocked.Increment(ref concurrent);
            maximumConcurrent = Math.Max(maximumConcurrent, current);
            int call = Interlocked.Increment(ref calls);
            try
            {
                if (call == 1)
                {
                    throw new IOException("Simulated transient status database failure.");
                }

                if (call == 2)
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(token);
                }
                else
                {
                    third.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return true;
            }
            finally
            {
                Interlocked.Decrement(ref concurrent);
            }
        }, logger, "monitor", clock, shutdown.Token);

        clock.Advance(TimeSpan.FromSeconds(15));
        await logger.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.AreEqual(1, calls);
        clock.Advance(TimeSpan.FromSeconds(15));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(2, calls, "An outstanding heartbeat must coalesce ticks, not start overlapping requests.");
        release.SetResult();
        await third.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        await shutdown.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.AreEqual(1, maximumConcurrent);
        Assert.AreEqual(0, concurrent);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(3, calls);
    }

    /// <summary>Verifies a replaced owner stops heartbeating rather than logging success.</summary>
    /// <returns>A task representing the ownership test.</returns>
    [TestMethod]
    public async Task HeartbeatStopsWhenOwnershipIsLost()
    {
        var clock = new ProcessingTestClock();
        int calls = 0;
        Task loop = ProcessingActivity.RunHeartbeatAsync(_ =>
        {
            calls++;
            return Task.FromResult(false);
        }, NullLogger.Instance, "parent-uri-backfill", clock, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(15));
        await loop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, calls);
    }

    private sealed class RecordingLogger : ILogger
    {
        internal TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 27 && exception is IOException)
            {
                Failed.TrySetResult();
            }
        }
    }

    public TestContext TestContext { get; set; }
}

internal sealed class ProcessingTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = [];

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    internal void Advance(TimeSpan duration)
    {
        _now += duration;
        foreach (ManualTimer timer in _timers.ToArray())
        {
            timer.Tick(_now);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ProcessingTestClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset _due;
        private TimeSpan _period;
        private bool _disposed;

        internal ManualTimer(ProcessingTestClock clock, TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
            Change(dueTime, period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _clock.GetUtcNow() + dueTime;
            _period = period;
            return !_disposed;
        }

        internal void Tick(DateTimeOffset now)
        {
            if (_disposed || now < _due)
            {
                return;
            }

            _due = _period > TimeSpan.Zero ? now + _period : DateTimeOffset.MaxValue;
            _callback(_state);
        }

        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}