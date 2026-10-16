// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed record ActorRefreshRequest(Did Did, long Revision);

internal sealed class ActorStore(MySqlConnection connection)
{
    internal async Task<IReadOnlyList<ActorRefreshRequest>> GetDueAsync(CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Did, Revision FROM ActorRefresh
            WHERE NextAttemptAt <= UTC_TIMESTAMP(6)
            ORDER BY NextAttemptAt, Did LIMIT 25
            """;

        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var requests = new List<ActorRefreshRequest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            requests.Add(new(new Did(reader.GetString(0)), reader.GetInt64(1)));
        }

        return requests;
    }

    internal async Task InvalidateAsync(Did did, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ActorRefresh r JOIN Actor a ON a.Did = r.Did
            SET a.UpdatedAt = IF(a.Handle IS NULL, a.UpdatedAt, UTC_TIMESTAMP(6)),
                a.Handle = NULL, a.AccountStatus = NULL, r.Revision = r.Revision + 1,
                r.NextAttemptAt = LEAST(r.NextAttemptAt, UTC_TIMESTAMP(6))
            WHERE r.Did = @did
            """;
        command.Parameters.AddWithValue("@did", did.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task SaveAsync(ActorRefreshRequest request, Handle? handle, CancellationToken cancellationToken)
    {
        await SaveResolutionAsync(request, new(handle, null), cancellationToken);
    }

    internal async Task<ActorRefreshRequest> GetTrackedAsync(Did did, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.Revision FROM ActorRefresh r JOIN Actor a ON a.Did = r.Did
            WHERE r.Did = @did AND NOT EXISTS (SELECT 1 FROM ExcludedActor e WHERE e.Did = r.Did)
            """;
        command.Parameters.AddWithValue("@did", did.ToString());
        object? revision = await command.ExecuteScalarAsync(cancellationToken);

        return revision is null
            ? throw new InvalidOperationException("The actor is not tracked, has no refresh job, or is excluded.")
            : new(did, Convert.ToInt64(revision));
    }

    internal async Task<bool> SaveResolutionAsync(ActorRefreshRequest request, ActorResolution resolution,
        CancellationToken cancellationToken, bool advanceRevision = false)
    {
        await using MySqlCommand command = connection.CreateCommand();
        // A notification received during HTTP resolution must win over the stale response.
        command.CommandText = """
            UPDATE ActorRefresh r JOIN Actor a ON a.Did = r.Did
            SET a.UpdatedAt = UTC_TIMESTAMP(6),
                a.Handle = @handle,
                a.AccountStatus = @status,
                r.NextAttemptAt = TIMESTAMPADD(SECOND, @seconds, UTC_TIMESTAMP(6)),
                r.Revision = r.Revision + @advance
            WHERE r.Did = @did AND r.Revision = @revision
            """;
        command.Parameters.AddWithValue("@did", request.Did.ToString());
        command.Parameters.AddWithValue("@revision", request.Revision);
        command.Parameters.AddWithValue("@handle", resolution.Handle?.ToString());
        command.Parameters.AddWithValue("@status", resolution.AccountStatus);
        command.Parameters.AddWithValue("@seconds", resolution.RefreshSeconds);
        command.Parameters.AddWithValue("@advance", advanceRevision ? 1 : 0);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}