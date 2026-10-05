// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using idunno.AtProto;

using JerryChart.Data;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ActorHandleUpdater(MySqlDataSource dataSource, ILogger<ActorHandleUpdater> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };

        var client = new ActorProfileClient(httpClient);
        int failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = new MySqlConnectionStringBuilder(dataSource.ConnectionString) { Pooling = false };
                await using var connection = new MySqlConnection(options.ConnectionString);
                await connection.OpenAsync(stoppingToken);
                ActorStore store = await InitializeStoreAsync(connection, stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    IReadOnlyList<ActorRefreshRequest> actors = await store.GetDueAsync(stoppingToken);
                    if (actors.Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }

                    IReadOnlyDictionary<Did, Handle?> handles = await client.GetAsync(actors, stoppingToken);
                    foreach (ActorRefreshRequest actor in actors)
                    {
                        Handle? handle = handles[actor.Did];
                        if (handle is null)
                        {
                            MonitorLog.MissingHandle(logger, actor.Did);
                        }

                        await store.SaveAsync(actor, handle, stoppingToken);
                    }

                    failures = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is MySqlException or HttpRequestException or IOException
                or JsonException or InvalidOperationException or OperationCanceledException)
            {
                failures = Math.Min(failures + 1, 7);
                TimeSpan retryDelay = RetryLoop.Delay(failures);
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