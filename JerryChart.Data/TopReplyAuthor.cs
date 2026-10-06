// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Data;

/// <summary>Describes an author ranked by recorded matching replies to the right Jerry.</summary>
/// <param name="Did">The author's stable DID.</param>
/// <param name="Handle">The current cached handle, or <see langword="null"/> if unknown.</param>
/// <param name="ReplyCount">The number of matching reply posts addressed to the right Jerry.</param>
/// <param name="AccountStatus">The last observed inactive relay status, or <see langword="null"/> if unknown.</param>
public sealed record TopReplyAuthor(string Did, string? Handle, long ReplyCount, string? AccountStatus = null);