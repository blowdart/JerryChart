// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using idunno.AtProto;

namespace JerryChart.Monitor;

internal sealed record AuditedReply(string Uri, string Text, bool Qualifies, string AuthorDid);

internal sealed record VerifiedAuditPost(string Uri, string Text, string AuthorDid, string? ParentUri, bool Qualifies);

internal sealed record AuditedThread(string Uri, int ReportedReplies, IReadOnlyList<AuditedReply> Replies,
    int HiddenReplies);

internal sealed class ThreadAudit(HttpClient httpClient)
{
    private readonly AppViewRateLimiter _limiter = new();

    internal static bool TryParsePost(string? value, out AtUri? post)
    {
        post = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? link) && link.Scheme == Uri.UriSchemeHttps &&
            link.Host is "bsky.app" or "www.bsky.app" or "bluesky.app" or "www.bluesky.app" &&
            link.IsDefaultPort && string.IsNullOrEmpty(link.UserInfo))
        {
            string[] parts = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || parts[0] != "profile" || parts[2] != "post")
            {
                return false;
            }

            candidate = $"at://{Uri.UnescapeDataString(parts[1])}/app.bsky.feed.post/{Uri.UnescapeDataString(parts[3])}";
        }

        return AtUri.TryParse(candidate, out post) && post.Collection?.ToString() == "app.bsky.feed.post" &&
            post.RecordKey is not null && !candidate.Contains('?', StringComparison.Ordinal) &&
            !candidate.Contains('#', StringComparison.Ordinal);
    }

    internal async Task<AuditedThread> ReadAsync(string value, CancellationToken cancellationToken)
    {
        if (!TryParsePost(value, out AtUri? post) || post is null)
        {
            throw new ArgumentException("A Bluesky post link or post AT URI is required.", nameof(value));
        }

        string authority = post.Authority.ToString();
        if (!Did.TryParse(authority, out _))
        {
            using JsonDocument resolution = await GetAsync(
                $"com.atproto.identity.resolveHandle?handle={Uri.EscapeDataString(authority)}", cancellationToken);
            string did = resolution.RootElement.GetProperty("did").GetString()
                ?? throw new InvalidDataException("Handle resolution returned no DID.");
            if (!Did.TryParse(did, out _))
            {
                throw new InvalidDataException("Handle resolution returned an invalid DID.");
            }

            post = new AtUri($"at://{did}/app.bsky.feed.post/{post.RecordKey}");
        }

        string uri = post.ToString();
        using JsonDocument document = await GetAsync(
            $"app.bsky.feed.getPostThread?uri={Uri.EscapeDataString(uri)}&depth=1&parentHeight=0", cancellationToken);

        return Parse(document.RootElement, uri);
    }

    internal async Task<IReadOnlyDictionary<string, VerifiedAuditPost>> VerifyAsync(
        IEnumerable<string> uris, CancellationToken cancellationToken)
    {
        var posts = new Dictionary<string, VerifiedAuditPost>(StringComparer.Ordinal);
        foreach (string[] batch in uris.Distinct(StringComparer.Ordinal).Chunk(25))
        {
            string query = string.Join('&', batch.Select(uri => $"uris={Uri.EscapeDataString(uri)}"));
            using JsonDocument document = await GetAsync($"app.bsky.feed.getPosts?{query}", cancellationToken);
            foreach (JsonElement item in document.RootElement.GetProperty("posts").EnumerateArray())
            {
                VerifiedAuditPost post = ParsePost(item);
                if (!batch.Contains(post.Uri, StringComparer.Ordinal) || !posts.TryAdd(post.Uri, post))
                {
                    throw new InvalidDataException("The AppView returned an unexpected or duplicate post during verification.");
                }
            }
        }

        return posts;
    }

    private static VerifiedAuditPost ParsePost(JsonElement post)
    {
        string uri = post.GetProperty("uri").GetString()
            ?? throw new InvalidDataException("A post has no URI.");
        string author = post.GetProperty("author").GetProperty("did").GetString()
            ?? throw new InvalidDataException("A post has no author.");
        JsonElement record = post.GetProperty("record");
        string text = record.GetProperty("text").GetString()
            ?? throw new InvalidDataException("A post has no text.");
        if (!TryParsePost(uri, out AtUri? parsed) || parsed?.Authority.ToString() != author ||
            !Did.TryParse(author, out _) ||
            !record.GetProperty("createdAt").TryGetDateTimeOffset(out DateTimeOffset created) ||
            created.UtcDateTime.Year < 1000)
        {
            throw new InvalidDataException("The AppView returned a malformed post.");
        }

        string? parent = record.TryGetProperty("reply", out JsonElement reply) && reply.ValueKind != JsonValueKind.Null
            ? reply.GetProperty("parent").GetProperty("uri").GetString()
                ?? throw new InvalidDataException("A reply has no parent URI.")
            : null;
        if (parent is not null && !TryParsePost(parent, out _))
        {
            throw new InvalidDataException("The AppView returned a malformed parent URI.");
        }

        return new(uri, text, author, parent, ReplyMatcher.ContainsPhrase(text));
    }

    internal static AuditedThread Parse(JsonElement root, string uri)
    {
        JsonElement thread = root.GetProperty("thread");
        if (!thread.TryGetProperty("post", out JsonElement post) || post.GetProperty("uri").GetString() != uri)
        {
            throw new InvalidDataException("The requested post is unavailable or the AppView returned another post.");
        }

        int reported = post.GetProperty("replyCount").GetInt32();
        if (reported < 0)
        {
            throw new InvalidDataException("The AppView returned a negative reply count.");
        }

        var replies = new List<AuditedReply>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int hidden = 0;
        if (thread.TryGetProperty("replies", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                if (!child.TryGetProperty("post", out JsonElement reply))
                {
                    if (child.TryGetProperty("blocked", out _) || child.TryGetProperty("notFound", out _))
                    {
                        hidden++;
                        continue;
                    }

                    throw new InvalidDataException("The AppView returned an unrecognized reply.");
                }

                VerifiedAuditPost parsed = ParsePost(reply);
                if (!seen.Add(parsed.Uri) || parsed.ParentUri != uri)
                {
                    throw new InvalidDataException("The AppView returned a malformed, duplicate or non-direct reply.");
                }

                replies.Add(new(parsed.Uri, parsed.Text, parsed.Qualifies, parsed.AuthorDid));
            }
        }

        return new(uri, reported, replies, hidden);
    }

    private async Task<JsonDocument> GetAsync(string query, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _limiter.SendAsync(
            token => httpClient.GetAsync($"https://public.api.bsky.app/xrpc/{query}", token), cancellationToken);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
