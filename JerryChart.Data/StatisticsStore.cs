// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto;

using MySqlConnector;

namespace JerryChart.Data;

/// <summary>Queries statistics from durable, deduplicated monitoring hits.</summary>
/// <param name="dataSource">The Aspire-configured MySQL connection pool.</param>
/// <param name="timeProvider">The clock used to determine UTC calendar-month boundaries.</param>
public sealed class StatisticsStore(MySqlDataSource dataSource, TimeProvider timeProvider)
{
    /// <summary>Gets the DID that identifies the intended Jerry.</summary>
    public static readonly Did RightJerryDid = new("did:plc:vc7f4oafdgxsihk4cry2xpze");

    /// <summary>Gets up to five resolved parent posts receiving matching replies to the right Jerry.</summary>
    /// <param name="cancellationToken">A token to cancel the database operation.</param>
    /// <returns>Recorded all-time counts ordered by count descending, then parent URI ascending.</returns>
    /// <exception cref="MySqlException">The database connection or query fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The data source has been disposed.</exception>
    public async Task<IReadOnlyList<TopReplyPost>> GetTopRightJerryPostsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ParentAtUri, COUNT(*) AS ReplyCount
            FROM Hits
            WHERE ParentAuthorDid = @rightJerry AND ParentAtUri IS NOT NULL
            GROUP BY ParentAtUriHash, ParentAtUri
            ORDER BY ReplyCount DESC, ParentAtUri ASC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@rightJerry", RightJerryDid.ToString());
        command.Parameters.AddWithValue("@limit", 5);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var posts = new List<TopReplyPost>();
        while (await reader.ReadAsync(cancellationToken))
        {
            posts.Add(new TopReplyPost(reader.GetString(0), reader.GetInt64(1)));
        }

