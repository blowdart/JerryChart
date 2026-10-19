// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Monitor;

/// <summary>Bounds live checkpoint writes while allowing an unsaved tail to be replayed safely.</summary>
internal sealed class LiveCheckpoint(TimeProvider timeProvider, Action<MonitorProgress> save)
{
    internal const int EventLimit = 1_000;
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private long _savedAt = timeProvider.GetTimestamp();
    private int _pendingEvents;

    // Call only after all effects of this event are durable. Never flush from a failure/finally path:
    // the caller may have failed partway through processing the next event.
    internal bool Processed(MonitorProgress progress)
    {
        _pendingEvents++;
        if (_pendingEvents < EventLimit && timeProvider.GetElapsedTime(_savedAt) < Interval)
        {
            return false;
        }

        save(progress);
        _pendingEvents = 0;
        _savedAt = timeProvider.GetTimestamp();

        return true;
    }
}
