// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

using idunno.AtProto.Jetstream;

using JerryChart.Data;

using Microsoft.Extensions.Diagnostics.Metrics;

using MySqlConnector;

using OpenTelemetry.Metrics;

namespace JerryChart.Monitor;

internal enum EventSource
{
    Archive,
    Live
}

internal enum MetricOperation
{
    Jetstream,
    ArchiveRecord,
    ArchiveBlock,
    ReplyValidation,
    BackfillDatabase,
    BackfillRequest,
    BackfillInvocation,
    ActorRefresh
}

internal enum ErrorCategory
{
    Database,
    Http,
    RateLimited,
    Authentication,
    WebSocket,
    Jetstream,
    CursorTooOld,
    UnknownZstdDictionary,
    InvalidRequest,
    Json,
    InvalidData,
    ArchiveGenerationMismatch,
    Transport,
    Configuration,
    Timeout,
    Cancellation,
    Other
}

internal enum BackfillTrigger
{
    Scheduled,
    Manual
}

internal enum BackfillOutcome
{
    Started,
    Completed,
    Skipped,
    Canceled,
    Failed
}

internal enum BackfillResult
{
    Resolved,
    Unavailable
}

internal struct EventTags
{
    /// <summary>Gets or sets the event delivery source.</summary>
    [TagName("source")]
    public EventSource Source { get; set; }
    /// <summary>Gets or sets the Jetstream event kind.</summary>
    [TagName("event.kind")]
    public JetStreamEventKind Kind { get; set; }
}

internal struct ErrorTags
{
    /// <summary>Gets or sets the operation observing the error.</summary>
    [TagName("operation")]
    public MetricOperation Operation { get; set; }
    /// <summary>Gets or sets the bounded error category.</summary>
    [TagName("error.type")]
    public ErrorCategory Type { get; set; }
}

internal struct BackfillTags
{
    /// <summary>Gets or sets the invocation trigger.</summary>
    [TagName("trigger")]
    public BackfillTrigger Trigger { get; set; }
    /// <summary>Gets or sets the invocation outcome.</summary>
    [TagName("outcome")]
    public BackfillOutcome Outcome { get; set; }
}

internal struct BackfillResultTags
{
    /// <summary>Gets or sets the invocation trigger.</summary>
    [TagName("trigger")]
    public BackfillTrigger Trigger { get; set; }
    /// <summary>Gets or sets the persisted result category.</summary>
    [TagName("result")]
    public BackfillResult Result { get; set; }
}

internal static partial class MonitorMetricInstruments
{
    /// <summary>Creates the successfully handled event-delivery counter.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated counter.</returns>
    [Counter<long>(typeof(EventTags), Name = "jerrychart.jetstream.events.processed")]
    public static partial ProcessedEvents CreateProcessedEvents(Meter meter);

    /// <summary>Creates the event processing duration histogram.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated histogram.</returns>
    [Histogram<double>(typeof(EventTags), Name = "jerrychart.jetstream.processing.duration.seconds")]
    public static partial EventDuration CreateEventDuration(Meter meter);

    /// <summary>Creates the encountered-error counter.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated counter.</returns>
    [Counter<long>(typeof(ErrorTags), Name = "jerrychart.monitor.errors")]
    public static partial EncounteredErrors CreateEncounteredErrors(Meter meter);

    /// <summary>Creates the retry counter.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated counter.</returns>
    [Counter<long>(typeof(ErrorTags), Name = "jerrychart.monitor.retries")]
    public static partial RetryCount CreateRetryCount(Meter meter);

    /// <summary>Creates the scheduled retry delay histogram.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated histogram.</returns>
    [Histogram<double>(typeof(ErrorTags), Name = "jerrychart.monitor.retry.delay.seconds")]
    public static partial RetryDelay CreateRetryDelay(Meter meter);

    /// <summary>Creates the backfill invocation lifecycle counter.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated counter.</returns>
    [Counter<long>(typeof(BackfillTags), Name = "jerrychart.backfill.invocations")]
    public static partial BackfillInvocations CreateBackfillInvocations(Meter meter);

    /// <summary>Creates the backfill invocation duration histogram.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated histogram.</returns>
    [Histogram<double>(typeof(BackfillTags), Name = "jerrychart.backfill.duration.seconds")]
    public static partial BackfillDuration CreateBackfillDuration(Meter meter);

    /// <summary>Creates the durably saved backfill result counter.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated counter.</returns>
    [Counter<long>(typeof(BackfillResultTags), Name = "jerrychart.backfill.posts")]
    public static partial BackfillPosts CreateBackfillPosts(Meter meter);
}

