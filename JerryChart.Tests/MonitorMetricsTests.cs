// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;

using idunno.AtProto.Jetstream;
using idunno.Security;

using JerryChart.Monitor;

using Microsoft.Extensions.Logging.Abstractions;

using MySqlConnector;

using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace JerryChart.Tests;

/// <summary>Verifies generated metric values, bounded tags, and OpenTelemetry collection.</summary>
[TestClass]
[DoNotParallelize]
public sealed class MonitorMetricsTests
{
    /// <summary>Verifies generated counters and histograms expose typed tags and seconds-valued measurements.</summary>
    [TestMethod]
    public void RecordsGeneratedInstrumentsWithTypedTags()
    {
        var readings = new List<Reading>();
        using MeterListener listener = Listen(readings);
        long started = Stopwatch.GetTimestamp();
        MonitorMetrics.EventProcessed(EventSource.Archive, JetStreamEventKind.Commit, started);
        MonitorMetrics.EventProcessed(EventSource.Live, JetStreamEventKind.Identity, started);
        MonitorMetrics.Retry(MetricOperation.Jetstream, new JsonException("Not a metric label."),
            TimeSpan.FromSeconds(15));
        MonitorMetrics.Backfill(BackfillTrigger.Scheduled, BackfillOutcome.Started);
        MonitorMetrics.Backfill(BackfillTrigger.Scheduled, BackfillOutcome.Skipped, started);
        MonitorMetrics.Posts(BackfillTrigger.Manual, 3, 2);

        Reading[] events = readings.Where(reading => reading.Name == "jerrychart.jetstream.events.processed").ToArray();
        Assert.HasCount(2, events);
        Assert.AreEqual(1d, events[0].Value);
        Assert.AreEqual("Archive", events[0].Tags["source"]);
        Assert.AreEqual("Commit", events[0].Tags["event.kind"]);
        Assert.AreEqual("Live", events[1].Tags["source"]);
        Assert.AreEqual("Identity", events[1].Tags["event.kind"]);
        Assert.HasCount(2, readings.Where(reading => reading.Name == "jerrychart.jetstream.processing.duration.seconds").ToArray());
        Assert.IsTrue(readings.Where(reading => reading.Name.EndsWith(".seconds", StringComparison.Ordinal))
            .All(reading => reading.Value >= 0 && double.IsFinite(reading.Value)));
        Assert.AreEqual(15d, readings.Single(reading => reading.Name == "jerrychart.monitor.retry.delay.seconds").Value);
        Reading retry = readings.Single(reading => reading.Name == "jerrychart.monitor.retries");
        Assert.AreEqual("Jetstream", retry.Tags["operation"]);
        Assert.AreEqual("Json", retry.Tags["error.type"]);
        Assert.AreEqual(1d, readings.Single(reading => reading.Name == "jerrychart.monitor.errors").Value);
        Assert.HasCount(2, readings.Where(reading => reading.Name == "jerrychart.backfill.invocations").ToArray());
        Assert.AreEqual("Skipped", readings.Single(reading => reading.Name == "jerrychart.backfill.duration.seconds").Tags["outcome"]);
        Assert.AreEqual(3d, readings.Single(reading => reading.Name == "jerrychart.backfill.posts" &&
            reading.Tags["result"] == "Resolved").Value);
        Assert.AreEqual(2d, readings.Single(reading => reading.Name == "jerrychart.backfill.posts" &&
            reading.Tags["result"] == "Unavailable").Value);
        Assert.DoesNotContain("Not a metric label.", readings.SelectMany(reading => reading.Tags.Values));
    }

    /// <summary>Verifies retry and archive-error paths record failures but normal shutdown does not.</summary>
    /// <returns>A task representing bounded retry execution.</returns>
    [TestMethod]
    public async Task ErrorPathsRecordCategoriesAndShutdownDoesNotRetry()
    {
        var readings = new List<Reading>();
        using MeterListener listener = Listen(readings);
        JetstreamMonitor.HandleArchiveError(123, new InvalidDataException("Invalid archive record."), NullLogger.Instance);
        JetstreamMonitor.HandleArchiveError(null, new JsonException("Invalid archive block."), NullLogger.Instance);
        using var shutdown = new CancellationTokenSource();
        await RetryLoop.RunAsync((_, _) => throw new HttpRequestException("Rate limit.", null,
            HttpStatusCode.TooManyRequests), NullLogger.Instance, shutdown.Token, (_, _) =>
            {
                shutdown.Cancel();
                return Task.CompletedTask;
            });
        Assert.HasCount(3, readings.Where(reading => reading.Name == "jerrychart.monitor.errors").ToArray());
        Assert.HasCount(1, readings.Where(reading => reading.Name == "jerrychart.monitor.retries").ToArray());
        Assert.AreEqual("RateLimited", readings.Single(reading => reading.Name == "jerrychart.monitor.retries").Tags["error.type"]);
        Assert.AreEqual(ErrorCategory.Jetstream,
            MonitorMetrics.Classify(new JetstreamConnectionException(HttpStatusCode.BadRequest, null, null)));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await RetryLoop.RunAsync((_, _) => throw new AssertFailedException("Canceled attempts must not run."),
            NullLogger.Instance, canceled.Token);
        Assert.HasCount(1, readings.Where(reading => reading.Name == "jerrychart.monitor.retries").ToArray());
    }

