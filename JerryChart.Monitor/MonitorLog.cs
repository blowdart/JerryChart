// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto;

using Microsoft.Extensions.Logging;

namespace JerryChart.Monitor;

internal static partial class MonitorLog
{
    [LoggerMessage(1, LogLevel.Error, "Replay requires Jetstream__ApiKey (or _JetstreamApiKey) and a wss:// Jetstream v2 host.")]
    internal static partial void InvalidConfiguration(ILogger logger);

    [LoggerMessage(2, LogLevel.Error, "The monitor failed. Durable progress is retained; correct the error before restarting.")]
    internal static partial void MonitorFailed(ILogger logger, Exception exception);

    [LoggerMessage(3, LogLevel.Information, "Monitor stopped. The last durable checkpoint will be resumed on restart.")]
    internal static partial void MonitorStopped(ILogger logger);

    [LoggerMessage(4, LogLevel.Information, "Starting Jetstream processing attempt. Resuming saved progress: {HasProgress}.")]
    internal static partial void StartingReplay(ILogger logger, bool hasProgress);

    [LoggerMessage(6, LogLevel.Information, "Listening to live Jetstream after sequence {Sequence}.")]
    internal static partial void ListeningToLive(ILogger logger, long? sequence);

    [LoggerMessage(8, LogLevel.Warning, "Skipping invalid archive record {Sequence}. This record is omitted from monitoring statistics.")]
    internal static partial void SkippingArchiveRecord(ILogger logger, Exception exception, long sequence);

    [LoggerMessage(9, LogLevel.Warning, "Skipping invalid archive block. Undecodable data in this block is omitted from monitoring statistics.")]
    internal static partial void SkippingArchiveBlock(ILogger logger, Exception exception);

    [LoggerMessage(10, LogLevel.Information, "Matched reply {AtUri} by {AuthorDid} to {ParentAuthorDid}.")]
    internal static partial void MatchedReply(ILogger logger, AtUri atUri, Did authorDid, Did parentAuthorDid);

    [LoggerMessage(11, LogLevel.Warning, "Post {AtUri} at sequence {Sequence} has no object record.")]
    internal static partial void MissingObjectRecord(ILogger logger, AtUri atUri, long? sequence);

    [LoggerMessage(12, LogLevel.Warning, "Post {AtUri} at sequence {Sequence} has no string text.")]
    internal static partial void MissingText(ILogger logger, AtUri atUri, long? sequence);

    [LoggerMessage(13, LogLevel.Warning, "Skipping malformed matching reply {AtUri} at sequence {Sequence}.")]
    internal static partial void MalformedReply(ILogger logger, Exception exception, AtUri atUri, long? sequence);

    [LoggerMessage(14, LogLevel.Error, "Monitor attempt failed. Retrying from durable progress in {DelaySeconds} seconds.")]
    internal static partial void RetryingMonitor(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(15, LogLevel.Warning, "AppView has no usable handle for {Did}; retrying in 15 minutes.")]
    internal static partial void MissingHandle(ILogger logger, Did did);

    [LoggerMessage(16, LogLevel.Error, "Actor handle refresh failed. Retrying the durable queue in {Seconds} seconds.")]
    internal static partial void RetryingActorRefresh(ILogger logger, Exception exception, double seconds);

    [LoggerMessage(17, LogLevel.Information, "Starting the explicit parent post URI backfill.")]
    internal static partial void ParentUriBackfillStarted(ILogger logger);

    [LoggerMessage(18, LogLevel.Information, "Parent post URI backfill completed. No unattempted or retryable hits remain.")]
    internal static partial void ParentUriBackfillCompleted(ILogger logger);

    [LoggerMessage(19, LogLevel.Information, "Parent URI backfill batch completed: {Resolved} resolved, {Unavailable} unavailable.")]
    internal static partial void ParentUriBackfillBatchCompleted(ILogger logger, int resolved, int unavailable);

    [LoggerMessage(20, LogLevel.Warning, "Post {AtUri} is unavailable from the public AppView. Its parent URI will not be retried.")]
    internal static partial void ParentPostUnavailable(ILogger logger, AtUri atUri);

    [LoggerMessage(21, LogLevel.Error, "Parent URI backfill request for {BatchSize} posts failed temporarily. Retrying in {DelaySeconds} seconds.")]
    internal static partial void RetryingParentUriBackfill(ILogger logger, Exception exception, int batchSize, double delaySeconds);

    [LoggerMessage(22, LogLevel.Information, "Parent URI backfill stopped. Unresolved work and retry times are persisted for the next invocation.")]
    internal static partial void ParentUriBackfillStopped(ILogger logger);

