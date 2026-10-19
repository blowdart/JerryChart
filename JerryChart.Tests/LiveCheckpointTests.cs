// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Monitor;

using Microsoft.Extensions.Time.Testing;

namespace JerryChart.Tests;

/// <summary>Verifies bounded live checkpoint writes and replay of the unsaved tail.</summary>
[TestClass]
public sealed class LiveCheckpointTests
{
    /// <summary>Verifies the event threshold saves only the latest processed sequence in each batch.</summary>
    [TestMethod]
    public void EventLimitReducesWritesAndLeavesTailForReplay()
    {
        var clock = new FakeTimeProvider();
        var saved = new List<MonitorProgress>();
        var checkpoint = new LiveCheckpoint(clock, saved.Add);
        for (int sequence = 1; sequence <= 2_499; sequence++)
        {
            checkpoint.Processed(Progress(sequence));
        }

        Assert.AreSequenceEqual(new long?[] { 1_000, 2_000 }, saved.Select(item => item.LiveAfterSeq).ToArray());
        Assert.AreSequenceEqual(saved.Select(item => DateTimeOffset.UnixEpoch.AddSeconds(item.LiveAfterSeq!.Value)).ToArray(),
            saved.Select(item => item.LastLiveEventAt!.Value).ToArray(),
            "Each durable cursor must carry the corresponding event time, not the checkpoint clock.");
        var restarted = new LiveCheckpoint(clock, saved.Add);
        for (long sequence = saved[^1].LiveAfterSeq!.Value + 1; sequence <= 3_000; sequence++)
        {
            restarted.Processed(Progress(sequence));
        }

        Assert.AreEqual(3_000L, saved[^1].LiveAfterSeq);
        Assert.HasCount(3, saved);
    }

    /// <summary>Verifies the time threshold is checked at event boundaries and resets after successful saves.</summary>
    [TestMethod]
    public void IntervalSavesAtNextProcessedEvent()
    {
        var clock = new FakeTimeProvider();
        var saved = new List<MonitorProgress>();
        var checkpoint = new LiveCheckpoint(clock, saved.Add);
        clock.Advance(LiveCheckpoint.Interval - TimeSpan.FromTicks(1));
        Assert.IsFalse(checkpoint.Processed(Progress(1)));
        Assert.IsEmpty(saved);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.IsTrue(checkpoint.Processed(Progress(2)));
        Assert.AreEqual(2L, saved.Single().LiveAfterSeq);
        Assert.IsFalse(checkpoint.Processed(Progress(3)));
        Assert.HasCount(1, saved);
        clock.Advance(LiveCheckpoint.Interval);
        Assert.IsTrue(checkpoint.Processed(Progress(4)));
        Assert.AreEqual(4L, saved[^1].LiveAfterSeq);
    }

    /// <summary>Verifies failed persistence is propagated without resetting the pending checkpoint boundary.</summary>
    [TestMethod]
    public void FailedSaveDoesNotResetBatch()
    {
        var clock = new FakeTimeProvider();
        bool fail = true;
        MonitorProgress? saved = null;
        var checkpoint = new LiveCheckpoint(clock, progress =>
        {
            if (fail)
            {
                throw new IOException("Checkpoint unavailable.");
            }

            saved = progress;
        });
        for (int sequence = 1; sequence < LiveCheckpoint.EventLimit; sequence++)
        {
            checkpoint.Processed(Progress(sequence));
        }

        Assert.Throws<IOException>(() => checkpoint.Processed(Progress(LiveCheckpoint.EventLimit)));
        Assert.IsNull(saved);
        fail = false;
        Assert.IsTrue(checkpoint.Processed(Progress(LiveCheckpoint.EventLimit + 1)));
        Assert.AreEqual(LiveCheckpoint.EventLimit + 1L, saved?.LiveAfterSeq);
    }

    private static MonitorProgress Progress(long sequence) => new()
    {
        Service = "wss://jetstream.us-west.bsky.network",
        LiveAfterSeq = sequence,
        LastLiveEventAt = DateTimeOffset.UnixEpoch.AddSeconds(sequence)
    };
}
