// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ActorExclusionStore(MySqlConnection connection)
{
    internal static async Task LockAsync(MySqlConnection connection, MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        // Serialize exclusion changes with hit writes. A cached block list or an unlocked existence check
        // lets an in-flight replay reinsert records after a privacy deletion has completed.
        command.CommandText = "SELECT Id FROM IngestionPrivacyLock WHERE Id = 1 FOR UPDATE";
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidDataException("The ingestion privacy lock row is missing.");
        }
    }

    internal async Task<long> ExcludeAsync(Did did, CancellationToken cancellationToken)
    {
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@did", did.ToString());
        command.CommandText = """
            INSERT INTO ExcludedActor (Did) VALUES (@did)
            ON DUPLICATE KEY UPDATE Did = ExcludedActor.Did
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "DELETE FROM Hits WHERE AuthorDid = @did OR ParentAuthorDid = @did";
        long deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            DELETE FROM ActorRefresh WHERE Did = @did;
            DELETE FROM Actor WHERE Did = @did;
            INSERT INTO StatisticsUpdate (Id, UpdatedAt) VALUES (1, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE UpdatedAt = GREATEST(UpdatedAt, UTC_TIMESTAMP(6))
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return deleted;
    }

    internal async Task<bool> IsExcludedAsync(string authorUri, Did parentDid, MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM ExcludedActor
                WHERE Did = @parent OR CONCAT('at://', Did) = SUBSTRING_INDEX(@uri, '/', 3))
            """;
        command.Parameters.AddWithValue("@parent", parentDid.ToString());
        command.Parameters.AddWithValue("@uri", authorUri);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
