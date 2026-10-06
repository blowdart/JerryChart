// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto.Jetstream.Archive;

using JerryChart.Data;
using JerryChart.Monitor;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JerryChart.Tests;

/// <summary>Verifies generation-stall visibility using a controllable clock without changing replay recovery.</summary>
[TestClass]
public sealed class ArchiveStallTests
{
    private const string ResumeMismatch = "The archive server did not resume the expected segment generation.";
    private const string DownloadMismatch = "Archive download ETag mismatch for segment 'segment', block 3, offset 123: planned checksum 'abc', expected ETag \"abc\", response ETag \"def\", HTTP 206. The segment may have been compacted since planning, or the download response may be missing its ETag.";

    /// <summary>Verifies only the installed SDK's precise generation messages receive the bounded category.</summary>
    [TestMethod]
    public void RecognizesOnlyKnownInvalidDataMismatchMessages()
    {
        foreach (string message in new[] { ResumeMismatch, DownloadMismatch })
        {
            Assert.IsTrue(ArchiveStallTracker.IsGenerationMismatch(new InvalidDataException(message)));
            Assert.AreEqual(ErrorCategory.ArchiveGenerationMismatch, MonitorMetrics.Classify(new InvalidDataException(message)));
            Assert.IsFalse(ArchiveStallTracker.IsGenerationMismatch(new IOException(message)));
        }
        foreach (string message in new[]
        {
            "ETag mismatch", "Archive download ETag mismatch for segment 'example'",
            DownloadMismatch + " unrelated error",
            ResumeMismatch + " unrelated error", "Snapshot completed without a pinned sealed tip.",
            "The archive returned an invalid download redirect."
        })
        {
            Assert.IsFalse(ArchiveStallTracker.IsGenerationMismatch(new InvalidDataException(message)));
            Assert.AreEqual(ErrorCategory.InvalidData, MonitorMetrics.Classify(new InvalidDataException(message)));
        }
    }

