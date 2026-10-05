// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Data;

/// <summary>Contains all-time counts of recorded matching reply posts, not distinct authors.</summary>
/// <param name="TotalReplies">The total number of matching reply posts.</param>
/// <param name="RightJerryReplies">The number whose immediate parent was authored by the designated Jerry DID.</param>
/// <param name="WrongJerryReplies">The number whose immediate parent was authored by any other DID.</param>
public sealed record ReplySummary(long TotalReplies, long RightJerryReplies, long WrongJerryReplies);