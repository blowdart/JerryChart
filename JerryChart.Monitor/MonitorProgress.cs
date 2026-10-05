// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto.Jetstream.Archive;

using JerryChart.Data;

namespace JerryChart.Monitor;

internal sealed record MonitorProgress
{
    /// <summary>Gets the Jetstream service URI associated with this checkpoint.</summary>
    public required string Service { get; init; }
    /// <summary>Gets the persisted checkpoint format version.</summary>
    public int FormatVersion { get; init; } = 1;
    /// <summary>Gets the last sequence from which archive recovery can resume.</summary>
    public long AfterSeq { get; init; }
    /// <summary>Gets the archive block checkpoint, or null when no archive block is saved.</summary>
    public SnapshotCheckpoint? ArchiveCheckpoint { get; init; }
    /// <summary>Gets the last processed live sequence, or null when not consuming live events.</summary>
    public long? LiveAfterSeq { get; init; }
    /// <summary>Gets the highest processed archive sequence saved with a checkpoint, excluding redelivery from rate estimates.</summary>
    public long? ArchiveHighWaterSeq { get; init; }
    /// <summary>Gets the approximate remaining snapshot duration measured at the last checkpoint, or null during warm-up.</summary>
    public ArchiveReplayEstimate? ArchiveEstimate { get; init; }

    internal MonitorProgress ReturnToArchive()
    {
        return this with
        {
            AfterSeq = LiveAfterSeq ?? AfterSeq,
            ArchiveCheckpoint = null,
            ArchiveHighWaterSeq = null,
            ArchiveEstimate = null,
            LiveAfterSeq = null
        };
    }
}