    /// <summary>Verifies both thresholds, a transition during a wait, and recovery only on successful progress.</summary>
    [TestMethod]
    public void RequiresThreeMismatchesAndFiveMinutesAndRecoversOnce()
    {
        var clock = new ProcessingTestClock();
        var logger = new TransitionLogger();
        var tracker = new ArchiveStallTracker(clock, logger);
        DateTimeOffset started = clock.GetUtcNow();
        tracker.Failure(new InvalidDataException(DownloadMismatch), TimeSpan.FromSeconds(1));
        tracker.Failure(new InvalidDataException(ResumeMismatch), TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsNull(tracker.Snapshot().StalledSince, "Elapsed time alone must not declare a stall.");
        tracker.Progress();
        for (int i = 0; i < 3; i++)
        {
            tracker.Failure(new InvalidDataException(DownloadMismatch), TimeSpan.FromSeconds(300));
        }
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        Assert.IsNull(tracker.Snapshot().StalledSince);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        DateTimeOffset stalledAt = clock.GetUtcNow();
        Assert.AreEqual(stalledAt, tracker.Snapshot().StalledSince);
        Assert.AreEqual(started.AddMinutes(5), tracker.Snapshot().LastProgressAt);
        tracker.Snapshot();
        Assert.HasCount(1, logger.Events.Where(id => id == 38).ToArray());
        tracker.AttemptStarted();
        Assert.IsNull(tracker.Snapshot().NextRetryAt);
        Assert.AreEqual(stalledAt, tracker.Snapshot().StalledSince, "Replanning is not successful processing.");
        tracker.Failure(new HttpRequestException("Unrelated failure."), TimeSpan.FromSeconds(90));
        Assert.AreEqual(0, tracker.Snapshot().ConsecutiveGenerationMismatches);
        Assert.AreEqual(stalledAt, tracker.Snapshot().StalledSince, "A different failure cannot clear the stall.");
        Assert.AreEqual(stalledAt.AddSeconds(90), tracker.Snapshot().NextRetryAt);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.IsTrue(tracker.Progress());
        Assert.IsFalse(tracker.Progress());
        Assert.IsNull(tracker.Snapshot().StalledSince);
        Assert.IsNull(tracker.Snapshot().NextRetryAt);
        Assert.AreEqual(clock.GetUtcNow(), tracker.Snapshot().LastProgressAt);
        Assert.HasCount(1, logger.Events.Where(id => id == 39).ToArray());
    }

    /// <summary>Verifies stalled state survives retry-owner replacement and process initialization.</summary>
    [TestMethod]
    public void PersistedStallIsNotClearedByInitializationOrOtherFailures()
    {
        var clock = new ProcessingTestClock();
        DateTimeOffset now = clock.GetUtcNow();
        var saved = new ArchiveReplayStatus(now.AddMinutes(-10), null, now.AddMinutes(-5), 9, now.AddMinutes(1));
        var tracker = new ArchiveStallTracker(clock, NullLogger.Instance);
        tracker.Initialize(saved);
        tracker.Initialize(null);
        tracker.AttemptStarted();
        tracker.Failure(new IOException("Connection lost."), TimeSpan.FromSeconds(1));
        Assert.AreEqual(saved.StalledSince, tracker.Snapshot().StalledSince);
        Assert.AreEqual(saved.NoProgressSince, tracker.Snapshot().NoProgressSince);
        Assert.IsNull(tracker.Snapshot().LastProgressAt);
        Assert.IsTrue(tracker.Progress());
        Assert.IsNull(tracker.Snapshot().StalledSince);
    }

    /// <summary>Verifies successful processing remains authoritative if a previously failed diagnostic read completes later.</summary>
    [TestMethod]
    public void LateInitializationDoesNotOverwriteSuccessfulProgress()
    {
        var clock = new ProcessingTestClock();
        var logger = new TransitionLogger();
        var tracker = new ArchiveStallTracker(clock, logger);
        DateTimeOffset now = clock.GetUtcNow();
        tracker.Progress();
        tracker.Initialize(new(now.AddMinutes(-10), null, now.AddMinutes(-5), 7, now.AddMinutes(1)));
        Assert.IsTrue(tracker.IsInitialized);
        Assert.IsNull(tracker.Snapshot().StalledSince);
        Assert.IsNull(tracker.Snapshot().NextRetryAt);
        Assert.AreEqual(now, tracker.Snapshot().LastProgressAt);
        Assert.HasCount(1, logger.Events.Where(id => id == 39).ToArray());
    }

    /// <summary>Verifies repeated planning, a changed generation, and rewound offsets are not durable forward progress.</summary>
    [TestMethod]
    public void DistinguishesForwardCheckpointProgressFromReplanning()
    {
        var checkpoint = new SnapshotCheckpoint
        {
            RequestFingerprint = "request", SealedTipSeq = 1000, PlanAfterSeq = 10,
            SegmentName = "segment", SegmentChecksum = "checksum", NextBlockIndex = 3, NextByteOffset = 100
        };
        Assert.IsFalse(JetstreamMonitor.HasForwardArchiveProgress(checkpoint, checkpoint));
        Assert.IsFalse(JetstreamMonitor.HasForwardArchiveProgress(checkpoint,
            checkpoint with { SegmentChecksum = "changed", NextBlockIndex = 4, NextByteOffset = 120 }));
        Assert.IsFalse(JetstreamMonitor.HasForwardArchiveProgress(checkpoint,
            checkpoint with { NextBlockIndex = 0, NextByteOffset = 0 }));
        Assert.IsTrue(JetstreamMonitor.HasForwardArchiveProgress(checkpoint, checkpoint with { NextBlockIndex = 4 }));
        Assert.IsTrue(JetstreamMonitor.HasForwardArchiveProgress(checkpoint, checkpoint with { NextByteOffset = 120 }));
        Assert.IsTrue(JetstreamMonitor.HasForwardArchiveProgress(checkpoint, checkpoint with { PlanAfterSeq = 20 }));
        Assert.IsFalse(JetstreamMonitor.HasForwardArchiveProgress(null,
            checkpoint with { PlanAfterSeq = 0, NextBlockIndex = 0, NextByteOffset = 0 }));
    }

    /// <summary>Verifies visibility preserves every retry delay and logs full traces for unrelated failures.</summary>
    /// <returns>A task representing simulated retry execution.</returns>
    [TestMethod]
    public async Task RetryScheduleIsUnchangedAndOnlyRepeatedMismatchTracesAreCoalesced()
    {
        var clock = new ProcessingTestClock();
        var logger = new TransitionLogger();
        var tracker = new ArchiveStallTracker(clock, logger);
        using var shutdown = new CancellationTokenSource();
        var delays = new List<double>();
        int attempt = 0;
        await RetryLoop.RunAsync((processed, _) =>
        {
            attempt++;
            if (attempt == 9)
            {
                tracker.Progress();
                processed();
            }
            throw attempt == 8 ? new HttpRequestException("Unrelated.") : new InvalidDataException(DownloadMismatch);
        }, logger, shutdown.Token, (delay, _) =>
        {
            delays.Add(delay.TotalSeconds);
            clock.Advance(delay);
            tracker.Snapshot();
            if (delays.Count == 9)
            {
                shutdown.Cancel();
            }

            return Task.CompletedTask;
        }, archiveStall: tracker);
        Assert.AreSequenceEqual(new double[] { 1, 5, 15, 30, 90, 150, 300, 300, 1 }, delays);
        Assert.HasCount(3, logger.Exceptions);
        Assert.IsInstanceOfType<HttpRequestException>(logger.Exceptions[1]);
        Assert.HasCount(1, logger.Events.Where(id => id == 38).ToArray());
        Assert.HasCount(1, logger.Events.Where(id => id == 39).ToArray());
    }

    private sealed class TransitionLogger : ILogger
    {
        internal List<int> Events { get; } = [];
        internal List<Exception> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Events.Add(eventId.Id);
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
    }
}
