// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using idunno.AtProto;

namespace JerryChart.Monitor;

internal sealed record AuditedReply(string Uri, string Text, bool Qualifies, string AuthorDid);

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

                string replyUri = reply.GetProperty("uri").GetString()
                    ?? throw new InvalidDataException("A reply has no URI.");
                string author = reply.GetProperty("author").GetProperty("did").GetString()
                    ?? throw new InvalidDataException("A reply has no author.");
                JsonElement record = reply.GetProperty("record");
                string text = record.GetProperty("text").GetString()
                    ?? throw new InvalidDataException("A reply has no text.");
                if (!TryParsePost(replyUri, out AtUri? parsed) || parsed?.Authority.ToString() != author ||
                    !Did.TryParse(author, out _) || !seen.Add(replyUri) ||
                    record.GetProperty("reply").GetProperty("parent").GetProperty("uri").GetString() != uri ||
                    !record.GetProperty("createdAt").TryGetDateTimeOffset(out DateTimeOffset created) ||
                    created.UtcDateTime.Year < 1000)
                {
                    throw new InvalidDataException("The AppView returned a malformed, duplicate or non-direct reply.");
                }

                replies.Add(new(replyUri, text, ReplyMatcher.ContainsPhrase(text), author));
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
