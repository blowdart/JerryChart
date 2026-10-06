// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal static class BackfillDatabaseRetry
{
    internal static async Task RunAsync(
        Func<Action, CancellationToken, Task> attempt,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task> wait,
        CancellationToken cancellationToken)
    {
        int failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await attempt(() => failures = 0, cancellationToken);
                return;
            }
            catch (MySqlException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The backfill database operation was canceled.", exception, cancellationToken);
            }
            catch (MySqlException exception) when (exception.IsTransient && !cancellationToken.IsCancellationRequested)
            {
                failures = Math.Min(failures + 1, 7);
                TimeSpan delay = RetryLoop.Delay(failures);
                MonitorMetrics.Retry(MetricOperation.BackfillDatabase, exception, delay);
                MonitorLog.RetryingBackfillDatabase(logger, exception, delay.TotalSeconds);
                await wait(delay, cancellationToken);
            }
        }
    }
}