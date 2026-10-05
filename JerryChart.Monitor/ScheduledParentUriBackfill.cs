// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ScheduledParentUriBackfill(
    ParentUriBackfillInvocation invocation,
    IHostApplicationLifetime lifetime,
    ILogger<ScheduledParentUriBackfill> logger,
    TimeProvider clock) : BackgroundService
{
    private int _running;

    internal static DateTimeOffset NextRun(DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        var next = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 3, 0, 0, TimeSpan.Zero);

        return next > utc ? next : next.AddDays(1);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MonitorLog.ParentUriBackfillScheduleRegistered(logger);
        DateTimeOffset next = NextRun(clock.GetUtcNow());
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan remaining = next - clock.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    // Recheck wall-clock changes without replaying missed daily slots.
                    await Task.Delay(remaining < TimeSpan.FromMinutes(1) ? remaining : TimeSpan.FromMinutes(1),
                        clock, stoppingToken);
                    continue;
                }

                await InvokeAsync(stoppingToken);
                DateTimeOffset now = clock.GetUtcNow();
                next = NextRun(now > next ? now : next);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels both the schedule wait and any active invocation.
        }
    }

    internal async Task InvokeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.ApplicationStopping);
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
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}

internal static class ParentUriBackfillScheduling
{
    internal static void AddParentUriBackfillScheduler(this IServiceCollection services)
    {
        services.AddTransient<ParentUriBackfillInvocation>();
        services.AddSingleton<ScheduledParentUriBackfill>();
        services.AddHostedService(services => services.GetRequiredService<ScheduledParentUriBackfill>());
    }
}