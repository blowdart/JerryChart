// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Monitor;

/// <summary>Persisted Hits.ParentUriBackfillStatus values; do not renumber existing database values.</summary>
internal enum ParentUriBackfillStatus : byte
{
    Pending = 0,
    Resolved = 1,
    Unavailable = 2,
    RetryPending = 3
}