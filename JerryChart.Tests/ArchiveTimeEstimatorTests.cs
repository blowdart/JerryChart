// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using JerryChart.Data;
using JerryChart.Monitor;

namespace JerryChart.Tests;

/// <summary>Verifies approximate archive duration from forward sequence progress, not event counts.</summary>
[TestClass]
public sealed class ArchiveTimeEstimatorTests
{
    /// <summary>Verifies the exact warm-up boundary, rate calculation, and bounded rolling window.</summary>
    [TestMethod]
    public void WarmsUpForTwoMinutesAndUsesRecentSequenceRate()
    {
        var clock = new ProcessingTestClock();
        var estimator = new ArchiveTimeEstimator(0, clock);
        Assert.IsNull(estimator.Measure(100, 10000));
        ArchiveReplayEstimate? estimate = null;
        for (int i = 1; i <= 24; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            estimate = estimator.Measure(100 + i * 100, 10000);
            if (i < 4)
            {
                Assert.IsNull(estimate);
            }
            else
            {
                Assert.IsNotNull(estimate);
                Assert.AreEqual((10000 - (100 + i * 100)) * 0.3, estimate.RemainingSeconds, 0.001);
            }
        }
        Assert.AreEqual(clock.GetUtcNow(), estimate!.MeasuredAt);
    }

    /// <summary>Verifies duplicates, pauses, retries, and snapshot completion cannot invent advancing progress.</summary>
    [TestMethod]
    public void RedeliveryAndPausesDoNotProduceAnEstimate()
    {
        var clock = new ProcessingTestClock();
        var estimator = new ArchiveTimeEstimator(1000, clock);
        Assert.IsNull(estimator.Measure(500, 10000));
        Assert.AreEqual(1000, estimator.HighWaterSequence);
        Assert.IsNull(estimator.Measure(1001, 10000));
        for (int i = 1; i <= 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            estimator.Measure(1001 + i * 100, 10000);
        }
        ArchiveReplayEstimate? measured = estimator.Measure(1401, 10000);
        Assert.IsNotNull(measured);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(measured, estimator.Measure(1401, 10000),
            "A repeated checkpoint preserves the estimate without refreshing its measurement time.");
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsNull(estimator.Measure(1500, 10000), "A stalled window must warm up again.");
        var retry = new ArchiveTimeEstimator(estimator.HighWaterSequence, clock);
        Assert.IsNull(retry.Measure(1200, 10000));
        Assert.IsNull(retry.Measure(1600, 10000), "A new attempt cannot reuse the previous rate.");
        Assert.IsNull(retry.Measure(10000, 10000));
        Assert.IsNull(retry.Measure(10001, 10000));
    }

    /// <summary>Verifies a changing rate drops samples outside the ten-minute window.</summary>
    [TestMethod]
    public void RollingWindowDropsOldSequenceRates()
    {
        var clock = new ProcessingTestClock();
        var estimator = new ArchiveTimeEstimator(0, clock);
        estimator.Measure(100, 10000);
        for (int i = 1; i <= 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            estimator.Measure(100 + i * 100, 10000);
        }

        clock.Advance(TimeSpan.FromSeconds(30));
        ArchiveReplayEstimate? estimate = estimator.Measure(3100, 10000);
        Assert.IsNotNull(estimate);
        Assert.AreEqual(6900 * 600.0 / 2900, estimate.RemainingSeconds, 0.001,
            "The sample at t=0 must be discarded; the oldest remaining sample is t=30, sequence 200.");
    }

    /// <summary>Verifies steady processing warms up even when checkpoints occur more than a minute apart.</summary>
    [TestMethod]
    public void EventSamplingSurvivesSparseAndRepeatedCheckpoints()
    {
        var clock = new ProcessingTestClock();
        var estimator = new ArchiveTimeEstimator(0, clock);
        estimator.Measure(100, 10000);
        for (int i = 1; i <= 120; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            estimator.Measure(100 + i * 10, 10000);
        }

        ArchiveReplayEstimate? checkpointEstimate = estimator.Measure(1300, 10000);
        Assert.IsNotNull(checkpointEstimate);
        Assert.AreEqual(870, checkpointEstimate.RemainingSeconds, 0.001);
        Assert.AreEqual(checkpointEstimate, estimator.Measure(1300, 10000));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsNull(estimator.Measure(1300, 10000));
    }

