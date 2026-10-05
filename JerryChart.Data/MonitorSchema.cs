// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using MySqlConnector;

namespace JerryChart.Data;

/// <summary>Creates the shared monitoring tables without acquiring the replay cursor lock.</summary>
public static class MonitorSchema
{
    /// <summary>Applies the embedded, idempotent monitoring schema.</summary>
    /// <param name="connection">An open MySQL connection.</param>
    /// <param name="cancellationToken">A token to cancel initialization.</param>
    /// <returns>A task representing schema initialization.</returns>
    /// <remarks>
    /// Serializes shared schema initialization, creates durable worker activity storage, adds missing parent-URI
    /// columns and indexes to existing Hits tables, and removes the obsolete notes table.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The embedded schema is missing, or the connection is not open.</exception>
    /// <exception cref="MySqlException">Schema creation or migration fails.</exception>
    /// <exception cref="OperationCanceledException">Initialization is canceled.</exception>
    public static async Task InitializeAsync(MySqlConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(CONCAT(DATABASE(), ':monitor-schema'), 30)";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw new InvalidOperationException("Could not acquire the shared monitoring schema lock.");
        }

        try
        {
            await InitializeCoreAsync(connection, cancellationToken);
        }
        finally
        {
            command.CommandText = "SELECT RELEASE_LOCK(CONCAT(DATABASE(), ':monitor-schema'))";
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private static async Task InitializeCoreAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using var schema = typeof(MonitorSchema).Assembly.GetManifestResourceStream("JerryChart.Data.Schema.sql")
            ?? throw new InvalidOperationException("The embedded monitor database schema is missing.");
        using var reader = new StreamReader(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = await reader.ReadToEndAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureColumnAsync(connection, "ParentAtUriHash", cancellationToken);
        await EnsureColumnAsync(connection, "ParentAtUri", cancellationToken);
        await EnsureColumnAsync(connection, "ParentUriBackfillStatus", cancellationToken);
        await EnsureColumnAsync(connection, "ParentUriBackfillAttemptCount", cancellationToken);
        await EnsureColumnAsync(connection, "ParentUriBackfillNextAttemptAt", cancellationToken);
        await EnsureIndexAsync(connection, "IX_Hits_ParentAtUriHash", cancellationToken);
        await EnsureIndexAsync(connection, "IX_Hits_ParentUriBackfill", cancellationToken);
    }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Hits' AND COLUMN_NAME = @name
            """;
        command.Parameters.AddWithValue("@name", name);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            command.Parameters.Clear();
            command.CommandText = name switch
            {
                "ParentAtUriHash" => "ALTER TABLE Hits ADD COLUMN ParentAtUriHash BINARY(32) NULL",
                "ParentAtUri" => "ALTER TABLE Hits ADD COLUMN ParentAtUri VARCHAR(8192) CHARACTER SET ascii COLLATE ascii_bin NULL",
                // Persisted values: 0 Pending, 1 Resolved, 2 Unavailable, 3 RetryPending.
                "ParentUriBackfillStatus" => "ALTER TABLE Hits ADD COLUMN ParentUriBackfillStatus TINYINT UNSIGNED NOT NULL DEFAULT 0",
                "ParentUriBackfillAttemptCount" => "ALTER TABLE Hits ADD COLUMN ParentUriBackfillAttemptCount INT UNSIGNED NOT NULL DEFAULT 0",
                "ParentUriBackfillNextAttemptAt" => "ALTER TABLE Hits ADD COLUMN ParentUriBackfillNextAttemptAt DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6))",
                _ => throw new ArgumentOutOfRangeException(nameof(name))
            };
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (MySqlException exception) when (exception.Number == 1060)
            {
                if (!await ColumnExistsAsync(connection, name, cancellationToken))
                {
                    throw;
                }
            }
        }
    }

    private static async Task EnsureIndexAsync(MySqlConnection connection, string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Hits' AND INDEX_NAME = @name
            """;
        command.Parameters.AddWithValue("@name", name);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            command.Parameters.Clear();
            command.CommandText = name switch
            {
                "IX_Hits_ParentAtUriHash" => "CREATE INDEX IX_Hits_ParentAtUriHash ON Hits (ParentAtUriHash)",
                "IX_Hits_ParentUriBackfill" =>
                    "CREATE INDEX IX_Hits_ParentUriBackfill ON Hits (ParentUriBackfillStatus, ParentUriBackfillNextAttemptAt)",
                _ => throw new ArgumentOutOfRangeException(nameof(name))
            };
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (MySqlException exception) when (exception.Number == 1061)
            {
                if (!await IndexExistsAsync(connection, name, cancellationToken))
                {
                    throw;
                }
            }
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        MySqlConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Hits' AND COLUMN_NAME = @name
            """;
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<bool> IndexExistsAsync(
        MySqlConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Hits' AND INDEX_NAME = @name
            """;
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }
}