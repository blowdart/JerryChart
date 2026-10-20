// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Data;

/// <summary>Reports when the monitor last inserted a new matching reply to Jerry Chen.</summary>
/// <param name="UpdatedAt">The UTC insertion time, or <see langword="null"/> when no recorded time belongs to a retained reply to Jerry Chen.</param>
/// <param name="AtUri">The AT URI of the reply associated with the timestamp, or <see langword="null"/> when unavailable.</param>
public sealed record StatisticsLastUpdated(DateTimeOffset? UpdatedAt, string? AtUri);