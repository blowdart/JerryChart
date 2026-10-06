// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ParentUriBackfillInvocation(
    MySqlDataSource dataSource,
    ILogger<ParentUriBackfillInvocation> logger,
    TimeProvider clock,
    Func<HttpClient>? createClient = null)
{
    internal async Task RunAsync(bool scheduled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BackfillTrigger trigger = scheduled ? BackfillTrigger.Scheduled : BackfillTrigger.Manual;
        long started = Stopwatch.GetTimestamp();
        MonitorMetrics.Backfill(trigger, BackfillOutcome.Started);
        BackfillOutcome outcome = BackfillOutcome.Failed;
        try
        {
            using HttpClient httpClient = createClient?.Invoke() ?? MonitorHttpClients.CreateAppViewClient();
            var client = new ParentPostClient(httpClient);
            var backfiller = new ParentUriBackfiller(dataSource, client, logger, clock, skipIfLocked: scheduled);
            await backfiller.RunAsync(cancellationToken);
            outcome = backfiller.Skipped ? BackfillOutcome.Skipped : BackfillOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = BackfillOutcome.Canceled;
            throw;
        }
        catch (Exception exception)
        {
            MonitorMetrics.Error(MetricOperation.BackfillInvocation, exception);
            throw;
        }
        finally
        {
            MonitorMetrics.Backfill(trigger, outcome, started);
        }
    }
}