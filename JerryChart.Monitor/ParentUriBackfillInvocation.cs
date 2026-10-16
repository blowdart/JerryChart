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
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const BackfillTrigger trigger = BackfillTrigger.Manual;
        long started = Stopwatch.GetTimestamp();
        MonitorMetrics.Backfill(trigger, BackfillOutcome.Started);
        BackfillOutcome outcome = BackfillOutcome.Failed;
        try
        {
            using HttpClient httpClient = createClient?.Invoke() ?? MonitorHttpClients.CreateAppViewClient();
            var client = new ParentPostClient(httpClient);
            var backfiller = new ParentUriBackfiller(dataSource, client, logger, clock);
            await backfiller.RunAsync(cancellationToken);
            outcome = BackfillOutcome.Completed;
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