        return posts;
    }

    /// <summary>Gets monthly counts from the first recorded right-Jerry reply through the current UTC month.</summary>
    /// <param name="cancellationToken">A token to cancel the database operation.</param>
    /// <returns>Chronological monthly counts including zero months, or an empty list when no eligible hits exist.</returns>
    /// <exception cref="MySqlException">The database connection or query fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The data source has been disposed.</exception>
    public async Task<IReadOnlyList<MonthlyReplyCount>> GetAllTimeRightJerryRepliesAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT YEAR(CreatedAt), MONTH(CreatedAt), COUNT(*)
            FROM Hits
            WHERE ParentAuthorDid = @rightJerry AND CreatedAt <= @now
            GROUP BY YEAR(CreatedAt), MONTH(CreatedAt)
            ORDER BY YEAR(CreatedAt), MONTH(CreatedAt)
            """;
        command.Parameters.AddWithValue("@rightJerry", RightJerryDid.ToString());
        command.Parameters.AddWithValue("@now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<DateOnly, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new DateOnly(reader.GetInt32(0), reader.GetInt32(1), 1), reader.GetInt64(2));
        }

        if (counts.Count == 0)
        {
            return [];
        }

        var start = counts.Keys.Min();
        var length = (now.Year - start.Year) * 12 + now.Month - start.Month + 1;

        return Enumerable.Range(0, length)
            .Select(index => start.AddMonths(index))
            .Select(month => new MonthlyReplyCount(month, counts.GetValueOrDefault(month)))
            .ToArray();
    }

    /// <summary>Gets the persisted ingestion time of the most recent new hit, excluding duplicate deliveries.</summary>
    /// <param name="cancellationToken">A token to cancel the database operation.</param>
    /// <returns>The UTC timestamp, or an unknown timestamp before the first tracked insertion.</returns>
    /// <exception cref="MySqlException">The database connection or query fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The data source has been disposed.</exception>
    public async Task<StatisticsLastUpdated> GetLastUpdatedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT UpdatedAt FROM StatisticsUpdate WHERE Id = 1";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        DateTimeOffset? updatedAt = value is DateTime date
            ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc))
            : null;

        return new StatisticsLastUpdated(updatedAt);
    }

    /// <summary>Gets six UTC monthly counts, including the current month through the present instant.</summary>
    /// <param name="cancellationToken">A token to cancel the database operation.</param>
    /// <returns>Exactly six months in chronological order, with zero counts for months without hits.</returns>
    /// <exception cref="MySqlException">The database connection or query fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The data source has been disposed.</exception>
    public async Task<IReadOnlyList<MonthlyReplyCount>> GetMonthlyRightJerryRepliesAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-5);
        var counts = new long[6];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT YEAR(CreatedAt), MONTH(CreatedAt), COUNT(*)
            FROM Hits
            WHERE ParentAuthorDid = @rightJerry AND CreatedAt >= @start AND CreatedAt <= @now
            GROUP BY YEAR(CreatedAt), MONTH(CreatedAt)
            ORDER BY YEAR(CreatedAt), MONTH(CreatedAt)
            """;
        command.Parameters.AddWithValue("@rightJerry", RightJerryDid.ToString());
        command.Parameters.AddWithValue("@start", start);
        command.Parameters.AddWithValue("@now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var index = (reader.GetInt32(0) - start.Year) * 12 + reader.GetInt32(1) - start.Month;
            counts[index] = reader.GetInt64(2);
        }

        return Enumerable.Range(0, 6)
            .Select(index => new MonthlyReplyCount(DateOnly.FromDateTime(start.AddMonths(index)), counts[index]))
            .ToArray();
    }

    /// <summary>Gets up to ten authors ordered by matching replies to the right Jerry, most replies first.</summary>
    /// <param name="cancellationToken">A token to cancel the database operation.</param>
    /// <returns>All-time counts with current cached handles; equal counts are ordered by DID.</returns>
    /// <exception cref="MySqlException">The database connection or query fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The data source has been disposed.</exception>
    public Task<IReadOnlyList<TopReplyAuthor>> GetTopRightJerryAuthorsAsync(
        CancellationToken cancellationToken = default)
    {
        return GetRightJerryAuthorsAsync(10, cancellationToken);
    }

    /// <summary>Gets every author who has recorded matching replies to the right Jerry.</summary>
    /// <param name="cancellationToken">A token to cancel the database operation.</param>
    /// <returns>All-time counts with current cached handles, ordered by count descending and DID ascending.</returns>
    /// <exception cref="MySqlException">The database connection or query fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The data source has been disposed.</exception>
    public Task<IReadOnlyList<TopReplyAuthor>> GetAllRightJerryAuthorsAsync(
        CancellationToken cancellationToken = default)
    {
        return GetRightJerryAuthorsAsync(null, cancellationToken);
    }

    private async Task<IReadOnlyList<TopReplyAuthor>> GetRightJerryAuthorsAsync(
        int? limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ranked.AuthorDid, actor.Handle, ranked.ReplyCount, actor.AccountStatus
            FROM (
                SELECT AuthorDid, COUNT(*) AS ReplyCount
                FROM Hits
                WHERE ParentAuthorDid = @rightJerry
                GROUP BY AuthorDid
                ORDER BY ReplyCount DESC, AuthorDid ASC
            ) AS ranked
            LEFT JOIN Actor AS actor ON actor.Did = ranked.AuthorDid
            ORDER BY ranked.ReplyCount DESC, ranked.AuthorDid ASC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@rightJerry", RightJerryDid.ToString());
        command.Parameters.AddWithValue("@limit", limit.HasValue ? (ulong)limit.Value : ulong.MaxValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var authors = new List<TopReplyAuthor>();
        while (await reader.ReadAsync(cancellationToken))
        {
            authors.Add(new TopReplyAuthor(reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return authors;
    }

    /// <summary>Gets all-time total, right-Jerry, and wrong-Jerry reply counts from one database snapshot.</summary>
    /// <param name="cancellationToken">A token to cancel the database operation.</param>
    /// <returns>The summary, with zero counts when no hits exist.</returns>
    /// <exception cref="MySqlException">The database connection or query fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The data source has been disposed.</exception>
    /// <exception cref="InvalidDataException">The database returns no aggregate row.</exception>
    public async Task<ReplySummary> GetReplySummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COUNT(CASE WHEN ParentAuthorDid = @rightJerry THEN 1 END)
            FROM Hits
            """;
        command.Parameters.AddWithValue("@rightJerry", RightJerryDid.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("The reply summary query returned no aggregate row.");
        }

        var total = reader.GetInt64(0);
        var rightJerry = reader.GetInt64(1);

        return new ReplySummary(total, rightJerry, total - rightJerry);
    }
}