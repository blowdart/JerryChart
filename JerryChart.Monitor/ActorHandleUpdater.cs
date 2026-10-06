// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using idunno.AtProto;

using JerryChart.Data;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ActorHandleUpdater(
    MySqlDataSource dataSource, ILogger<ActorHandleUpdater> logger, TimeProvider? timeProvider = null) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using HttpClient httpClient = MonitorHttpClients.CreateAppViewClient();

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        ProcessingActivity? activity = null;
        var client = new ActorProfileClient(httpClient, clock, async (delay, token) =>
        {
            await WaitForRequestAsync(delay, async (phase, cancellationToken) =>
            {
                if (activity is not null)
                {
                    await activity.ChangeBestEffortAsync(phase, cancellationToken);
                }
            }, (duration, cancellationToken) => Task.Delay(duration, cancellationToken), token);
        });
        int failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = new MySqlConnectionStringBuilder(dataSource.ConnectionString) { Pooling = false };
                await using var connection = new MySqlConnection(options.ConnectionString);
                await connection.OpenAsync(stoppingToken);
                ActorStore store = await InitializeStoreAsync(connection, stoppingToken);
                try
                {
                    activity = await ProcessingActivity.StartAsync(dataSource, logger, "handle-refresh",
                        "handle-refresh-running", ":actor-handles", connection, clock, stoppingToken);
                }
                catch (Exception exception) when (exception is MySqlException or OperationCanceledException or InvalidOperationException)
                {
                    // Visibility failures must not change actor resolution or its existing retry policy.
                    MonitorLog.ProcessingStatusWriteFailed(logger, exception, "handle-refresh", "start");
                }

                async Task RefreshAsync()
                {
                    string? lastPhase = activity is null ? null : "handle-refresh-running";
                    async Task ReportAsync(string phase)
                    {
                        if (activity is not null && lastPhase != phase &&
                            await activity.ChangeBestEffortAsync(phase, stoppingToken))
                        {
                            lastPhase = phase;
                        }
                    }

                    while (!stoppingToken.IsCancellationRequested)
                    {
                        IReadOnlyList<ActorRefreshRequest> actors = await store.GetDueAsync(stoppingToken);
                        if (actors.Count == 0)
                        {
                            await ReportAsync("handle-refresh-idle");
                            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                            continue;
                        }

                        await ReportAsync("handle-refresh-running");
                        IReadOnlyDictionary<Did, Handle?> handles = await client.GetAsync(actors, stoppingToken);
                        foreach (ActorRefreshRequest actor in actors)
                        {
                            Handle? handle = handles[actor.Did];
                            ActorResolution resolution = handle is not null
                                ? new(handle, null)
                                : await client.ResolveMissingAsync(actor.Did, stoppingToken);
                            if (resolution.AccountStatus is not null)
                            {
                                MonitorLog.ActorInactive(logger, actor.Did, resolution.AccountStatus, resolution.RefreshSeconds);
                            }
                            else if (handle is null)
                            {
                                MonitorLog.MissingHandle(logger, actor.Did);
                            }

                            await store.SaveResolutionAsync(actor, resolution, stoppingToken);
                        }

                        failures = 0;
                    }
                }

                try
                {
                    if (activity is null)
                    {
                        await RefreshAsync();
                    }
                    else
                    {
                        await activity.ExecuteAsync(RefreshAsync, "stopped", stoppingToken, IsRetryable);
                    }
                }
                finally
                {
                    activity = null;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                failures = Math.Min(failures + 1, 7);
                TimeSpan retryDelay = RetryLoop.Delay(failures);
                MonitorMetrics.Retry(MetricOperation.ActorRefresh, exception, retryDelay);
                MonitorLog.RetryingActorRefresh(logger, exception, retryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(retryDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

        }
    }

    private static bool IsRetryable(Exception exception) =>
        exception is MySqlException or HttpRequestException or IOException or JsonException
            or InvalidOperationException or OperationCanceledException;

    internal static async Task WaitForRequestAsync(TimeSpan delay,
        Func<string, CancellationToken, Task> report,
        Func<TimeSpan, CancellationToken, Task> wait, CancellationToken cancellationToken)
    {
        await report("handle-refresh-waiting", cancellationToken);
        await wait(delay, cancellationToken);
        await report("handle-refresh-running", cancellationToken);
    }

    internal static async Task<ActorStore> InitializeStoreAsync(
        MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(CONCAT(DATABASE(), ':actor-handles'), 0)";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw new InvalidOperationException("Another actor updater owns this database's refresh queue.");
        }

        await MonitorSchema.InitializeAsync(connection, cancellationToken);

        return new ActorStore(connection);
    }
}