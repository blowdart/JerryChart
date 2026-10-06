// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using JerryChart.Data;
using JerryChart.Monitor;

namespace JerryChart.Tests;

/// <summary>Verifies delivered-event amortized measurements without archive access or database writes.</summary>
[TestClass]
public sealed class ArchiveThroughputTests
{
    /// <summary>Verifies the exact warm-up boundary includes the first download wait and counts deliveries, not sequences.</summary>
    [TestMethod]
    public void CountsActualDeliveriesOverEnumerationWallTime()
    {
        var clock = new DeliveryClock();
        var estimator = new ArchiveTimeEstimator(long.MaxValue - 1, clock);
        DateTimeOffset start = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(100));
        Assert.IsNull(estimator.MeasureThroughput(), "No delivered events means no rate.");
        for (int i = 0; i < 240; i++)
        {
            estimator.Delivered();
        }
        // Neither duplicate cursors nor huge sequence distances contribute to the delivery count.
        estimator.Measure(long.MaxValue - 1, long.MaxValue);
        clock.Advance(TimeSpan.FromSeconds(19.999));
        Assert.IsNull(estimator.MeasureThroughput());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        ArchiveReplayThroughput? measurement = estimator.MeasureThroughput();
        Assert.IsNotNull(measurement);
        Assert.AreEqual(240L, measurement.DeliveredEvents);
        Assert.AreEqual(120, measurement.WindowSeconds);
        Assert.AreEqual(2, measurement.EventsPerSecond);
        Assert.AreEqual(500_000, measurement.MicrosecondsPerEvent);
        Assert.AreEqual(start, measurement.WindowStartedAt);
        Assert.AreEqual(clock.GetUtcNow(), measurement.MeasuredAt);
    }

    /// <summary>Verifies active waits reduce the rate, stale deliveries expire exactly, and retries cannot reuse rates.</summary>
    [TestMethod]
    public void WaitingOverheadAndRetryWindowsAreIndependent()
    {
        var clock = new DeliveryClock();
        var estimator = new ArchiveTimeEstimator(0, clock);
        estimator.Delivered();
        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.AreEqual(1.0 / 120, estimator.MeasureThroughput()!.EventsPerSecond);
        clock.Advance(TimeSpan.FromSeconds(120));
        ArchiveReplayThroughput? waiting = estimator.MeasureThroughput();
        Assert.IsNotNull(waiting);
        Assert.AreEqual(1.0 / 240, waiting.EventsPerSecond);
        Assert.AreEqual(clock.GetUtcNow(), waiting.MeasuredAt);
        clock.Advance(TimeSpan.FromSeconds(359.999));
        Assert.IsNotNull(estimator.MeasureThroughput());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsNull(estimator.MeasureThroughput(), "Ten minutes without a delivery suppresses the rate.");
        estimator.Delivered();
        Assert.AreEqual(2L, estimator.MeasureThroughput()!.DeliveredEvents);
        Assert.AreEqual(600, estimator.MeasureThroughput()!.WindowSeconds, "This is an attempt average, not a rolling rate.");
        var retry = new ArchiveTimeEstimator(0, clock);
        retry.Delivered();
        Assert.IsNull(retry.MeasureThroughput());
    }

    /// <summary>Verifies duration is monotonic even when the UTC clock changes.</summary>
    [TestMethod]
    public void DurationDoesNotDependOnUtcClockAdjustments()
    {
        var clock = new DeliveryClock();
        var estimator = new ArchiveTimeEstimator(0, clock);
        estimator.Delivered();
        clock.Advance(TimeSpan.FromSeconds(120));
        clock.ShiftUtc(TimeSpan.FromHours(2));
        Assert.AreEqual(120, estimator.MeasureThroughput()!.WindowSeconds);
        clock.ShiftUtc(TimeSpan.FromHours(-4));
        Assert.AreEqual(120, estimator.MeasureThroughput()!.WindowSeconds);
    }

    /// <summary>Verifies freshness and validity at exact thresholds and across phase and attempt changes.</summary>
    [TestMethod]
    public void OnlyCurrentFreshArchiveMeasurementsArePublic()
    {
        DateTimeOffset start = new DeliveryClock().GetUtcNow();
        DateTimeOffset now = start.AddSeconds(120);
        var measurement = new ArchiveReplayThroughput(240, 120, start, now);
        var activity = new WorkerActivity("archive", "archive", true, start, start, now, null);
        Assert.AreEqual(measurement, measurement.Evaluate(activity, now.AddSeconds(59.999)));
        Assert.IsNull(measurement.Evaluate(activity, now.AddSeconds(60)));
        Assert.IsNull(measurement.Evaluate(activity, now.AddSeconds(-1)));
        foreach (string phase in new[] { "retrying", "live", "stopped", "failure" })
        {
            Assert.IsNull(measurement.Evaluate(activity with { Phase = phase, State = phase }, now));
        }
        Assert.IsNull(measurement.Evaluate(activity with { State = "stale", IsRunning = false }, now));
        Assert.IsNull(measurement.Evaluate(activity with { ChangedAt = start.AddSeconds(1) }, now));
        Assert.IsNull(measurement.Evaluate(activity with { StartedAt = start.AddSeconds(1) }, now));
        Assert.IsNull((measurement with { WindowStartedAt = now.AddSeconds(1) }).Evaluate(activity, now));
        foreach (double seconds in new[] { 119.999, 0, -1, double.NaN, double.PositiveInfinity, double.MaxValue })
        {
            Assert.IsNull((measurement with { WindowSeconds = seconds }).Evaluate(activity, now));
        }
        Assert.IsNull((measurement with { DeliveredEvents = 0 }).Evaluate(activity, now));
        Assert.IsNull((measurement with { DeliveredEvents = -1 }).Evaluate(activity, now));
    }

    /// <summary>Verifies generated checkpoint metadata supports new diagnostics, legacy progress, and return-to-archive clearing.</summary>
    [TestMethod]
    public void GeneratedCheckpointSerializationPreservesOptionalMeasurements()
    {
        DateTimeOffset now = new DeliveryClock().GetUtcNow();
        var progress = new MonitorProgress
        {
            Service = "https://example.test",
            ArchiveThroughput = new(240, 120, now, now.AddSeconds(120))
        };
        string json = JsonSerializer.Serialize(progress, ReplayJsonContext.Default.MonitorProgress);
        Assert.AreEqual(progress, JsonSerializer.Deserialize(json, ReplayJsonContext.Default.MonitorProgress));
        var legacy = JsonSerializer.Deserialize("""{"Service":"https://example.test","FormatVersion":1,"AfterSeq":0}""",
            ReplayJsonContext.Default.MonitorProgress);
        Assert.IsNotNull(legacy);
        Assert.IsNull(legacy.ArchiveThroughput);
        Assert.IsNull(progress.ReturnToArchive().ArchiveThroughput);
    }

    private sealed class DeliveryClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        internal void ShiftUtc(TimeSpan duration) => _utc += duration;
        internal void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
            _utc += duration;
        }
    }
}
