// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Threading.Channels;

using JerryChart.Monitor;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Tests;

/// <summary>Verifies startup and daily UTC backfill scheduling and cancellation without network calls.</summary>
[TestClass]
public sealed class ParentUriBackfillSchedulingTests
{
    /// <summary>Verifies startup invokes the shared entrypoint once and retains daily service registrations.</summary>
    /// <returns>A task representing registration and startup testing.</returns>
    [TestMethod]
    public async Task StartupInvokesSharedEntrypointOnceWithDailyServiceRegistration()
    {
        var builder = Host.CreateApplicationBuilder();
        var logger = new ScheduleLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logger);
        await using var source = new MySqlDataSourceBuilder(
            "Server=unused.invalid;Database=unused;User ID=unused").Build();
        builder.Services.AddSingleton(source);
        var clock = new ScheduleClock();
        builder.Services.AddSingleton<TimeProvider>(clock);
        int invocations = 0;
        builder.Services.AddSingleton<Func<HttpClient>>(() =>
        {
            Interlocked.Increment(ref invocations);
            throw new IOException("Simulated startup failure before any database or network access.");
        });
        builder.Services.AddParentUriBackfillScheduler();
        Assert.AreEqual(ServiceLifetime.Transient,
            builder.Services.Single(service => service.ServiceType == typeof(ParentUriBackfillInvocation)).Lifetime);
        Assert.AreEqual(ServiceLifetime.Singleton,
            builder.Services.Single(service => service.ServiceType == typeof(ScheduledParentUriBackfill)).Lifetime);
        using var host = builder.Build();
        Assert.AreSame(host.Services.GetRequiredService<ScheduledParentUriBackfill>(),
            host.Services.GetServices<IHostedService>().Single(service => service is ScheduledParentUriBackfill));
        await host.StartAsync(TestContext.CancellationToken);
        await clock.NextTimerAsync();
        Assert.AreEqual(1, invocations);
        await host.StopAsync(TestContext.CancellationToken);
        Assert.AreEqual(0, logger.StoppedInvocations);
        Assert.AreEqual(1, logger.Failures, "A known startup failure must be logged without terminating scheduling.");
    }

    /// <summary>Verifies daily slots remain strictly future and UTC, including the exact boundary.</summary>
    [TestMethod]
    public void NextSlotIsStrictlyFutureAndAlwaysUtc()
    {
        var slot = new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(slot, ScheduledParentUriBackfill.NextRun(slot.AddTicks(-1)));
        Assert.AreEqual(slot.AddDays(1), ScheduledParentUriBackfill.NextRun(slot));
        Assert.AreEqual(slot.AddDays(1), ScheduledParentUriBackfill.NextRun(slot.AddHours(12)));
        Assert.AreEqual(slot, ScheduledParentUriBackfill.NextRun(slot.AddMinutes(-1).ToOffset(TimeSpan.FromHours(-7))));
        var yearEnd = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(new DateTimeOffset(2027, 1, 1, 3, 0, 0, TimeSpan.Zero),
            ScheduledParentUriBackfill.NextRun(yearEnd));
    }

    /// <summary>Verifies a canceled invocation does not allocate an HTTP client or start a worker.</summary>
    /// <returns>A task representing invocation cancellation testing.</returns>
    [TestMethod]
    public async Task CancellationIsLinkedBeforeSharedEntrypointRuns()
    {
        using var host = Host.CreateApplicationBuilder().Build();
        await using var source = new MySqlDataSourceBuilder(
            "Server=unused.invalid;Database=unused;User ID=unused").Build();
        var invocation = new ParentUriBackfillInvocation(source,
            host.Services.GetRequiredService<ILogger<ParentUriBackfillInvocation>>(), TimeProvider.System,
            () => throw new AssertFailedException("Canceled invocations must not create a network client."));
        using var scheduled = new ScheduledParentUriBackfill(invocation,
            host.Services.GetRequiredService<IHostApplicationLifetime>(),
            host.Services.GetRequiredService<ILogger<ScheduledParentUriBackfill>>(), TimeProvider.System);
        await scheduled.InvokeAsync(new CancellationToken(canceled: true));
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await scheduled.InvokeAsync(CancellationToken.None);
        Assert.IsTrue(host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.IsCancellationRequested);
    }

    /// <summary>Verifies startup, the next UTC slot, no repeats or catch-up replay, and awaited shutdown.</summary>
    /// <returns>A task representing controlled-clock service execution.</returns>
    [TestMethod]
    public async Task BackgroundLoopRunsAtStartupAndNextSlotWithoutRepeatingOrCatchingUp()
    {
        using var host = Host.CreateApplicationBuilder().Build();
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await using var source = new MySqlDataSourceBuilder(
            "Server=unused.invalid;Database=unused;User ID=unused").Build();
        var logger = new ScheduleLogger();
        var clock = new ScheduleClock();
        var invocation = new ParentUriBackfillInvocation(source,
            host.Services.GetRequiredService<ILogger<ParentUriBackfillInvocation>>(), clock,
            () => throw new AssertFailedException("Stopped host must not allocate a client."));
        using var scheduled = new ScheduledParentUriBackfill(invocation,
            host.Services.GetRequiredService<IHostApplicationLifetime>(), logger, clock);
        await scheduled.StartAsync(CancellationToken.None);
        try
        {
            ScheduleTimer first = await clock.NextTimerAsync();
            Assert.AreEqual(1, logger.StoppedInvocations);
            Assert.AreEqual(TimeSpan.FromMinutes(1), first.DueTime);
            clock.Advance(TimeSpan.FromMinutes(1), first);
            ScheduleTimer second = await clock.NextTimerAsync();
            Assert.AreEqual(2, logger.StoppedInvocations);
            clock.Advance(TimeSpan.FromSeconds(1), second);
            ScheduleTimer third = await clock.NextTimerAsync();
            Assert.AreEqual(2, logger.StoppedInvocations, "The same daily slot must not repeat.");
            clock.Advance(TimeSpan.FromDays(3), third);
            await clock.NextTimerAsync();
            Assert.AreEqual(3, logger.StoppedInvocations, "Missed slots must not be replayed individually.");
        }
        finally
        {
            await scheduled.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        }
        Assert.AreEqual(0, logger.Failures);
    }

    /// <summary>Verifies startup exactly at or crossing 03:00 UTC consumes that slot without a duplicate immediate run.</summary>
    /// <param name="crossesBoundary">Whether startup work advances the clock across the daily boundary.</param>
    /// <returns>A task representing deterministic boundary testing.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartupAtOrAcrossDailyBoundarySchedulesTomorrow(bool crossesBoundary)
    {
        using var host = Host.CreateApplicationBuilder().Build();
        await using var source = new MySqlDataSourceBuilder(
            "Server=unused.invalid;Database=unused;User ID=unused").Build();
        DateTimeOffset slot = new(2026, 10, 5, 3, 0, 0, TimeSpan.Zero);
        var clock = new ScheduleClock(crossesBoundary ? slot.AddSeconds(-1) : slot);
        var logger = new ScheduleLogger();
        int invocations = 0;
        var invocation = new ParentUriBackfillInvocation(source,
            host.Services.GetRequiredService<ILogger<ParentUriBackfillInvocation>>(), clock, () =>
            {
                Interlocked.Increment(ref invocations);
                if (crossesBoundary && invocations == 1)
                {
                    clock.SetUtcNow(slot.AddDays(3).AddSeconds(1));
                }
                throw new IOException("Simulated startup work without database access.");
            });
        using var scheduled = new ScheduledParentUriBackfill(invocation,
            host.Services.GetRequiredService<IHostApplicationLifetime>(), logger, clock);
        await scheduled.StartAsync(CancellationToken.None);
        try
        {
            ScheduleTimer first = await clock.NextTimerAsync();
            Assert.AreEqual(1, invocations);
            Assert.AreEqual(TimeSpan.FromMinutes(1), first.DueTime);
            DateTimeOffset next = ScheduledParentUriBackfill.NextRun(clock.GetUtcNow());
            Assert.AreEqual(crossesBoundary ? slot.AddDays(4) : slot.AddDays(1), next);
            clock.Advance(next - clock.GetUtcNow() - TimeSpan.FromSeconds(1), first);
            ScheduleTimer nearSlot = await clock.NextTimerAsync();
            Assert.AreEqual(1, invocations, "Startup must consume the current/crossed slot, not replay it.");
            Assert.AreEqual(TimeSpan.FromSeconds(1), nearSlot.DueTime);
            clock.Advance(TimeSpan.FromSeconds(1), nearSlot);
            await clock.NextTimerAsync();
            Assert.AreEqual(2, invocations, "The next daily slot must still execute normally.");
        }
        finally
        {
            await scheduled.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        }
    }

    /// <summary>Verifies startup holds the overlap guard until shared work finishes and shutdown awaits it.</summary>
    /// <returns>A task representing bounded overlap and cancellation testing.</returns>
    [TestMethod]
    public async Task StartupSuppressesOverlappingInvocationsAndShutdownAwaitsCompletion()
    {
        using var host = Host.CreateApplicationBuilder().Build();
        await using var source = new MySqlDataSourceBuilder(
            "Server=unused.invalid;Database=unused;User ID=unused").Build();
        var clock = new ScheduleClock();
        var logger = new ScheduleLogger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int invocations = 0;
        var invocation = new ParentUriBackfillInvocation(source,
            host.Services.GetRequiredService<ILogger<ParentUriBackfillInvocation>>(), clock, () =>
            {
                Interlocked.Increment(ref invocations);
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10), TestContext.CancellationToken))
                {
                    throw new AssertFailedException("The startup invocation must be released by the test.");
                }
                throw new IOException("Simulated bounded startup work without database access.");
            });
        using var scheduled = new ScheduledParentUriBackfill(invocation,
            host.Services.GetRequiredService<IHostApplicationLifetime>(), logger, clock);
        await scheduled.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            await scheduled.InvokeAsync(CancellationToken.None);
            Assert.AreEqual(1, invocations, "An active startup run must suppress another scheduler invocation.");
            Task stop = scheduled.StopAsync(CancellationToken.None);
            Assert.IsFalse(stop.IsCompleted, "Shutdown must await the active invocation.");
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            Assert.AreEqual(1, invocations);
        }
        finally
        {
            release.Set();
            await scheduled.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        }
    }

    private sealed class ScheduleClock(DateTimeOffset? now = null) : TimeProvider
    {
        private DateTimeOffset _now = now ?? new(2026, 10, 5, 2, 59, 0, TimeSpan.Zero);
        private readonly Channel<ScheduleTimer> _timers = Channel.CreateUnbounded<ScheduleTimer>();
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ScheduleTimer(callback, state, dueTime);
            Assert.IsTrue(_timers.Writer.TryWrite(timer));

            return timer;
        }
        internal async Task<ScheduleTimer> NextTimerAsync()
        {
            return await _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        internal void Advance(TimeSpan elapsed, ScheduleTimer timer)
        {
            _now += elapsed;
            timer.Fire();
        }
        internal void SetUtcNow(DateTimeOffset now) => _now = now;
    }

    private sealed class ScheduleTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        internal TimeSpan DueTime { get; } = dueTime;
        internal void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScheduleLogger : ILoggerProvider, ILogger<ScheduledParentUriBackfill>
    {
        private int _stoppedInvocations;
        private int _failures;
        internal int StoppedInvocations => _stoppedInvocations;
        internal int Failures => _failures;
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 22)
            {
                Interlocked.Increment(ref _stoppedInvocations);
            }

            if (logLevel >= LogLevel.Error)
            {
                Interlocked.Increment(ref _failures);
            }
        }

        public void Dispose() { }
    }

    public TestContext TestContext { get; set; }
}