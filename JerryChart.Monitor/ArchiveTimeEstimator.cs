// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Data;

namespace JerryChart.Monitor;

/// <summary>Estimates snapshot duration from a bounded window of forward sequence progress.</summary>
/// <param name="highWaterSequence">The highest previously persisted archive sequence.</param>
/// <param name="clock">The clock for measurement times and stalled-window detection.</param>
internal sealed class ArchiveTimeEstimator(long highWaterSequence, TimeProvider clock)
{
    private readonly Queue<(DateTimeOffset Time, long Sequence)> _samples = [];
    private DateTimeOffset? _lastAdvance;
    private ArchiveReplayEstimate? _estimate;
    /// <summary>Gets the highest processed sequence, including progress from earlier attempts.</summary>
    internal long HighWaterSequence { get; private set; } = highWaterSequence;

    /// <summary>Measures remaining duration after forward progress, excluding duplicates and stale rate windows.</summary>
    /// <param name="sequence">The highest successfully processed sequence at this checkpoint.</param>
    /// <param name="sealedTip">The pinned snapshot's final sequence.</param>
    /// <returns>An approximate duration after two minutes of fresh samples; otherwise, <see langword="null"/>.</returns>
    internal ArchiveReplayEstimate? Measure(long sequence, long sealedTip)
    {
        DateTimeOffset now = clock.GetUtcNow();
        // Download/quota waits are part of the wall-clock rate. Do not discard history between normal archive bursts.
        if (_lastAdvance is { } last && (now < last || now - last >= TimeSpan.FromSeconds(ArchiveReplayEstimate.MaximumAgeSeconds)))
        {
            _samples.Clear();
            _estimate = null;
        }

        // Redelivery after replanning is not forward progress. A new attempt also starts a new rate window.
        if (sequence >= sealedTip)
        {
            HighWaterSequence = Math.Max(HighWaterSequence, sequence);
            _estimate = null;
            return null;
        }

        if (sequence <= HighWaterSequence)
        {
            return _estimate is { } estimate && estimate.MeasuredAt <= now &&
                now - estimate.MeasuredAt < TimeSpan.FromSeconds(ArchiveReplayEstimate.MaximumAgeSeconds) ? estimate : null;
        }

        HighWaterSequence = sequence;
        _lastAdvance = now;
        while (_samples.TryPeek(out var oldest) && now - oldest.Time > TimeSpan.FromMinutes(10))
        {
            _samples.Dequeue();
        }

        var first = _samples.TryPeek(out var baseline) ? baseline : (Time: now, Sequence: sequence);
        if (_samples.Count == 0 || now - _samples.Last().Time >= TimeSpan.FromSeconds(15))
        {
            _samples.Enqueue((now, sequence));
        }

        double seconds = (now - first.Time).TotalSeconds;
        if (seconds < 120 || sequence <= first.Sequence)
        {
            return null;
        }

        // Sequence distance is a proxy for work, not an event count. Use floating point before subtracting
        // to avoid overflowing arithmetic on valid long cursors; never expose a non-finite estimate.
        double remaining = ((double)sealedTip - sequence) * seconds / ((double)sequence - first.Sequence);
        _estimate = double.IsFinite(remaining) && remaining >= 0
            ? new ArchiveReplayEstimate(remaining, now) : null;
        return _estimate;
    }
}