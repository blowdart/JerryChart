// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Data;

/// <summary>Contains the number of matching replies to the right Jerry in one UTC calendar month.</summary>
/// <param name="Month">The first day of the month, serialized as an ISO date.</param>
/// <param name="ReplyCount">The number of recorded matching reply posts created in that month.</param>
public sealed record MonthlyReplyCount(DateOnly Month, long ReplyCount);