// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.AtProto.Jetstream.Archive;
using idunno.Bluesky;

using JerryChart.Data;

using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal static class JetstreamMonitor
{
    internal static async Task RunAsync(MySqlDataSource dataSource, string apiKey, Uri service,
        ILogger logger, CancellationToken cancellationToken)
    {
        ProcessingActivity? lastActivity = null;
        var archiveStall = new ArchiveStallTracker(TimeProvider.System, logger);
        try
        {
            await RetryLoop.RunAsync((progress, token) => RunAttemptAsync(dataSource, apiKey, service,
                logger, progress, activity => lastActivity = activity, archiveStall, token), logger, cancellationToken,
                retrying: token => lastActivity?.ReportArchiveAsync(token) ?? Task.CompletedTask, archiveStall: archiveStall);
        }
        catch (Exception exception) when (!RetryLoop.IsRetryable(exception))
        {
            MonitorMetrics.Error(MetricOperation.Jetstream, exception);
            throw;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested && lastActivity is not null)
            {
                await lastActivity.FinishAsync("stopped");
            }
        }
    }

    private static async Task RunAttemptAsync(MySqlDataSource dataSource, string apiKey, Uri service,
        ILogger logger, Action processedEvent, Action<ProcessingActivity> started, ArchiveStallTracker archiveStall,
        CancellationToken cancellationToken)
    {
        // A dedicated physical connection guarantees the advisory lock is released even on cancellation or failure.
        var connectionString = new MySqlConnectionStringBuilder(dataSource.ConnectionString) { Pooling = false };

        await using MySqlConnection connection = new(connectionString.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var store = new MonitorStore(connection);
        await store.InitializeAsync(cancellationToken);
        await using ProcessingActivity activity = await ProcessingActivity.StartAsync(dataSource, logger, "monitor", "archive",
            ":jerry-no-v1", connection, TimeProvider.System, cancellationToken, archiveStall);
        started(activity);
        MonitorLog.JetstreamProcessingStarted(logger);
        await activity.ExecuteAsync(() => RetryLoop.RunAsync(
            (progress, token) => ReplayAsync(apiKey, service, logger, store, activity, archiveStall, () =>
            {
                progress();
                processedEvent();
            }, token), logger, cancellationToken,
            retrying: async token =>
            {
                await activity.ChangeAsync("retrying", token);
                await activity.ReportArchiveAsync(token);
            }, retryDatabase: false, archiveStall: archiveStall),
            "stopped", cancellationToken, retryable: RetryLoop.IsRetryable);
    }

    private static async Task ReplayAsync(string apiKey, Uri service, ILogger logger, MonitorStore store,
        ProcessingActivity activity, ArchiveStallTracker archiveStall, Action processedEvent, CancellationToken cancellationToken)
    {
        // Every retry reloads durable progress and starts a new snapshot enumeration, allowing fresh archive planning
        // after an ETag/generation mismatch. Preserve the original request and entire checkpoint; do not patch offsets
        // or checksums. See https://github.com/blowdart/idunno.Bluesky/pull/609 and README's archive recovery section.
        MonitorProgress? saved = await store.LoadProgressAsync(cancellationToken);
        MonitorProgress progress = saved ?? new MonitorProgress { Service = service.GetLeftPart(UriPartial.Authority) };
        if (progress.Service != service.GetLeftPart(UriPartial.Authority))
        {
            throw new InvalidDataException("The saved cursor belongs to a different Jetstream service. Restore the original host.");
        }

        MonitorLog.StartingReplay(logger, saved is not null);
        await activity.ReportArchiveAsync(cancellationToken, attemptStarted: true);
        // The SDK must own its HTTP client: this version rejects cross-origin archive redirects when given an
        // external factory, including the archive's HTTPS CDN redirect. Its owned client already uses
        // idunno.Security.Ssrf for HTTP and WebSocket connections, with automatic redirects disabled.
        // This preserves manual SDK redirect validation and SSRF checks on the CDN connection; do not replace
        // it with an external factory or enable automatic redirects to work around the archive policy.
        await using var jetstream = new AtProtoJetstream(uri: service, options: new JetstreamOptions
        {
            ApiKey = apiKey,
            MaximumConcurrentMessageParsers = 1
        }, collections: [new CollectionSelector(CollectionNsid.Post)]);
        jetstream.KindFilter = [JetStreamEventKind.Commit];

        var request = new SnapshotRequest
        {
            AfterSeq = progress.AfterSeq,
            Collections = [new CollectionSelector(CollectionNsid.Post)],
            Kinds = [JetStreamEventKind.Commit]
        };

        if (progress.LiveAfterSeq is null)
        {
            long lastProcessedSequence = progress.AfterSeq;
            await activity.ChangeAsync("archive", cancellationToken);
            var estimator = new ArchiveTimeEstimator(progress.ArchiveHighWaterSeq ?? progress.AfterSeq, TimeProvider.System);
            activity.ArchiveEstimator = estimator;
            await foreach (JetstreamEvent item in jetstream.SnapshotAsync(request, progress.ArchiveCheckpoint, checkpoint =>
            {
                ArchiveReplayEstimate? estimate = estimator.Measure(lastProcessedSequence, checkpoint.SealedTipSeq);
                MonitorProgress next = progress with
                {
                    ArchiveCheckpoint = checkpoint,
                    ArchiveHighWaterSeq = estimator.HighWaterSequence,
                    ArchiveEstimate = estimate,
                    ArchiveThroughput = estimator.MeasureThroughput()
                };
                // Publish in-memory progress only after persistence succeeds. A restarted segment can redeliver hits;
                // ProcessAsync commits them idempotently before enumeration advances to the next checkpoint.
                store.SaveProgress(next);
                if (HasForwardArchiveProgress(progress.ArchiveCheckpoint, checkpoint))
                {
                    archiveStall.Progress();
                }
                progress = next;
            }, cancellationToken: cancellationToken,
                onArchiveError: (sequence, exception) => HandleArchiveError(sequence, exception, logger)))
            {
                estimator.Delivered();
                long started = Stopwatch.GetTimestamp();
                await ProcessAsync(item, store, logger, cancellationToken);
                MonitorMetrics.EventProcessed(EventSource.Archive, item.Kind, started);
                if (archiveStall.Progress())
                {
                    await activity.ReportArchiveAsync(cancellationToken);
                }
                if (item.Sequence is long sequence)
                {
                    lastProcessedSequence = Math.Max(lastProcessedSequence, sequence);
                    // Sample processing, not checkpoint cadence: block downloads and planning callbacks can be bursty.
                    // Only the checkpoint callback persists this measurement, after the associated work is durable.
                    if (progress.ArchiveCheckpoint is { } archive)
                    {
                        estimator.Measure(lastProcessedSequence, archive.SealedTipSeq);
                    }
                }
                processedEvent();
            }

            long tip = progress.ArchiveCheckpoint?.SealedTipSeq
                ?? throw new InvalidDataException("Snapshot completed without a pinned sealed tip.");
            activity.ArchiveEstimator = null;
            progress = progress with
            {
                LiveAfterSeq = Math.Max(tip, progress.AfterSeq), ArchiveEstimate = null, ArchiveThroughput = null
            };
            store.SaveProgress(progress);
            archiveStall.Progress();
            await activity.ReportArchiveAsync(cancellationToken);
        }

        MonitorLog.ListeningToLive(logger, progress.LiveAfterSeq);
        await activity.ChangeAsync("live", cancellationToken);
        try
        {
            jetstream.KindFilter = [JetStreamEventKind.Commit, JetStreamEventKind.Identity, JetStreamEventKind.Account];
            await foreach (JetstreamEvent item in jetstream.StreamAsync(progress.LiveAfterSeq, maximumReconnectAttempts: 0,
                cancellationToken))
            {
                if (item.Sequence is not long sequence)
                {
                    throw new InvalidDataException("A live Jetstream event has no sequence.");
                }

                if (sequence <= progress.LiveAfterSeq)
                {
                    continue;
                }

                long started = Stopwatch.GetTimestamp();
                await ProcessAsync(item, store, logger, cancellationToken);
                MonitorProgress next = progress with { LiveAfterSeq = sequence };
                store.SaveProgress(next);
                progress = next;
                MonitorMetrics.EventProcessed(EventSource.Live, item.Kind, started);
                if (archiveStall.Progress())
                {
                    await activity.ReportArchiveAsync(cancellationToken);
                }
                processedEvent();
            }
        }
        catch (JetstreamConnectionException exception) when (exception.ErrorDetail?.Error == "CursorTooOld")
        {
            store.SaveProgress(progress.ReturnToArchive());
            throw;
        }
        catch (InvalidDataException exception) when (
            exception.Message == "The live Jetstream cursor is outside the server's retained events.")
        {
            store.SaveProgress(progress.ReturnToArchive());
            throw;
        }
    }

    internal static bool HasForwardArchiveProgress(SnapshotCheckpoint? previous, SnapshotCheckpoint next) =>
        next.PlanAfterSeq > (previous?.PlanAfterSeq ?? 0) ||
        (previous is null || next.SegmentName == previous.SegmentName && next.SegmentChecksum == previous.SegmentChecksum) &&
            (next.NextBlockIndex > (previous?.NextBlockIndex ?? 0) ||
                next.NextByteOffset > (previous?.NextByteOffset ?? 0));

    internal static JetstreamArchiveErrorAction HandleArchiveError(long? sequence, Exception exception, ILogger logger)
    {
        // Only record/block decoding failures reach this callback. ETag/generation mismatches escape enumeration
        // and must reach RetryLoop; skipping a block here is not generation recovery and intentionally loses data.
        if (sequence is long recordSequence)
        {
            MonitorMetrics.Error(MetricOperation.ArchiveRecord, exception);
            MonitorLog.SkippingArchiveRecord(logger, exception, recordSequence);

            return JetstreamArchiveErrorAction.SkipRecord;
        }

        MonitorMetrics.Error(MetricOperation.ArchiveBlock, exception);
        MonitorLog.SkippingArchiveBlock(logger, exception);

        return JetstreamArchiveErrorAction.SkipBlock;
    }

    internal static async Task ProcessAsync(JetstreamEvent item, MonitorStore store, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (item is JetstreamIdentityEvent or JetstreamAccountEvent)
        {
            await new ActorStore(store.Connection).InvalidateAsync(item.Did, cancellationToken);
            return;
        }

        if (ReplyMatcher.Match(item, logger) is { } hit &&
            await store.SaveHitAsync(hit, cancellationToken))
        {
            MonitorLog.MatchedReply(logger, hit.AtUri, hit.AuthorDid, hit.ParentAuthorDid);
        }
    }
}