    /// <summary>Verifies an OpenTelemetry reader collects the application meter as an exported time series.</summary>
    [TestMethod]
    public void OpenTelemetryCollectsEventCounterAndHistogram()
    {
        using var exporter = new CaptureExporter();
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddMonitorMetrics()
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: int.MaxValue))
            .Build();
        MonitorMetrics.EventProcessed(EventSource.Live, JetStreamEventKind.Commit, Stopwatch.GetTimestamp());
        Assert.IsTrue(provider.ForceFlush());
        Assert.IsGreaterThanOrEqualTo(1, exporter.Events);
        Assert.IsGreaterThanOrEqualTo(1, exporter.DurationCount);
        Assert.AreSequenceEqual(
            new[] { 0.0001, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 30, double.PositiveInfinity }, exporter.EventDurationBounds);
    }

    /// <summary>Verifies SSRF handler detections are exported through explicit package registration.</summary>
    /// <returns>A task representing a blocked local request.</returns>
    [TestMethod]
    public async Task OpenTelemetryCollectsSsrfHandlerCounters()
    {
        using var exporter = new CaptureExporter();
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddMonitorMetrics()
            .AddSsrfHandlerMetrics()
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: int.MaxValue))
            .Build();
        using HttpClient client = MonitorHttpClients.CreateAppViewClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1/", timeout.Token));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://10.0.0.1/", timeout.Token));
        Assert.IsTrue(Ssrf.IsUnsafeIpAddress(IPAddress.Loopback, metrics: new SsrfMetrics()));
        Assert.IsTrue(provider.ForceFlush());
        Assert.IsGreaterThanOrEqualTo(1, exporter.SsrfCounters.GetValueOrDefault("idunno.security.ssrf.blocked.requests.total"));
        Assert.IsGreaterThanOrEqualTo(1, exporter.SsrfCounters.GetValueOrDefault("idunno.security.ssrf.unsafe.uri.total"));
        Assert.IsGreaterThanOrEqualTo(1, exporter.SsrfCounters.GetValueOrDefault("idunno.security.ssrf.unsafe.ip_address.total"));
    }

    /// <summary>Verifies actual invocation failures and cancellation retain their outcomes without exposing messages.</summary>
    /// <returns>A task representing client-free invocation testing.</returns>
    [TestMethod]
    public async Task BackfillFailuresAndCancellationRecordOneTerminalOutcome()
    {
        var readings = new List<Reading>();
        using MeterListener listener = Listen(readings);
        await using MySqlDataSource source = new MySqlDataSourceBuilder("Server=unused.invalid;Database=unused;User ID=unused").Build();
        var failed = new ParentUriBackfillInvocation(source, NullLogger<ParentUriBackfillInvocation>.Instance,
            TimeProvider.System, () => throw new IOException("Secret-shaped text must not be a label."));
        await Assert.ThrowsAsync<IOException>(() => failed.RunAsync(false, CancellationToken.None));
        using var shutdown = new CancellationTokenSource();
        var canceled = new ParentUriBackfillInvocation(source, NullLogger<ParentUriBackfillInvocation>.Instance,
            TimeProvider.System, () =>
            {
                shutdown.Cancel();
                throw new OperationCanceledException(shutdown.Token);
            });
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled.RunAsync(true, shutdown.Token));
        Reading[] invocations = readings.Where(reading => reading.Name == "jerrychart.backfill.invocations").ToArray();
        Assert.HasCount(4, invocations);
        Assert.AreEqual("Started", invocations[0].Tags["outcome"]);
        Assert.AreEqual("Manual", invocations[0].Tags["trigger"]);
        Assert.AreEqual("Failed", invocations[1].Tags["outcome"]);
        Assert.AreEqual("Scheduled", invocations[2].Tags["trigger"]);
        Assert.AreEqual("Canceled", invocations[3].Tags["outcome"]);
        Assert.HasCount(1, readings.Where(reading => reading.Name == "jerrychart.monitor.errors").ToArray());
        Assert.HasCount(2, readings.Where(reading => reading.Name == "jerrychart.backfill.duration.seconds").ToArray());
        Assert.DoesNotContain("Secret-shaped text must not be a label.", readings.SelectMany(reading => reading.Tags.Values));
    }

    private static MeterListener Listen(List<Reading> readings)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Name == MonitorMetrics.MeterName)
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            readings.Add(new Reading(instrument.Name, value, ConvertTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            readings.Add(new Reading(instrument.Name, value, ConvertTags(tags))));
        listener.Start();

        return listener;
    }

    private static Dictionary<string, string?> ConvertTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, string?>();
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            result.Add(tag.Key, tag.Value?.ToString());
        }

        return result;
    }

    private sealed record Reading(string Name, double Value, Dictionary<string, string?> Tags);

    private sealed class CaptureExporter : BaseExporter<Metric>
    {
        internal long Events { get; private set; }
        internal long DurationCount { get; private set; }
        internal List<double> EventDurationBounds { get; } = [];
        internal Dictionary<string, long> SsrfCounters { get; } = [];
        /// <inheritdoc/>
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                {
                    if (metric.MeterName == "idunno.Security.Ssrf")
                    {
                        SsrfCounters[metric.Name] = SsrfCounters.GetValueOrDefault(metric.Name) + point.GetSumLong();
                    }
                    if (metric.Name == "jerrychart.jetstream.events.processed")
                    {
                        Events += point.GetSumLong();
                    }
                    if (metric.Name == "jerrychart.jetstream.processing.duration.seconds")
                    {
                        DurationCount += point.GetHistogramCount();
                        EventDurationBounds.Clear();
                        foreach (HistogramBucket bucket in point.GetHistogramBuckets())
                        {
                            EventDurationBounds.Add(bucket.ExplicitBound);
                        }
                    }
                }
            }

            return ExportResult.Success;
        }
    }
}
