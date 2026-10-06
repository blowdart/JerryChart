// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using MySqlConnector;

namespace JerryChart.Api;

internal sealed class CheckpointFreshnessSampler(
    MySqlDataSource dataSource, TimeProvider clock, ILogger<CheckpointFreshnessSampler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset? checkpoint = await ApiMetrics.QueryAsync(StatisticsReport.CheckpointFreshness,
                    () => ReadCheckpointAsync(stoppingToken), stoppingToken);
                ApiMetrics.RecordDataAge(checkpoint, clock.GetUtcNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is MySqlException or TimeoutException)
            {
                ApiLog.CheckpointSamplingFailed(logger, exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task<DateTimeOffset?> ReadCheckpointAsync(CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT UpdatedAt FROM JetstreamReplayProgress WHERE MonitorId = 'jerry-no-v1'";
        command.CommandTimeout = 10;
        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value switch
        {
            null or DBNull => null,
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => throw new InvalidDataException("The stored monitor checkpoint timestamp is invalid.")
        };
    }
}
