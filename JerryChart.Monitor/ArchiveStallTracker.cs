// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Data;

using Microsoft.Extensions.Logging;

namespace JerryChart.Monitor;

internal sealed class ArchiveStallTracker(TimeProvider clock, ILogger logger)
{
    private readonly object _gate = new();
    private ArchiveReplayStatus _status = new(clock.GetUtcNow(), null, null, 0, null);
    private bool _initialized;

    internal bool IsInitialized
    {
        get
        {
            lock (_gate)
            {
                return _initialized;
            }
        }
    }

    internal static bool IsGenerationMismatch(Exception exception) =>
        exception is InvalidDataException &&
        (exception.Message == "The archive server did not resume the expected segment generation." ||
            exception.Message.StartsWith("Archive download ETag mismatch for segment '", StringComparison.Ordinal) &&
            exception.Message.Contains(": planned checksum '", StringComparison.Ordinal) &&
            exception.Message.Contains(", expected ETag ", StringComparison.Ordinal) &&
            exception.Message.Contains("response ETag ", StringComparison.Ordinal) &&
            exception.Message.EndsWith(". The segment may have been compacted since planning, or the download response may be missing its ETag.",
                StringComparison.Ordinal));

    internal void Initialize(ArchiveReplayStatus? saved)
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                if (_status.LastProgressAt is null)
                {
                    _status = saved ?? _status;
                }
                else if (saved?.StalledSince is { } stalled)
                {
                    MonitorLog.ArchiveRecovered(logger, (clock.GetUtcNow() - stalled).TotalSeconds,
                        _status.LastProgressAt.Value);
                }
                _initialized = true;
            }
        }
    }

    internal ArchiveReplayStatus Snapshot()
    {
        lock (_gate)
        {
            Evaluate();

            return _status;
        }
    }

    internal bool Failure(Exception exception, TimeSpan delay)
    {
        lock (_gate)
        {
            bool mismatch = IsGenerationMismatch(exception);
            int previous = _status.ConsecutiveGenerationMismatches;
            _status = _status with
            {
                ConsecutiveGenerationMismatches = mismatch ? (int)Math.Min((long)previous + 1, int.MaxValue) : 0,
                NextRetryAt = clock.GetUtcNow() + delay
            };
            Evaluate();

            return mismatch && previous > 0;
        }
    }

    internal void AttemptStarted()
    {
        lock (_gate)
        {
            _status = _status with { NextRetryAt = null };
        }
    }

    internal bool Progress()
    {
        lock (_gate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            bool recovered = _status.StalledSince is not null;
            if (_status.StalledSince is { } stalled)
            {
                MonitorLog.ArchiveRecovered(logger, (now - stalled).TotalSeconds, now);
            }
            _status = new(now, now, null, 0, null);

            return recovered;
        }
    }

    private void Evaluate()
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (_status.StalledSince is null && _status.ConsecutiveGenerationMismatches >= 3 &&
            now - _status.NoProgressSince >= TimeSpan.FromMinutes(5))
        {
            _status = _status with { StalledSince = now };
            MonitorLog.ArchiveStalled(logger, (now - _status.NoProgressSince).TotalSeconds,
                _status.ConsecutiveGenerationMismatches,
                _status.LastProgressAt?.ToString("O") ?? "No successful processing or checkpoint advancement recorded by the stall tracker yet",
                _status.NextRetryAt);
        }
    }
}
