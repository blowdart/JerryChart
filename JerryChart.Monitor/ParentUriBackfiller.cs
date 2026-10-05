// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ParentUriBackfiller(
    MySqlDataSource dataSource,
    ParentPostClient client,
    ILogger logger,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? wait = null,
    bool skipIfLocked = false)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        wait ?? ((duration, token) => Task.Delay(duration, token));

    private ProcessingActivity? _lastActivity;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await BackfillDatabaseRetry.RunAsync(RunAttemptAsync, logger, cancellationToken, _delay);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested && _lastActivity is not null)
            {
                await _lastActivity.FinishAsync("stopped");
            }
        }
    }

    private async Task RunAttemptAsync(Action persisted, CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(dataSource.ConnectionString) { Pooling = false };
        await using var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var store = new ParentUriBackfillStore(connection);
        if (skipIfLocked)
        {
            if (!await store.TryInitializeAsync(cancellationToken))
            {
                MonitorLog.ScheduledParentUriBackfillSkipped(logger);
                return;
            }
        }
        else
        {
            await store.InitializeAsync(cancellationToken);
        }
        await using var activity = await ProcessingActivity.StartAsync(dataSource, logger, "parent-uri-backfill",
            "backfill-running", ":parent-uri-backfill", connection, _clock, cancellationToken);
        _lastActivity = activity;
        await activity.ExecuteAsync(() => BackfillAsync(store, activity, persisted, cancellationToken),
            "completed", cancellationToken);
    }

    private async Task BackfillAsync(ParentUriBackfillStore store, ProcessingActivity activity,
        Action persisted, CancellationToken cancellationToken)
    {
        MonitorLog.ParentUriBackfillStarted(logger);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ParentUriBackfillRequest> batch = await store.GetDueBatchAsync(cancellationToken);
            if (batch.Count == 0)
            {
                DateTime? nextAttempt = await store.GetNextRetryAtAsync(cancellationToken);
                if (nextAttempt is null)
                {
                    MonitorLog.ParentUriBackfillCompleted(logger);
                    return;
                }

                persisted();
                DateTimeOffset retryAt = new(DateTime.SpecifyKind(nextAttempt.Value, DateTimeKind.Utc));
                TimeSpan delay = retryAt - _clock.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    await activity.ChangeAsync("retrying", cancellationToken);
                    await _delay(delay, cancellationToken);
                }

                continue;
            }

            IReadOnlyList<ParentUriBackfillResult> results;
            try
            {
                await activity.ChangeAsync("backfill-running", cancellationToken);
                results = await client.GetAsync(batch, cancellationToken);
            }
            catch (Exception exception) when (ParentPostClient.IsTransient(exception))
            {
                TimeSpan delay = await store.SaveRetryTimesAsync(batch, client.MinimumRetryDelay, cancellationToken);
                persisted();
                MonitorLog.RetryingParentUriBackfill(logger, exception, batch.Count, delay.TotalSeconds);
                await activity.ChangeAsync("retrying", cancellationToken);
                await _delay(delay, cancellationToken);
                continue;
            }

            await store.SaveResultsAsync(results, cancellationToken);
            persisted();
            int resolved = results.Count(result => result.ParentAtUri is not null);
            int unavailable = results.Count - resolved;
            foreach (ParentUriBackfillResult result in results.Where(result => result.ParentAtUri is null))
            {
                MonitorLog.ParentPostUnavailable(logger, result.Request.AtUri);
            }

            MonitorLog.ParentUriBackfillBatchCompleted(logger, resolved, unavailable);
        }
    }
}