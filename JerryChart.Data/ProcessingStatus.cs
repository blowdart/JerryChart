// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Data;

/// <summary>A public snapshot of worker activity, approximate archive duration, and refresh queues.</summary>
/// <param name="ObservedAt">The UTC time at which heartbeat freshness was evaluated.</param>
/// <param name="Monitor">Jetstream replay and live-monitor activity.</param>
/// <param name="ParentUriBackfill">Explicit parent-URI backfill activity and queue counts across all Hits.</param>
/// <param name="HandleRefresh">Explicit handle updater activity and aggregate refresh schedule counts.</param>
public sealed record ProcessingStatus(
    DateTimeOffset ObservedAt, MonitorProcessingStatus Monitor, ParentUriProcessingStatus ParentUriBackfill,
    HandleRefreshProcessingStatus HandleRefresh);

/// <summary>Durable worker activity. A checkpoint or outstanding work alone never proves liveness.</summary>
/// <param name="Phase">Recorded phase, or not-started when no run has been recorded.</param>
/// <param name="State">Fresh recorded phase, stale for an expired active heartbeat, or not-started.</param>
/// <param name="IsRunning">Whether an active phase has a heartbeat younger than 60 seconds.</param>
/// <param name="StartedAt">The last run's UTC start, or null.</param>
/// <param name="ChangedAt">The last phase change in UTC, or null.</param>
/// <param name="HeartbeatAt">The last successfully persisted heartbeat in UTC, or null.</param>
/// <param name="FinishedAt">The recorded terminal time in UTC, or null.</param>
public sealed record WorkerActivity(
    string Phase, string State, bool IsRunning, DateTimeOffset? StartedAt,
    DateTimeOffset? ChangedAt, DateTimeOffset? HeartbeatAt, DateTimeOffset? FinishedAt)
{
    /// <summary>Evaluates activity using an explicit clock without treating terminal runs as stale.</summary>
    /// <param name="now">The current UTC time.</param>
    /// <returns>The activity with current freshness and liveness.</returns>
    public WorkerActivity Evaluate(DateTimeOffset now)
    {
        bool active = Phase is "archive" or "live" or "backfill-running" or "retrying"
            or "handle-refresh-idle" or "handle-refresh-running" or "handle-refresh-waiting";
        bool fresh = HeartbeatAt is { } heartbeat && heartbeat <= now &&
            now - heartbeat < TimeSpan.FromSeconds(60);

        return this with { State = active && !fresh ? "stale" : Phase, IsRunning = active && fresh };
    }
}

/// <summary>Jetstream worker status without exposing cursor JSON or service credentials.</summary>
/// <param name="Activity">The explicitly recorded worker activity.</param>
/// <param name="CheckpointUpdatedAt">The last durable checkpoint write in UTC; not a heartbeat.</param>
/// <param name="ArchiveEstimate">Approximate snapshot time remaining, or null when no fresh estimate is available.</param>
/// <param name="ArchiveReplay">Last recorded archive progress and generation-stall diagnostics, independent of heartbeat freshness.</param>
public sealed record MonitorProcessingStatus(
    WorkerActivity Activity, DateTimeOffset? CheckpointUpdatedAt, ArchiveReplayEstimate? ArchiveEstimate = null,
    ArchiveReplayStatus? ArchiveReplay = null);

/// <summary>Reports archive progress and retry visibility without changing recovery or exposing SDK checkpoint details.</summary>
/// <param name="NoProgressSince">The last successful processing/progress time, or the start of observation when none succeeded.</param>
/// <param name="LastProgressAt">The last successful event handling or durable forward checkpoint progress, or null.</param>
/// <param name="StalledSince">The time the generation stall was declared, or null when not stalled.</param>
/// <param name="ConsecutiveGenerationMismatches">The consecutive precisely recognized SDK generation mismatch count.</param>
/// <param name="NextRetryAt">The scheduled retry time, or null while an attempt is underway.</param>
public sealed record ArchiveReplayStatus(
    DateTimeOffset NoProgressSince, DateTimeOffset? LastProgressAt, DateTimeOffset? StalledSince,
    int ConsecutiveGenerationMismatches, DateTimeOffset? NextRetryAt);

/// <summary>Represents a sequence-rate-based estimate for the pinned archive snapshot, not the moving live stream.</summary>
/// <param name="RemainingSeconds">The estimated seconds remaining at measurement time, not an exact completion guarantee.</param>
/// <param name="MeasuredAt">The UTC time of the forward-progress measurement.</param>
public sealed record ArchiveReplayEstimate(double RemainingSeconds, DateTimeOffset MeasuredAt)
{
    /// <summary>Specifies the maximum measurement age in seconds, allowing bursty downloads within the rate window.</summary>
    public const int MaximumAgeSeconds = 600;

    /// <summary>Suppresses estimates during retries, stale progress, or a later processing attempt.</summary>
    /// <param name="activity">The worker activity evaluated at the current time.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns>The estimate when both activity and progress are fresh; otherwise, <see langword="null"/>.</returns>
    /// <exception cref="InvalidDataException">The stored remaining duration is negative or non-finite.</exception>
    public ArchiveReplayEstimate? Evaluate(WorkerActivity activity, DateTimeOffset now)
    {
        if (!double.IsFinite(RemainingSeconds) || RemainingSeconds < 0)
        {
            throw new InvalidDataException("The stored archive time estimate is invalid.");
        }

        return activity.State == "archive" && activity.IsRunning && MeasuredAt <= now &&
            now - MeasuredAt < TimeSpan.FromSeconds(MaximumAgeSeconds) && activity.ChangedAt <= MeasuredAt &&
            activity.StartedAt <= MeasuredAt ? this : null;
    }
}

/// <summary>Parent-URI worker activity, independent of queue completeness.</summary>
/// <param name="Activity">The explicitly recorded worker activity.</param>
/// <param name="Pending">Unattempted rows (status 0), all immediately eligible.</param>
/// <param name="RetryPending">Retryable rows (status 3), including future retries.</param>
/// <param name="RetryDue">Retryable rows whose next attempt was due when the snapshot query began.</param>
/// <param name="Resolved">Rows with terminal resolved status (1), including newly ingested hits.</param>
/// <param name="Unavailable">Rows with terminal unavailable status (2), not retried.</param>
public sealed record ParentUriProcessingStatus(
    WorkerActivity Activity, long Pending, long RetryPending, long RetryDue, long Resolved, long Unavailable);

/// <summary>Reports the continuous handle updater independently of its recurring refresh schedule.</summary>
/// <param name="Activity">The explicitly recorded, ownership-fenced worker activity.</param>
/// <param name="Pending">All scheduled refresh rows, including future periodic refreshes of resolved accounts.</param>
/// <param name="Due">Scheduled rows eligible when the snapshot query began; never evidence of worker liveness.</param>
/// <param name="NextDueAt">The earliest queued eligibility time in UTC, or null for an empty queue.</param>
public sealed record HandleRefreshProcessingStatus(
    WorkerActivity Activity, long Pending, long Due, DateTimeOffset? NextDueAt);