internal static class MonitorMetrics
{
    internal const string MeterName = "JerryChart.Monitor";
    private static readonly Meter s_meter = new(MeterName);
    private static readonly ProcessedEvents s_events = MonitorMetricInstruments.CreateProcessedEvents(s_meter);
    private static readonly EventDuration s_eventDuration = MonitorMetricInstruments.CreateEventDuration(s_meter);
    private static readonly EncounteredErrors s_errors = MonitorMetricInstruments.CreateEncounteredErrors(s_meter);
    private static readonly RetryCount s_retries = MonitorMetricInstruments.CreateRetryCount(s_meter);
    private static readonly RetryDelay s_retryDelay = MonitorMetricInstruments.CreateRetryDelay(s_meter);
    private static readonly BackfillInvocations s_invocations = MonitorMetricInstruments.CreateBackfillInvocations(s_meter);
    private static readonly BackfillDuration s_backfillDuration = MonitorMetricInstruments.CreateBackfillDuration(s_meter);
    private static readonly BackfillPosts s_posts = MonitorMetricInstruments.CreateBackfillPosts(s_meter);
    private static Func<ArchiveReplayStatus?> s_archiveStatus = () => null;
    private static TimeProvider s_archiveClock = TimeProvider.System;
    static MonitorMetrics()
    {
        s_meter.CreateObservableGauge(
            "jerrychart.archive.stalled", () => s_archiveStatus()?.StalledSince is not null ? 1 : 0);
        s_meter.CreateObservableGauge(
            "jerrychart.archive.generation_mismatches.consecutive", () => s_archiveStatus()?.ConsecutiveGenerationMismatches ?? 0);
        s_meter.CreateObservableGauge(
            "jerrychart.archive.stall.duration.seconds", () => Age(s_archiveStatus()?.StalledSince));
        s_meter.CreateObservableGauge(
            "jerrychart.archive.no_progress.seconds", () => Age(s_archiveStatus()?.NoProgressSince));
        s_meter.CreateObservableGauge(
            "jerrychart.archive.retry.remaining.seconds", () =>
                s_archiveStatus()?.NextRetryAt is { } retry ? Math.Max(0, (retry - s_archiveClock.GetUtcNow()).TotalSeconds) : 0);
    }

    internal static void ObserveArchive(ArchiveStallTracker tracker, TimeProvider clock)
    {
        s_archiveClock = clock;
        s_archiveStatus = tracker.Snapshot;
    }

    private static double Age(DateTimeOffset? timestamp) =>
        timestamp is { } time ? Math.Max(0, (s_archiveClock.GetUtcNow() - time).TotalSeconds) : 0;

    internal static MeterProviderBuilder AddMonitorMetrics(this MeterProviderBuilder builder)
    {
        return builder.AddMeter(MeterName)
            .AddView("jerrychart.jetstream.processing.duration.seconds", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [0.0001, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 30]
            })
            .AddView("jerrychart.monitor.retry.delay.seconds", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [1, 5, 15, 30, 90, 150, 300, 600, 3600]
            })
            .AddView("jerrychart.backfill.duration.seconds", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [1, 5, 15, 30, 60, 300, 900, 3600, 21600, 86400]
            });
    }

    internal static ErrorCategory Classify(Exception exception) => exception switch
    {
        JetstreamConnectionException { ErrorDetail.Error: "CursorTooOld" } => ErrorCategory.CursorTooOld,
        JetstreamConnectionException { ErrorDetail.Error: "UnknownZstdDictionary" } => ErrorCategory.UnknownZstdDictionary,
        JetstreamConnectionException { ErrorDetail.Error: "InvalidRequest" } => ErrorCategory.InvalidRequest,
        JetstreamConnectionException { StatusCode: HttpStatusCode.TooManyRequests } => ErrorCategory.RateLimited,
        JetstreamConnectionException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => ErrorCategory.Authentication,
        JetstreamConnectionException => ErrorCategory.Jetstream,
        MySqlException => ErrorCategory.Database,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => ErrorCategory.RateLimited,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => ErrorCategory.Authentication,
        HttpRequestException => ErrorCategory.Http,
        WebSocketException => ErrorCategory.WebSocket,
        JsonException => ErrorCategory.Json,
        InvalidDataException when ArchiveStallTracker.IsGenerationMismatch(exception) => ErrorCategory.ArchiveGenerationMismatch,
        InvalidDataException => ErrorCategory.InvalidData,
        TimeoutException => ErrorCategory.Timeout,
        OperationCanceledException => ErrorCategory.Cancellation,
        ArgumentException or InvalidOperationException => ErrorCategory.Configuration,
        IOException => ErrorCategory.Transport,
        _ => ErrorCategory.Other
    };

    internal static void EventProcessed(EventSource source, JetStreamEventKind kind, long started)
    {
        var tags = new EventTags { Source = source, Kind = kind };
        s_events.Add(1, tags);
        s_eventDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
    }

    internal static void Error(MetricOperation operation, Exception exception)
    {
        Error(operation, Classify(exception));
    }

    internal static void Error(MetricOperation operation, ErrorCategory category)
    {
        s_errors.Add(1, new ErrorTags { Operation = operation, Type = category });
    }

    internal static void Retry(MetricOperation operation, Exception exception, TimeSpan delay)
    {
        var tags = new ErrorTags { Operation = operation, Type = Classify(exception) };
        s_errors.Add(1, tags);
        s_retries.Add(1, tags);
        s_retryDelay.Record(delay.TotalSeconds, tags);
    }

    internal static void Backfill(BackfillTrigger trigger, BackfillOutcome outcome, long? started = null)
    {
        var tags = new BackfillTags { Trigger = trigger, Outcome = outcome };
        s_invocations.Add(1, tags);
        if (started is long timestamp)
        {
            s_backfillDuration.Record(Stopwatch.GetElapsedTime(timestamp).TotalSeconds, tags);
        }
    }

    internal static void Posts(BackfillTrigger trigger, int resolved, int unavailable)
    {
        s_posts.Add(resolved, new BackfillResultTags { Trigger = trigger, Result = BackfillResult.Resolved });
        s_posts.Add(unavailable, new BackfillResultTags { Trigger = trigger, Result = BackfillResult.Unavailable });
    }
}
