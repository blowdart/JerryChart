// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.Metrics;

using JerryChart.Api;

using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace JerryChart.Tests;

/// <summary>Verifies source-generated API metrics and report execution semantics.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ApiMetricsTests
{
    /// <summary>Verifies successful list queries record duration and actual size without changing the result.</summary>
    /// <returns>A task representing the measured query.</returns>
    [TestMethod]
    public async Task SuccessfulQueriesRecordDurationAndResultSize()
    {
        var readings = new List<Reading>();
        using MeterListener listener = Listen(readings);
        int[] expected = [1, 2, 3];
        int[] actual = await ApiMetrics.QueryAsync(StatisticsReport.Authors,
            () => Task.FromResult(expected), CancellationToken.None, result => result.Length);
        Assert.AreSame(expected, actual);
        await ApiMetrics.QueryAsync(StatisticsReport.TopPosts,
            () => Task.FromResult(Array.Empty<int>()), CancellationToken.None, result => result.Length);
        await ApiMetrics.QueryAsync(StatisticsReport.Summary, () => Task.FromResult(42), CancellationToken.None);

        Assert.HasCount(3, readings.Where(r => r.Name.EndsWith("duration.seconds", StringComparison.Ordinal)).ToArray());
        Assert.IsTrue(readings.All(r => r.Value >= 0 && double.IsFinite(r.Value)));
        Reading[] sizes = readings.Where(r => r.Name.EndsWith("result.items", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(2, sizes);
        Assert.AreEqual(3d, sizes[0].Value);
        Assert.AreEqual("Authors", sizes[0].Tags["report"]);
        Assert.AreEqual(0d, sizes[1].Value);
        Assert.DoesNotContain(r => r.Name.EndsWith("query.errors", StringComparison.Ordinal), readings);
    }

    /// <summary>Verifies failures retain their exceptions and normal request cancellation is not an error.</summary>
    /// <returns>A task representing failure and cancellation checks.</returns>
    [TestMethod]
    public async Task FailuresRecordBoundedErrorsAndCancellationOnlyRecordsDuration()
    {
        var readings = new List<Reading>();
        using MeterListener listener = Listen(readings);
        var failure = new TimeoutException("Must not become a tag.");
        TimeoutException caught = await Assert.ThrowsAsync<TimeoutException>(() => ApiMetrics.QueryAsync<int>(
            StatisticsReport.TopAuthors, () => Task.FromException<int>(failure), CancellationToken.None));
        Assert.AreSame(failure, caught);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ApiMetrics.QueryAsync<int>(
            StatisticsReport.Authors, () => Task.FromCanceled<int>(canceled.Token), canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => ApiMetrics.QueryAsync<int>(
            StatisticsReport.MonthlyReplies, () => Task.FromException<int>(new OperationCanceledException()),
            CancellationToken.None));

        Reading[] errors = readings.Where(r => r.Name.EndsWith("query.errors", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(2, errors);
        Assert.AreEqual("Timeout", errors[0].Tags["error.type"]);
        Assert.AreEqual("Cancellation", errors[1].Tags["error.type"]);
        Assert.HasCount(3, readings.Where(r => r.Name.EndsWith("duration.seconds", StringComparison.Ordinal)).ToArray());
        Assert.DoesNotContain(r => r.Name.EndsWith("result.items", StringComparison.Ordinal), readings);
        Assert.DoesNotContain(failure.Message, readings.SelectMany(r => r.Tags.Values));
    }

    /// <summary>Verifies missing checkpoints emit nothing and future timestamps cannot produce negative ages.</summary>
    [TestMethod]
    public void FreshnessMeasuresCheckpointAgeWithoutInventingMissingData()
    {
        var readings = new List<Reading>();
        using MeterListener listener = Listen(readings);
        DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        ApiMetrics.RecordDataAge(null, now);
        Assert.IsEmpty(readings);
        ApiMetrics.RecordDataAge(now.AddSeconds(-120), now);
        ApiMetrics.RecordDataAge(now.AddMinutes(1), now);
        Assert.HasCount(2, readings);
        Assert.AreEqual(120d, readings[0].Value);
        Assert.AreEqual(0d, readings[1].Value);
        Assert.IsTrue(readings.All(r => r.Tags.Count == 0));
    }

    private static readonly int[] s_exportedPosts = [1];

    /// <summary>Verifies OpenTelemetry exports the generated gauge and query histogram.</summary>
    /// <returns>A task representing the measured query.</returns>
    [TestMethod]
    public async Task OpenTelemetryExportsApiMetrics()
    {
        using var exporter = new CaptureExporter();
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddApiMetrics()
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: int.MaxValue))
            .Build();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ApiMetrics.RecordDataAge(now.AddSeconds(-60), now);
        await ApiMetrics.QueryAsync(StatisticsReport.TopPosts, () => Task.FromResult(s_exportedPosts),
            CancellationToken.None, result => result.Length);
        Assert.IsTrue(provider.ForceFlush());
        Assert.AreEqual(60d, exporter.Age);
        Assert.IsGreaterThanOrEqualTo(1, exporter.DurationCount);
        Assert.AreSequenceEqual(new[] { 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 15, 30, double.PositiveInfinity }, exporter.DurationBounds);
    }

    private static MeterListener Listen(List<Reading> readings)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Name == ApiMetrics.MeterName)
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
        internal double? Age { get; private set; }
        internal long DurationCount { get; private set; }
        internal List<double> DurationBounds { get; } = [];

        /// <inheritdoc/>
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                {
                    if (metric.Name == "jerrychart.api.statistics.data.age.seconds")
                    {
                        Age = point.GetGaugeLastValueDouble();
                    }
                    if (metric.Name == "jerrychart.api.statistics.query.duration.seconds")
                    {
                        DurationCount += point.GetHistogramCount();
                        DurationBounds.Clear();
                        foreach (HistogramBucket bucket in point.GetHistogramBuckets())
                        {
                            DurationBounds.Add(bucket.ExplicitBound);
                        }
                    }
                }
            }

            return ExportResult.Success;
        }
    }
}