    /// <summary>Verifies two-minute archive bursts warm up and include download waits in the rate.</summary>
    [TestMethod]
    public void TwoMinuteBurstsProduceAndRetainAnEstimateBetweenCheckpoints()
    {
        var clock = new ProcessingTestClock();
        var estimator = new ArchiveTimeEstimator(1000, clock);
        DateTimeOffset started = clock.GetUtcNow();
        Assert.IsNull(estimator.Measure(1100, 10000));
        clock.Advance(TimeSpan.FromSeconds(123));
        ArchiveReplayEstimate? estimate = estimator.Measure(2100, 10000);
        Assert.IsNotNull(estimate);
        Assert.AreEqual(7900 * 123.0 / 1000, estimate.RemainingSeconds, 0.001);
        clock.Advance(TimeSpan.FromSeconds(123));
        var worker = new WorkerActivity("archive", "archive", true, started, started, clock.GetUtcNow(), null);
        Assert.AreEqual(estimate, estimate.Evaluate(worker.Evaluate(clock.GetUtcNow()), clock.GetUtcNow()),
            "A fresh heartbeat permits the previous checkpoint estimate during a normal download wait.");
        Assert.AreEqual(estimate, estimator.Measure(2100, 10000),
            "Repeated callbacks must not refresh or remove an unexpired measurement.");
        estimate = estimator.Measure(3100, 10000);
        Assert.IsNotNull(estimate);
        Assert.AreEqual(6900 * 246.0 / 2000, estimate.RemainingSeconds, 0.001);
        clock.Advance(TimeSpan.FromSeconds(599));
        Assert.AreEqual(estimate, estimator.Measure(3100, 10000));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNull(estimator.Measure(3100, 10000));
        Assert.IsNull(estimator.Measure(4100, 10000), "Ten minutes without forward progress restarts warm-up.");
    }

    /// <summary>Verifies estimates are hidden on stale progress, retries, restarts, and non-archive phases.</summary>
    [TestMethod]
    public void StatusRequiresFreshProgressInTheCurrentArchiveAttempt()
    {
        var clock = new ProcessingTestClock();
        DateTimeOffset now = clock.GetUtcNow();
        var estimate = new ArchiveReplayEstimate(120, now);
        var worker = new WorkerActivity("archive", "archive", true, now, now, now, null);
        Assert.AreEqual(estimate, estimate.Evaluate(worker, now.AddSeconds(599)));
        Assert.IsNull(estimate.Evaluate(worker, now.AddSeconds(600)));
        Assert.IsNull(estimate.Evaluate(worker, now.AddSeconds(-1)));
        Assert.IsNull(estimate.Evaluate(worker with { State = "retrying" }, now));
        Assert.IsNull(estimate.Evaluate(worker with { State = "live" }, now));
        Assert.IsNull(estimate.Evaluate(worker with { State = "stale", IsRunning = false }, now));
        Assert.IsNull(estimate.Evaluate(worker with { ChangedAt = now.AddSeconds(1) }, now.AddSeconds(1)));
        Assert.IsNull(estimate.Evaluate(worker with { StartedAt = now.AddSeconds(1) }, now.AddSeconds(1)));
        Assert.ThrowsExactly<InvalidDataException>(() => (estimate with { RemainingSeconds = double.NaN }).Evaluate(worker, now));
    }

    /// <summary>Verifies optional estimation metadata survives checkpoints and does not invalidate legacy progress.</summary>
    [TestMethod]
    public void ProgressSerializationPreservesEstimateAndSupportsLegacyCheckpoints()
    {
        var progress = new MonitorProgress
        {
            Service = "https://example.test",
            ArchiveHighWaterSeq = 1000,
            ArchiveEstimate = new(123, new ProcessingTestClock().GetUtcNow())
        };
        string json = JsonSerializer.Serialize(progress, ReplayJsonContext.Default.MonitorProgress);
        Assert.AreEqual(progress, JsonSerializer.Deserialize(json, ReplayJsonContext.Default.MonitorProgress));
        var legacy = JsonSerializer.Deserialize("""{"Service":"https://example.test","FormatVersion":1,"AfterSeq":0}""",
            ReplayJsonContext.Default.MonitorProgress);
        Assert.IsNotNull(legacy);
        Assert.IsNull(legacy.ArchiveEstimate);
        Assert.IsNull(legacy.ArchiveHighWaterSeq);
        Assert.IsNull(progress.ReturnToArchive().ArchiveEstimate);
        Assert.IsNull(progress.ReturnToArchive().ArchiveHighWaterSeq);
    }
}