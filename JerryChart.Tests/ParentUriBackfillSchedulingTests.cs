// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Coravel.Scheduling.Schedule;
using Coravel.Scheduling.Schedule.Interfaces;

using JerryChart.Monitor;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Tests;

/// <summary>Verifies the installed Coravel daily schedule and host cancellation wiring without network calls.</summary>
[TestClass]
public sealed class ParentUriBackfillSchedulingTests
{
    /// <summary>Verifies daily 03:00 UTC registration, no immediate startup run, and fresh invocation registrations.</summary>
    /// <returns>A task representing deterministic Coravel tick testing.</returns>
    [TestMethod]
    public async Task DailyScheduleUsesUtcWithoutAnImmediateStartupInvocation()
    {
        var builder = Host.CreateApplicationBuilder();
        var logger = new ScheduleLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logger);
        await using var source = new MySqlDataSourceBuilder(
            "Server=unused.invalid;Database=unused;User ID=unused").Build();
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddParentUriBackfillScheduler();
        Assert.AreEqual(ServiceLifetime.Transient,
            builder.Services.Single(service => service.ServiceType == typeof(ParentUriBackfillInvocation)).Lifetime);
        Assert.AreEqual(ServiceLifetime.Transient,
            builder.Services.Single(service => service.ServiceType == typeof(ScheduledParentUriBackfill)).Lifetime);
        using var host = builder.Build();
        host.Services.UseParentUriBackfillScheduler();
        var scheduler = (Scheduler)host.Services.GetRequiredService<IScheduler>();
        // No hosted timer is started. Explicit Coravel ticks exercise its actual installed API.
        // A stopped host lets due invocations be observed without connecting to any database.
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await scheduler.RunAtAsync(new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual(0, logger.StoppedInvocations);
        await scheduler.RunAtAsync(new DateTime(2026, 10, 5, 2, 59, 0, DateTimeKind.Utc));
        Assert.AreEqual(0, logger.StoppedInvocations);
        await scheduler.RunAtAsync(new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual(1, logger.StoppedInvocations);
        await scheduler.RunAtAsync(new DateTime(2026, 10, 5, 3, 0, 1, DateTimeKind.Utc));
        await scheduler.RunAtAsync(new DateTime(2026, 10, 5, 3, 1, 0, DateTimeKind.Utc));
        Assert.AreEqual(1, logger.StoppedInvocations);
        await scheduler.RunAtAsync(new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual(2, logger.StoppedInvocations);
        Assert.AreEqual(0, logger.Failures);
    }

    /// <summary>Verifies a canceled Coravel invocation does not allocate an HTTP client or start a worker.</summary>
    /// <returns>A task representing invocation cancellation testing.</returns>
    [TestMethod]
    public async Task CoravelCancellationIsLinkedBeforeSharedEntrypointRuns()
    {
        using var host = Host.CreateApplicationBuilder().Build();
        await using var source = new MySqlDataSourceBuilder(
            "Server=unused.invalid;Database=unused;User ID=unused").Build();
        var invocation = new ParentUriBackfillInvocation(source,
            host.Services.GetRequiredService<ILogger<ParentUriBackfillInvocation>>(), TimeProvider.System,
            () => throw new AssertFailedException("Canceled invocations must not create a network client."));
        var scheduled = new ScheduledParentUriBackfill(invocation,
            host.Services.GetRequiredService<IHostApplicationLifetime>(),
            host.Services.GetRequiredService<ILogger<ScheduledParentUriBackfill>>())
        {
            CancellationToken = new CancellationToken(canceled: true)
        };
        await scheduled.Invoke();
        Assert.IsTrue(scheduled.CancellationToken.IsCancellationRequested);
    }

    private sealed class ScheduleLogger : ILoggerProvider, ILogger
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
}