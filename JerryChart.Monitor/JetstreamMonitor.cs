// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

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
        try
        {
            await RetryLoop.RunAsync((progress, token) => RunAttemptAsync(dataSource, apiKey, service,
                logger, progress, activity => lastActivity = activity, token), logger, cancellationToken);
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
        ILogger logger, Action processedEvent, Action<ProcessingActivity> started, CancellationToken cancellationToken)
    {
        // A dedicated physical connection guarantees the advisory lock is released even on cancellation or failure.
        var connectionString = new MySqlConnectionStringBuilder(dataSource.ConnectionString) { Pooling = false };

        await using MySqlConnection connection = new(connectionString.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var store = new MonitorStore(connection);
        await store.InitializeAsync(cancellationToken);
        await using var activity = await ProcessingActivity.StartAsync(dataSource, logger, "monitor", "archive",
            ":jerry-no-v1", connection, TimeProvider.System, cancellationToken);
        started(activity);
        await activity.ExecuteAsync(() => RetryLoop.RunAsync(
            (progress, token) => ReplayAsync(apiKey, service, logger, store, activity, () =>
            {
                progress();
                processedEvent();
            }, token), logger, cancellationToken,
            retrying: token => activity.ChangeAsync("retrying", token), retryDatabase: false),
            "stopped", cancellationToken, retryable: RetryLoop.IsRetryable);
    }

    private static async Task ReplayAsync(string apiKey, Uri service, ILogger logger, MonitorStore store,
        ProcessingActivity activity, Action processedEvent, CancellationToken cancellationToken)
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
            var estimator = new ArchiveTimeEstimator(progress.ArchiveHighWaterSeq ?? progress.AfterSeq, TimeProvider.System);
            long lastProcessedSequence = progress.AfterSeq;
            await activity.ChangeAsync("archive", cancellationToken);
            await foreach (JetstreamEvent item in jetstream.SnapshotAsync(request, progress.ArchiveCheckpoint, checkpoint =>
            {
                ArchiveReplayEstimate? estimate = estimator.Measure(lastProcessedSequence, checkpoint.SealedTipSeq);
                MonitorProgress next = progress with
                {
                    ArchiveCheckpoint = checkpoint,
                    ArchiveHighWaterSeq = estimator.HighWaterSequence,
                    ArchiveEstimate = estimate
                };
                // Publish in-memory progress only after persistence succeeds. A restarted segment can redeliver hits;
                // ProcessAsync commits them idempotently before enumeration advances to the next checkpoint.
                store.SaveProgress(next);
                progress = next;
            }, cancellationToken: cancellationToken,
                onArchiveError: (sequence, exception) => HandleArchiveError(sequence, exception, logger)))
            {
                MonitorLog.ProcessingArchiveEvent(logger, item.Sequence, item.Kind);
                await ProcessAsync(item, store, logger, cancellationToken);
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
            progress = progress with { LiveAfterSeq = Math.Max(tip, progress.AfterSeq), ArchiveEstimate = null };
            store.SaveProgress(progress);
        }

        MonitorLog.ListeningToLive(logger, progress.LiveAfterSeq);
        await activity.ChangeAsync("live", cancellationToken);
        try
        {
            jetstream.KindFilter = [JetStreamEventKind.Commit, JetStreamEventKind.Identity];
            await foreach (var item in jetstream.StreamAsync(progress.LiveAfterSeq, maximumReconnectAttempts: 0,
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

                MonitorLog.ProcessingLiveEvent(logger, sequence, item.Kind);
                await ProcessAsync(item, store, logger, cancellationToken);
                var next = progress with { LiveAfterSeq = sequence };
                store.SaveProgress(next);
                progress = next;
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

    internal static JetstreamArchiveErrorAction HandleArchiveError(long? sequence, Exception exception, ILogger logger)
    {
        // Only record/block decoding failures reach this callback. ETag/generation mismatches escape enumeration
        // and must reach RetryLoop; skipping a block here is not generation recovery and intentionally loses data.
        if (sequence is long recordSequence)
        {
            MonitorLog.SkippingArchiveRecord(logger, exception, recordSequence);

            return JetstreamArchiveErrorAction.SkipRecord;
        }

        MonitorLog.SkippingArchiveBlock(logger, exception);

        return JetstreamArchiveErrorAction.SkipBlock;
    }

    internal static async Task ProcessAsync(JetstreamEvent item, MonitorStore store, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (item is JetstreamIdentityEvent)
        {
            await new ActorStore(store.Connection).InvalidateAsync(item.Did, cancellationToken);
            return;
        }

        if (ReplyMatcher.Match(item, logger) is { } hit)
        {
            await store.SaveHitAsync(hit, cancellationToken);
            MonitorLog.MatchedReply(logger, hit.AtUri, hit.AuthorDid, hit.ParentAuthorDid);
        }
    }
}