    [LoggerMessage(23, LogLevel.Error, "Parent URI backfill failed. Correct the error before restarting.")]
    internal static partial void ParentUriBackfillFailed(ILogger logger, Exception exception);

    [LoggerMessage(24, LogLevel.Error, "Parent URI backfill requires ConnectionStrings__jerrychart.")]
    internal static partial void MissingBackfillConnectionString(ILogger logger);

    [LoggerMessage(25, LogLevel.Error, "Parent URI backfill database attempt failed temporarily. Reconnecting and resuming durable work in {DelaySeconds} seconds.")]
    internal static partial void RetryingBackfillDatabase(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(26, LogLevel.Information, "Processing worker {Resource} changed phase to {Phase}.")]
    internal static partial void ProcessingPhaseChanged(ILogger logger, string resource, string phase);

    [LoggerMessage(27, LogLevel.Error, "Could not persist {Phase} status for {Resource}. Freshness must not be assumed.")]
    internal static partial void ProcessingStatusWriteFailed(ILogger logger, Exception exception, string resource, string phase);

    [LoggerMessage(28, LogLevel.Warning, "Processing worker {Resource} lost its status ownership or resource lock; heartbeat stopped.")]
    internal static partial void ProcessingOwnershipLost(ILogger logger, string resource);

    [LoggerMessage(29, LogLevel.Error, "Processing worker {Resource} exited its attempt with phase {Phase}.")]
    internal static partial void ProcessingRunFailed(ILogger logger, Exception exception, string resource, string phase);

    [LoggerMessage(30, LogLevel.Information, "Scheduled parent-URI backfill skipped: another invocation owns the database lock.")]
    internal static partial void ScheduledParentUriBackfillSkipped(ILogger logger);

    [LoggerMessage(31, LogLevel.Information, "Startup or daily parent-URI backfill is due. Attempting the independent database lock.")]
    internal static partial void ScheduledParentUriBackfillDue(ILogger logger);

    [LoggerMessage(32, LogLevel.Information, "Parent-URI backfill runs once at startup and daily at 03:00 UTC.")]
    internal static partial void ParentUriBackfillScheduleRegistered(ILogger logger);

    [LoggerMessage(34, LogLevel.Information, "Actor {Did} has no resolved handle; relay status {AccountStatus}. Rechecking in {RefreshSeconds} seconds.")]
    internal static partial void ActorInactive(ILogger logger, Did did, string accountStatus, int refreshSeconds);

    [LoggerMessage(35, LogLevel.Information, "Excluded actor {Did}. Deleted {DeletedHits} replies and removed its identity refresh data. Future ingestion is blocked.")]
    internal static partial void ActorExcluded(ILogger logger, Did did, long deletedHits);

    [LoggerMessage(36, LogLevel.Error, "Could not exclude actor {Did}. Privacy deletion must not be assumed complete.")]
    internal static partial void ExclusionFailed(ILogger logger, Exception exception, Did did);

    [LoggerMessage(37, LogLevel.Warning, "Exclusion of actor {Did} was canceled. Verify exclusion state before assuming deletion completed.")]
    internal static partial void ExclusionCanceled(ILogger logger, Did did);

    [LoggerMessage(38, LogLevel.Warning, "Archive stalled after {NoProgressSeconds} seconds without progress and {ConsecutiveMismatches} consecutive generation mismatches. Last progress: {LastProgressAt}; next retry: {NextRetryAt}. Heartbeat is independent; durable recovery is unchanged.")]
    internal static partial void ArchiveStalled(ILogger logger, double noProgressSeconds, int consecutiveMismatches,
        string lastProgressAt, DateTimeOffset? nextRetryAt);

    [LoggerMessage(39, LogLevel.Information, "Archive recovered after {StallSeconds} seconds stalled. Successful processing or durable progress at {ProgressAt}.")]
    internal static partial void ArchiveRecovered(ILogger logger, double stallSeconds, DateTimeOffset progressAt);

    [LoggerMessage(40, LogLevel.Warning, "Archive generation mismatch repeated ({ConsecutiveMismatches} consecutive). Retrying unchanged durable progress in {DelaySeconds} seconds.")]
    internal static partial void RepeatedArchiveMismatch(ILogger logger, int consecutiveMismatches, double delaySeconds);

    [LoggerMessage(41, LogLevel.Information, "Jetstream processing started. The monitor owns the database lock and will resume durable archive or live progress.")]
    internal static partial void JetstreamProcessingStarted(ILogger logger);
}