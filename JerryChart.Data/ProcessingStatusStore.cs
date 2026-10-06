// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

using MySqlConnector;

namespace JerryChart.Data;

/// <summary>Reads explicit processing status and persists ownership-fenced worker heartbeats.</summary>
/// <param name="dataSource">The shared MySQL pool. Each operation opens its own connection.</param>
/// <param name="timeProvider">The UTC clock used for writes, retry eligibility, and freshness.</param>
public sealed class ProcessingStatusStore(MySqlDataSource dataSource, TimeProvider timeProvider)
{
    /// <summary>Gets explicit worker status, parent-URI counts, and aggregate handle refresh eligibility.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An uncached status snapshot. Missing runs are not-started, never inferred from queue rows.</returns>
    /// <exception cref="MySqlException">The database connection or query fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The data source has been disposed.</exception>
    public async Task<ProcessingStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Resource, Phase, StartedAt, ChangedAt, HeartbeatAt, FinishedAt FROM ProcessingActivity;
            SELECT UpdatedAt,
                JSON_UNQUOTE(JSON_EXTRACT(CheckpointJson, '$.ArchiveEstimate.MeasuredAt')),
                CAST(JSON_UNQUOTE(JSON_EXTRACT(CheckpointJson, '$.ArchiveEstimate.RemainingSeconds')) AS DOUBLE)
            FROM JetstreamReplayProgress WHERE MonitorId = 'jerry-no-v1';
            SELECT NoProgressSince, LastProgressAt, StalledSince, ConsecutiveGenerationMismatches, NextRetryAt
            FROM ArchiveReplayActivity WHERE Resource = 'monitor';
            SELECT COALESCE(SUM(ParentUriBackfillStatus = 0), 0),
                COALESCE(SUM(ParentUriBackfillStatus = 3), 0),
                COALESCE(SUM(ParentUriBackfillStatus = 3 AND ParentUriBackfillNextAttemptAt <= @now), 0),
                COALESCE(SUM(ParentUriBackfillStatus = 1), 0),
                COALESCE(SUM(ParentUriBackfillStatus = 2), 0) FROM Hits;
            SELECT COUNT(*), COALESCE(SUM(NextAttemptAt <= @now), 0), MIN(NextAttemptAt) FROM ActorRefresh;
            """;
        command.Parameters.AddWithValue("@now", now.UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        WorkerActivity monitor = NotStarted();
        WorkerActivity parent = NotStarted();
        WorkerActivity handles = NotStarted();
        while (await reader.ReadAsync(cancellationToken))
        {
            var activity = new WorkerActivity(reader.GetString(1), reader.GetString(1), false,
                Utc(reader, 2), Utc(reader, 3), Utc(reader, 4), Utc(reader, 5));
            switch (reader.GetString(0))
            {
                case "monitor":
                    monitor = activity;
                    break;
                case "parent-uri-backfill":
                    parent = activity;
                    break;
                case "handle-refresh":
                    handles = activity;
                    break;
            }
        }

        await reader.NextResultAsync(cancellationToken);
        DateTimeOffset? checkpoint = null;
        ArchiveReplayEstimate? estimate = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            checkpoint = Utc(reader, 0);
            if (!reader.IsDBNull(1) && !reader.IsDBNull(2))
            {
                estimate = new(reader.GetDouble(2),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
            }
        }
        await reader.NextResultAsync(cancellationToken);
        ArchiveReplayStatus? archiveReplay = await reader.ReadAsync(cancellationToken) ? ReadArchiveReplay(reader) : null;
        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var parentQueue = new ParentUriProcessingStatus(parent,
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        now = timeProvider.GetUtcNow();
        WorkerActivity evaluatedMonitor = monitor.Evaluate(now);
        return new(now, new(evaluatedMonitor, checkpoint,
            archiveReplay?.StalledSince is null ? estimate?.Evaluate(evaluatedMonitor, now) : null, archiveReplay),
            parentQueue with { Activity = parent.Evaluate(now) },
            new(handles.Evaluate(now), reader.GetInt64(0), reader.GetInt64(1), Utc(reader, 2)));
    }

    internal async Task<ArchiveReplayStatus?> LoadArchiveReplayAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT NoProgressSince, LastProgressAt, StalledSince, ConsecutiveGenerationMismatches, NextRetryAt
            FROM ArchiveReplayActivity WHERE Resource = 'monitor'
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? ReadArchiveReplay(reader) : null;
    }

    internal async Task ArchiveReplayAsync(string resource, string runId, ArchiveReplayStatus status,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            INSERT INTO ArchiveReplayActivity
                (Resource, NoProgressSince, LastProgressAt, StalledSince, ConsecutiveGenerationMismatches, NextRetryAt)
            SELECT @resource, @since, @progress, @stalled, @mismatches, @retry
            FROM ProcessingActivity
            WHERE Resource = @resource AND RunId = @run AND FinishedAt IS NULL
            ON DUPLICATE KEY UPDATE NoProgressSince = @since, LastProgressAt = @progress,
                StalledSince = @stalled, ConsecutiveGenerationMismatches = @mismatches, NextRetryAt = @retry
            """;
        command.Parameters.AddWithValue("@resource", resource);
        command.Parameters.AddWithValue("@run", runId);
        command.Parameters.AddWithValue("@since", status.NoProgressSince.UtcDateTime);
        command.Parameters.AddWithValue("@progress", status.LastProgressAt?.UtcDateTime);
        command.Parameters.AddWithValue("@stalled", status.StalledSince?.UtcDateTime);
        command.Parameters.AddWithValue("@mismatches", status.ConsecutiveGenerationMismatches);
        command.Parameters.AddWithValue("@retry", status.NextRetryAt?.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ArchiveReplayStatus ReadArchiveReplay(MySqlDataReader reader) =>
        new(Utc(reader, 0)!.Value, Utc(reader, 1), Utc(reader, 2), reader.GetInt32(3), Utc(reader, 4));

    internal Task<bool> StartAsync(string resource, string runId, string phase, string lockName,
        int connectionId, CancellationToken cancellationToken) =>
        WriteAsync("""
            INSERT INTO ProcessingActivity (Resource, RunId, Phase, StartedAt, ChangedAt, HeartbeatAt)
            SELECT @resource, @run, @phase, @now, @now, @now
            WHERE IS_USED_LOCK(CONCAT(DATABASE(), @lock)) = @connection
            ON DUPLICATE KEY UPDATE RunId = @run, Phase = @phase, StartedAt = @now,
                ChangedAt = @now, HeartbeatAt = @now, FinishedAt = NULL
            """, resource, runId, phase, lockName, connectionId, cancellationToken);

    internal Task<bool> HeartbeatAsync(string resource, string runId, string lockName,
        int connectionId, CancellationToken cancellationToken) =>
        WriteAsync("""
            UPDATE ProcessingActivity SET HeartbeatAt = @now
            WHERE Resource = @resource AND RunId = @run AND FinishedAt IS NULL
                AND IS_USED_LOCK(CONCAT(DATABASE(), @lock)) = @connection
            """, resource, runId, null, lockName, connectionId, cancellationToken);

    internal Task<bool> ChangeAsync(string resource, string runId, string phase, bool finished,
        CancellationToken cancellationToken) =>
        WriteAsync(finished ? """
            UPDATE ProcessingActivity SET ChangedAt = IF(Phase <> @phase, @now, ChangedAt),
                Phase = @phase, FinishedAt = COALESCE(FinishedAt, @now)
            WHERE Resource = @resource AND RunId = @run
            """ : """
            UPDATE ProcessingActivity SET ChangedAt = IF(Phase <> @phase, @now, ChangedAt), Phase = @phase
            WHERE Resource = @resource AND RunId = @run AND FinishedAt IS NULL
            """, resource, runId, phase, null, 0, cancellationToken, finished);

    private async Task<bool> WriteAsync(string sql, string resource, string runId, string? phase,
        string? lockName, int connectionId, CancellationToken cancellationToken, bool finished = false)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("@resource", resource);
        command.Parameters.AddWithValue("@run", runId);
        command.Parameters.AddWithValue("@phase", phase);
        command.Parameters.AddWithValue("@lock", lockName);
        command.Parameters.AddWithValue("@connection", connectionId);
        command.Parameters.AddWithValue("@now", timeProvider.GetUtcNow().UtcDateTime);
        command.Parameters.AddWithValue("@finished", finished);

        if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
        {
            return true;
        }

        // UseAffectedRows=true reports zero for an unchanged phase or identical clock tick.
        // Check the fenced postcondition rather than mistaking a no-op for lost ownership.
        command.CommandText = (phase, lockName) switch
        {
            (null, _) => """
                SELECT COUNT(*) FROM ProcessingActivity
                WHERE Resource = @resource AND RunId = @run AND FinishedAt IS NULL AND HeartbeatAt = @now
                    AND IS_USED_LOCK(CONCAT(DATABASE(), @lock)) = @connection
                """,
            (_, not null) => """
                SELECT COUNT(*) FROM ProcessingActivity
                WHERE Resource = @resource AND RunId = @run AND Phase = @phase AND StartedAt = @now
                    AND FinishedAt IS NULL AND IS_USED_LOCK(CONCAT(DATABASE(), @lock)) = @connection
                """,
            _ => """
                SELECT COUNT(*) FROM ProcessingActivity
                WHERE Resource = @resource AND RunId = @run AND Phase = @phase
                    AND ((@finished AND FinishedAt IS NOT NULL) OR (NOT @finished AND FinishedAt IS NULL))
                """
        };

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static WorkerActivity NotStarted() => new("not-started", "not-started", false, null, null, null, null);

    private static DateTimeOffset? Utc(MySqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}