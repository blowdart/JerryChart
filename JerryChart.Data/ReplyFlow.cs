// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Data;

/// <summary>Contains all-time reply totals and their right-Jerry monthly breakdown from one database snapshot.</summary>
/// <param name="Summary">The counts of all, right-Jerry, and wrong-Jerry replies.</param>
/// <param name="Months">The nonempty UTC creation months of right-Jerry replies, in chronological order.</param>
public sealed record ReplyFlow(ReplySummary Summary, IReadOnlyList<MonthlyReplyCount> Months);
