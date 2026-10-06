// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using JerryChart.Data;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class MonitorStore(MySqlConnection connection)
{
    private const string MonitorId = "jerry-no-v1";

    internal MySqlConnection Connection => connection;

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        // This connection owns the lock until disposed, including while the replay is waiting for data.
        command.CommandText = "SELECT GET_LOCK(CONCAT(DATABASE(), ':jerry-no-v1'), 0)";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw new InvalidOperationException("Another Jerry no monitor already owns this database's replay cursor.");
        }

        await MonitorSchema.InitializeAsync(connection, cancellationToken);
        command.CommandText = "SELECT COUNT(*) FROM MonitorSchemaMigration WHERE MigrationId = 'actor-refresh-v1'";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
        {
            return;
        }

        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Actor (Did)
            SELECT AuthorDid FROM Hits UNION SELECT ParentAuthorDid FROM Hits
            ON DUPLICATE KEY UPDATE Did = Actor.Did;

            INSERT INTO ActorRefresh (Did)
            SELECT Did FROM Actor WHERE NOT EXISTS (SELECT 1 FROM ExcludedActor e WHERE e.Did = Actor.Did)
            ON DUPLICATE KEY UPDATE Did = ActorRefresh.Did;

            INSERT INTO MonitorSchemaMigration (MigrationId, AppliedAt)
            VALUES ('actor-refresh-v1', UTC_TIMESTAMP(6))
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<MonitorProgress?> LoadProgressAsync(CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT CheckpointJson FROM JetstreamReplayProgress WHERE MonitorId = @monitorId";
        command.Parameters.AddWithValue("@monitorId", MonitorId);
        object? json = await command.ExecuteScalarAsync(cancellationToken);
        if (json is null)
        {
            return null;
        }

        MonitorProgress progress = JsonSerializer.Deserialize((string)json, ReplayJsonContext.Default.MonitorProgress)
            ?? throw new InvalidDataException("The stored replay checkpoint is null.");
        if (progress.FormatVersion != 1 || string.IsNullOrWhiteSpace(progress.Service) ||
            progress.AfterSeq < 0 || progress.LiveAfterSeq < progress.AfterSeq ||
            progress.ArchiveCheckpoint is { RequestFingerprint: null or "" })
        {
            throw new InvalidDataException("The stored replay progress is invalid and cannot be resumed safely.");
        }

        return progress;
    }

    internal void SaveProgress(MonitorProgress progress)
    {
        // The SDK callback is synchronous. Do not fire-and-forget: advancing before a durable write loses progress.
        using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO JetstreamReplayProgress (MonitorId, CheckpointJson, UpdatedAt)
            VALUES (@monitorId, @checkpoint, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE CheckpointJson = @checkpoint, UpdatedAt = UTC_TIMESTAMP(6)
            """;
        command.Parameters.AddWithValue("@monitorId", MonitorId);
        command.Parameters.AddWithValue("@checkpoint",
            JsonSerializer.Serialize(progress, ReplayJsonContext.Default.MonitorProgress));

        command.ExecuteNonQuery();
    }

    internal async Task<bool> SaveHitAsync(Hit hit, CancellationToken cancellationToken)
    {
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ActorExclusionStore.LockAsync(connection, transaction, cancellationToken);
        await using (MySqlCommand exclusion = connection.CreateCommand())
        {
            exclusion.Transaction = transaction;
            exclusion.CommandText = "SELECT EXISTS (SELECT 1 FROM ExcludedActor WHERE Did IN (@author, @parent))";
            exclusion.Parameters.AddWithValue("@author", hit.AuthorDid.ToString());
            exclusion.Parameters.AddWithValue("@parent", hit.ParentAuthorDid.ToString());
            if (Convert.ToInt32(await exclusion.ExecuteScalarAsync(cancellationToken)) == 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Actor (Did, UpdatedAt)
            VALUES (@author, UTC_TIMESTAMP(6)), (@parent, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE Did = Did;

            INSERT INTO ActorRefresh (Did)
            VALUES (@author), (@parent)
            ON DUPLICATE KEY UPDATE Did = Did;

            INSERT INTO StatisticsUpdate (Id, UpdatedAt)
            SELECT 1, UTC_TIMESTAMP(6)
            WHERE NOT EXISTS (SELECT 1 FROM Hits WHERE AtUriHash = @hash)
            ON DUPLICATE KEY UPDATE UpdatedAt = GREATEST(UpdatedAt, UTC_TIMESTAMP(6));

            INSERT INTO Hits (AtUriHash, AtUri, CreatedAt, AuthorDid, ParentAuthorDid,
                ParentAtUriHash, ParentAtUri, ParentUriBackfillStatus)
            VALUES (@hash, @uri, @createdAt, @author, @parent, @parentHash, @parentUri, @status)
            ON DUPLICATE KEY UPDATE AtUriHash = AtUriHash
            """;
        string atUri = hit.AtUri.ToString();
        string parentAtUri = hit.ParentAtUri.ToString();
        command.Parameters.AddWithValue("@hash", SHA256.HashData(Encoding.UTF8.GetBytes(atUri)));
        command.Parameters.AddWithValue("@uri", atUri);
        command.Parameters.AddWithValue("@createdAt", hit.CreatedAt.UtcDateTime);
        command.Parameters.AddWithValue("@author", hit.AuthorDid.ToString());
        command.Parameters.AddWithValue("@parent", hit.ParentAuthorDid.ToString());
        command.Parameters.AddWithValue("@parentHash", SHA256.HashData(Encoding.UTF8.GetBytes(parentAtUri)));
        command.Parameters.AddWithValue("@parentUri", parentAtUri);
        command.Parameters.AddWithValue("@status", (byte)ParentUriBackfillStatus.Resolved);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }
}