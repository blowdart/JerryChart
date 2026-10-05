// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using Coravel;
using Coravel.Invocable;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ScheduledParentUriBackfill(
    ParentUriBackfillInvocation invocation,
    IHostApplicationLifetime lifetime,
    ILogger<ScheduledParentUriBackfill> logger) : IInvocable, ICancellableInvocable
{
    /// <inheritdoc />
    public CancellationToken CancellationToken { get; set; }

    /// <summary>Runs the shared backfill entrypoint with shutdown cancellation and a nonblocking lock guard.</summary>
    /// <returns>A task representing the scheduled invocation.</returns>
    public async Task Invoke()
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            CancellationToken, lifetime.ApplicationStopping);
        try
        {
            shutdown.Token.ThrowIfCancellationRequested();
            MonitorLog.ScheduledParentUriBackfillDue(logger);
            await invocation.RunAsync(scheduled: true, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            MonitorLog.ParentUriBackfillStopped(logger);
        }
        catch (Exception exception) when (exception is MySqlException or IOException or HttpRequestException
            or JsonException or ArgumentException or InvalidOperationException or InvalidDataException or TimeoutException)
        {
            MonitorLog.ParentUriBackfillFailed(logger, exception);
        }
    }
}

internal static class ParentUriBackfillScheduling
{
    internal static void AddParentUriBackfillScheduler(this IServiceCollection services)
    {
        services.AddScheduler();
        services.AddTransient<ParentUriBackfillInvocation>();
        services.AddTransient<ScheduledParentUriBackfill>();
    }

    internal static void UseParentUriBackfillScheduler(this IServiceProvider services)
    {
        ILogger logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(ScheduledParentUriBackfill));
        services.UseScheduler(scheduler =>
        {
            scheduler.Schedule<ScheduledParentUriBackfill>()
                .DailyAt(3, 0)
                .Zoned(TimeZoneInfo.Utc)
                .PreventOverlapping("parent-uri-backfill");
        }).OnError(exception => MonitorLog.ScheduledParentUriBackfillUnexpectedFailure(logger, exception));
        MonitorLog.ParentUriBackfillScheduleRegistered(logger);
    }
}