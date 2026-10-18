// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

using idunno.AtProto;
using idunno.AtProto.Jetstream.Archive;

using JerryChart.Data;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class OperatorJobs(MySqlDataSource source, TextWriter output)
{
    internal async Task<int> AuditThreadAsync(string post, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var audit = new ThreadAudit(httpClient);
        AuditedThread thread = await audit.ReadAsync(post, cancellationToken);
        await using MySqlConnection connection = await source.OpenConnectionAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT AtUri, ParentAtUri FROM Hits
            WHERE (ParentAtUriHash = UNHEX(SHA2(@parent, 256)) AND ParentAtUri = @parent)
                OR AtUriHash IN (
                    SELECT UNHEX(SHA2(j.Uri, 256))
                    FROM JSON_TABLE(@replies, '$[*]' COLUMNS (Uri VARCHAR(8192) PATH '$')) j)
            """;
        command.Parameters.AddWithValue("@parent", thread.Uri);
        command.Parameters.AddWithValue("@replies",
            JsonSerializer.Serialize(thread.CandidateUris.ToArray(), ReplayJsonContext.Default.StringArray));
        var stored = new HashSet<string>(StringComparer.Ordinal);
        var linked = new HashSet<string>(StringComparer.Ordinal);
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                stored.Add(reader.GetString(0));
                if (!reader.IsDBNull(1) && reader.GetString(1) == thread.Uri)
                {
                    linked.Add(reader.GetString(0));
                }
            }
        }

        command.CommandText = "SELECT Did FROM ExcludedActor";
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                excluded.Add(reader.GetString(0));
            }
        }

        DateTimeOffset databaseObservedAt = DateTimeOffset.UtcNow;
        await transaction.CommitAsync(cancellationToken);
        IReadOnlyDictionary<string, VerifiedAuditPost> verified = await audit.VerifyAsync(
            stored.Concat(thread.CandidateUris), cancellationToken);
        string parentDid = new AtUri(thread.Uri).Authority.ToString();
        bool Matches(VerifiedAuditPost reply) => reply.Qualifies && reply.ParentUri == thread.Uri;
        bool IsExcluded(VerifiedAuditPost reply) => excluded.Contains(reply.AuthorDid) || excluded.Contains(parentDid);
        int confirmed = linked.Count(uri => verified.TryGetValue(uri, out VerifiedAuditPost? reply) && Matches(reply));
        int unavailable = linked.Count(uri => !verified.ContainsKey(uri));
        int missing = verified.Values.Count(reply => Matches(reply) && !stored.Contains(reply.Uri) && !IsExcluded(reply));
        await output.WriteLineAsync($"Thread: {thread.Uri}");
        await output.WriteLineAsync($"Database snapshot read completed (UTC): {databaseObservedAt:O}; public verification completed (UTC): {DateTimeOffset.UtcNow:O}.");
        await output.WriteLineAsync($"AppView reported replies: {thread.ReportedReplies}; direct replies returned by thread: {thread.Replies.Count}; blocked/missing entries: {thread.HiddenReplies}.");
        await output.WriteLineAsync($"Stored for this parent: {linked.Count}; confirmed stored matches: {confirmed}; stored now nonmatching or different parent: {linked.Count - confirmed - unavailable}; stored unavailable/unverifiable: {unavailable}.");
        await output.WriteLineAsync($"Confirmed qualifying replies absent from database and not excluded: {missing}.");
        await output.WriteLineAsync("Discovery completeness: NOT ESTABLISHED. Public thread results may be incomplete; no missing replies found does not prove an exhaustive count.");
        await output.WriteLineAsync("Read-only audit. getPosts verifies each stored/discovered URI independently. An omitted post is unavailable/unverifiable, not confirmed deleted. HTTP or malformed-response failures make the audit inconclusive and fail the command.");
        await output.WriteLineAsync("Counts retain historical matches, including replies subsequently edited or deleted. Database and public observations are not atomic.");
        var discovered = thread.CandidateUris.ToHashSet(StringComparer.Ordinal);
        foreach (string uri in stored.Concat(discovered).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!verified.TryGetValue(uri, out VerifiedAuditPost? reply))
            {
                await output.WriteLineAsync($"UNAVAILABLE/UNVERIFIABLE | {(stored.Contains(uri) ? "stored" : "discovered")} | {Printable(uri)}");
                continue;
            }

            string status = IsExcluded(reply) ? "excluded" : "absent";
            if (stored.Contains(uri))
            {
                status = linked.Contains(uri) ? "stored" : "stored, parent unresolved or different";
            }
            string discovery = discovered.Contains(uri) ? "returned by thread" : "absent from thread response";
            await output.WriteLineAsync($"{(Matches(reply) ? "MATCH" : "NO MATCH")} | {status} | {Printable(uri)} | {discovery} | {Printable(reply.Text)}");
        }

        return 0;
    }

    internal async Task<int> RefreshActorAsync(Did did, ActorProfileClient client, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await source.OpenConnectionAsync(cancellationToken);
        var store = new ActorStore(connection);
        ActorRefreshRequest request = await store.GetTrackedAsync(did, cancellationToken);
        IReadOnlyDictionary<Did, Handle?> handles = await client.GetAsync([request], cancellationToken);
        Handle? handle = handles[did];
        ActorResolution resolution = handle is not null ? new(handle, null) :
            await client.ResolveMissingAsync(did, cancellationToken);
        // Fence any updater HTTP request that began before this explicit refresh.
        if (!await store.SaveResolutionAsync(request, resolution, cancellationToken, advanceRevision: true))
        {
            throw new InvalidOperationException("The actor changed or was excluded during refresh; no stale result was saved. Retry the command.");
        }

        await output.WriteLineAsync($"Refreshed {did}: handle={resolution.Handle?.ToString() ?? "(unknown)"}; account status={resolution.AccountStatus ?? "(not reported)"}; next refresh in {resolution.RefreshSeconds} seconds.");

        return 0;
    }

    internal async Task<int> CheckConsistencyAsync(CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await source.OpenConnectionAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'Unresolved parent URIs (legacy/retry queue)', COUNT(*) FROM Hits WHERE ParentUriBackfillStatus IN (0, 3)
            UNION ALL
            SELECT 'Invalid parent URI/hash/status combinations', COUNT(*) FROM Hits
            WHERE (ParentAtUri IS NULL) <> (ParentAtUriHash IS NULL)
                OR (ParentAtUri IS NOT NULL AND ParentAtUriHash <> UNHEX(SHA2(ParentAtUri, 256)))
                OR (ParentUriBackfillStatus = 1 AND ParentAtUri IS NULL)
                OR (ParentUriBackfillStatus <> 1 AND ParentAtUri IS NOT NULL)
                OR ParentUriBackfillStatus NOT IN (0, 1, 2, 3)
            UNION ALL
            SELECT 'Invalid hit URI hashes', COUNT(*) FROM Hits WHERE AtUriHash <> UNHEX(SHA2(AtUri, 256))
            UNION ALL
            SELECT 'Hit actors missing Actor records', COUNT(*) FROM
                (SELECT AuthorDid AS Did FROM Hits UNION SELECT ParentAuthorDid FROM Hits) h
                LEFT JOIN Actor a ON a.Did = h.Did WHERE a.Did IS NULL
            UNION ALL
            SELECT 'Tracked actors missing refresh jobs', COUNT(*) FROM Actor a
                LEFT JOIN ActorRefresh r ON r.Did = a.Did WHERE r.Did IS NULL
            UNION ALL
            SELECT 'Refresh jobs missing Actor records', COUNT(*) FROM ActorRefresh r
                LEFT JOIN Actor a ON a.Did = r.Did WHERE a.Did IS NULL
            UNION ALL
            SELECT 'Excluded actors retained in Actor or ActorRefresh', COUNT(*) FROM ExcludedActor e
                WHERE EXISTS (SELECT 1 FROM Actor a WHERE a.Did = e.Did)
                    OR EXISTS (SELECT 1 FROM ActorRefresh r WHERE r.Did = e.Did)
            UNION ALL
            SELECT 'Hits involving excluded actors', COUNT(*) FROM Hits h
                WHERE EXISTS (SELECT 1 FROM ExcludedActor e WHERE e.Did = h.AuthorDid OR e.Did = h.ParentAuthorDid)
            """;
        long problems = 0;
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long count = reader.GetInt64(1);
            problems += count;
            await output.WriteLineAsync($"{reader.GetString(0)}: {count}");
        }

        await output.WriteLineAsync("Read-only check; no rows, schedules or checkpoints were changed. Unresolved legacy rows may be expected; use backfill-parent-uris to repair them.");

        return problems == 0 ? 0 : 1;
    }

    internal async Task<int> ReplayStatusAsync(CancellationToken cancellationToken)
    {
        ProcessingStatus snapshot = await new ProcessingStatusStore(source, TimeProvider.System).GetAsync(cancellationToken);
        MonitorProcessingStatus monitor = snapshot.Monitor;
        await output.WriteLineAsync($"Observed (UTC): {Time(snapshot.ObservedAt)}");
        await output.WriteLineAsync($"Monitor phase: {monitor.Activity.Phase}; state: {monitor.Activity.State}; running: {monitor.Activity.IsRunning}");
        await output.WriteLineAsync($"Heartbeat (UTC): {Time(monitor.Activity.HeartbeatAt)}");
        await output.WriteLineAsync($"Checkpoint updated (UTC): {Time(monitor.CheckpointUpdatedAt)}");
        await output.WriteLineAsync($"Last durable progress (UTC): {Time(monitor.ArchiveReplay?.LastProgressAt)}");
        await output.WriteLineAsync($"Stalled since (UTC): {Time(monitor.ArchiveReplay?.StalledSince)}");
        await output.WriteLineAsync($"Consecutive generation mismatches: {monitor.ArchiveReplay?.ConsecutiveGenerationMismatches ?? 0}");
        await output.WriteLineAsync($"Next retry (UTC): {Time(monitor.ArchiveReplay?.NextRetryAt)}");
        await using MySqlConnection connection = await source.OpenConnectionAsync(cancellationToken);
        MonitorProgress? progress = await new MonitorStore(connection).LoadProgressAsync(cancellationToken);
        if (progress is null)
        {
            await output.WriteLineAsync("No durable replay checkpoint recorded.");
        }
        else
        {
            await output.WriteLineAsync($"Archive resume sequence: {progress.AfterSeq}; live sequence: {progress.LiveAfterSeq?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}; archive high-water sequence: {progress.ArchiveHighWaterSeq?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
            if (progress.ArchiveCheckpoint is SnapshotCheckpoint checkpoint)
            {
                await output.WriteLineAsync($"Archive segment: {Printable(checkpoint.SegmentName ?? "(none)")}; next block: {checkpoint.NextBlockIndex}; next byte offset: {checkpoint.NextByteOffset}");
            }
        }

        return 0;
    }

    private static string Time(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "(not recorded)";

    private static string Printable(string value) =>
        string.Concat(value.Select(character => char.IsControl(character) ? ' ' : character));
}
