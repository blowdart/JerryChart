// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Data;

/// <summary>Describes a parent post ranked by recorded matching replies to the right Jerry.</summary>
/// <param name="AtUri">The resolved AT URI of Jerry's parent post, not a reply URI.</param>
/// <param name="ReplyCount">The number of recorded matching replies addressed to this post.</param>
public sealed record TopReplyPost(string AtUri, long ReplyCount);
