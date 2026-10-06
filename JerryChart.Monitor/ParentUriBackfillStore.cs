// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

using idunno.AtProto;

using JerryChart.Data;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ParentUriBackfillStore(MySqlConnection connection)
{
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!await TryInitializeAsync(cancellationToken))
        {
            throw new InvalidOperationException("Another parent-URI backfill already owns this database.");
        }
    }

    internal async Task<bool> TryInitializeAsync(CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // The caller owns this non-pooled connection; disposing it releases the session lock.
        command.CommandText = "SELECT GET_LOCK(CONCAT(DATABASE(), ':parent-uri-backfill'), 0)";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            return false;
        }

        await MonitorSchema.InitializeAsync(connection, cancellationToken);

        return true;
    }

    internal async Task<IReadOnlyList<ParentUriBackfillRequest>> GetDueBatchAsync(
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AtUri, ParentAuthorDid, ParentUriBackfillAttemptCount
            FROM Hits
            WHERE ParentUriBackfillStatus = @pending
                OR (ParentUriBackfillStatus = @retryPending AND ParentUriBackfillNextAttemptAt <= UTC_TIMESTAMP(6))
            ORDER BY ParentUriBackfillStatus, ParentUriBackfillAttemptCount, CreatedAt, AtUriHash
            LIMIT 25
            """;
        AddPendingParameters(command);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var requests = new List<ParentUriBackfillRequest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new(new AtUri(reader.GetString(0)), new Did(reader.GetString(1)), reader.GetInt32(2)));
        }

        return requests;
    }

    internal async Task<DateTime?> GetNextRetryAtAsync(CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(ParentUriBackfillNextAttemptAt)
            FROM Hits
            WHERE ParentUriBackfillStatus = @retryPending
            """;
        command.Parameters.AddWithValue("@retryPending", (byte)ParentUriBackfillStatus.RetryPending);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DateTime nextAttempt ? nextAttempt : null;
    }

    internal async Task<TimeSpan> SaveRetryTimesAsync(
        IReadOnlyList<ParentUriBackfillRequest> requests,
        TimeSpan minimumDelay,
        CancellationToken cancellationToken)
    {
        TimeSpan shortestDelay = TimeSpan.MaxValue;
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ActorExclusionStore.LockAsync(connection, transaction, cancellationToken);
        foreach (ParentUriBackfillRequest request in requests)
        {
            if (await new ActorExclusionStore(connection).IsExcludedAsync(
                request.AtUri.ToString(), request.ParentAuthorDid, transaction, cancellationToken))
            {
                // An exclusion can delete a row while its HTTP lookup is in flight. Do not restore it or
                // mistake the intentional deletion for a database failure that retries the entire batch.
                continue;
            }
            int failureCount = request.AttemptCount == int.MaxValue
                ? int.MaxValue
                : request.AttemptCount + 1;
            TimeSpan retryDelay = RetryLoop.Delay(failureCount);
            TimeSpan delay = retryDelay > minimumDelay ? retryDelay : minimumDelay;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Hits
                SET ParentUriBackfillStatus = @retryPending,
                    ParentUriBackfillAttemptCount = LEAST(ParentUriBackfillAttemptCount + 1, 2147483647),
                    ParentUriBackfillNextAttemptAt = TIMESTAMPADD(SECOND, @seconds, UTC_TIMESTAMP(6))
                WHERE AtUriHash = @hash AND ParentAuthorDid = @parentDid
                    AND ParentUriBackfillStatus IN (@pending, @retryPending)
                """;
            AddPendingParameters(command);
            AddIdentityParameters(command, request);
            command.Parameters.AddWithValue("@seconds", delay.TotalSeconds);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException($"Could not persist the retry state for {request.AtUri}.");
            }

            shortestDelay = delay < shortestDelay ? delay : shortestDelay;
        }

        await transaction.CommitAsync(cancellationToken);

        return shortestDelay == TimeSpan.MaxValue ? minimumDelay : shortestDelay;
    }

    internal async Task SaveResultsAsync(
        IReadOnlyList<ParentUriBackfillResult> results,
        CancellationToken cancellationToken)
    {
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ActorExclusionStore.LockAsync(connection, transaction, cancellationToken);
        foreach (ParentUriBackfillResult result in results)
        {
            if (await new ActorExclusionStore(connection).IsExcludedAsync(
                result.Request.AtUri.ToString(), result.Request.ParentAuthorDid, transaction, cancellationToken))
            {
                continue;
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = result.ParentAtUri is null
                ? """
                    UPDATE Hits SET ParentUriBackfillStatus = @status
                    WHERE AtUriHash = @hash AND ParentAuthorDid = @parentDid
                        AND ParentUriBackfillStatus IN (@pending, @retryPending)
                    """
                : """
                    UPDATE Hits
                    SET ParentAtUriHash = @parentHash, ParentAtUri = @parentUri,
                        ParentUriBackfillStatus = @status, ParentUriBackfillAttemptCount = 0
                    WHERE AtUriHash = @hash AND ParentAuthorDid = @parentDid
                        AND ParentUriBackfillStatus IN (@pending, @retryPending)
                    """;
            AddPendingParameters(command);
            AddIdentityParameters(command, result.Request);
            command.Parameters.AddWithValue("@status", (byte)(result.ParentAtUri is null
                ? ParentUriBackfillStatus.Unavailable : ParentUriBackfillStatus.Resolved));
            if (result.ParentAtUri is { } parentAtUri)
            {
                string uri = parentAtUri.ToString();
                command.Parameters.AddWithValue("@parentHash", SHA256.HashData(Encoding.UTF8.GetBytes(uri)));
                command.Parameters.AddWithValue("@parentUri", uri);
            }

            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException($"Could not persist the backfill result for {result.Request.AtUri}.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void AddPendingParameters(MySqlCommand command)
    {
        command.Parameters.AddWithValue("@pending", (byte)ParentUriBackfillStatus.Pending);
        command.Parameters.AddWithValue("@retryPending", (byte)ParentUriBackfillStatus.RetryPending);
    }

    private static void AddIdentityParameters(MySqlCommand command, ParentUriBackfillRequest request)
    {
        command.Parameters.AddWithValue("@hash", SHA256.HashData(Encoding.UTF8.GetBytes(request.AtUri.ToString())));
        command.Parameters.AddWithValue("@parentDid", request.ParentAuthorDid.ToString());
    }
}