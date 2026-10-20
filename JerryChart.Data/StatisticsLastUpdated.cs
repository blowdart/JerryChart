// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Data;

/// <summary>Reports when the monitor last inserted a new matching reply.</summary>
/// <param name="UpdatedAt">The UTC insertion time, or <see langword="null"/> if none has been recorded.</param>
/// <param name="AtUri">The AT URI of the reply associated with the timestamp, or <see langword="null"/> when unavailable.</param>
public sealed record StatisticsLastUpdated(DateTimeOffset? UpdatedAt, string? AtUri);