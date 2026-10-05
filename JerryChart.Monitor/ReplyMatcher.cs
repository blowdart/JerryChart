// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.Bluesky;

using Microsoft.Extensions.Logging;

namespace JerryChart.Monitor;

internal static partial class ReplyMatcher
{
    // This simple pattern does not need NonBacktracking, which prevents full source generation.
    [GeneratedRegex(@"\bJerry[\s\p{P}]+no\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Phrase();

    internal static bool ContainsPhrase(string text)
    {
        return Phrase().IsMatch(text);
    }

    internal static Hit? Match(JetstreamEvent item, ILogger logger)
    {
        if (item is not JetstreamCommitEvent commit ||
            commit.Commit.Operation is not (JetstreamCommitOperation.Create or JetstreamCommitOperation.Update) ||
            commit.Commit.Collection != CollectionNsid.Post)
        {
            return null;
        }

        AtUri atUri = new($"at://{commit.Did}/{commit.Commit.Collection}/{commit.Commit.RKey}");
        if (commit.Commit.Record is not JsonElement { ValueKind: JsonValueKind.Object } record)
        {
            MonitorLog.MissingObjectRecord(logger, atUri, item.Sequence);
            return null;
        }

        if (!record.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String)
        {
            MonitorLog.MissingText(logger, atUri, item.Sequence);
            return null;
        }

        if (!ContainsPhrase(text.GetString()!) ||
            !record.TryGetProperty("reply", out JsonElement reply) || reply.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            if (!record.TryGetProperty("createdAt", out JsonElement createdAt) ||
                createdAt.ValueKind != JsonValueKind.String || !createdAt.TryGetDateTimeOffset(out _))
            {
                throw new JsonException("A matching reply must have a valid createdAt timestamp.");
            }

            Post post = JsonSerializer.Deserialize(record,
                (JsonTypeInfo<Post>)BlueskyJsonSerializerOptions.Options.GetTypeInfo(typeof(Post)))
                ?? throw new JsonException("Could not deserialize the matching reply.");
            if (post.Reply?.Parent.Uri is not { Authority: Did parentAuthor } parentUri ||
                parentUri.Collection != CollectionNsid.Post || parentUri.RecordKey is null)
            {
                throw new JsonException("The immediate parent must be a post AT URI with a DID authority.");
            }

            if (post.CreatedAt.UtcDateTime.Year < 1000)
            {
                throw new JsonException("The creation timestamp is outside MySQL DATETIME's range.");
            }

            return new Hit(post.CreatedAt.ToUniversalTime(), atUri, commit.Did, parentAuthor, parentUri);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            MonitorLog.MalformedReply(logger, exception, atUri, item.Sequence);
            return null;
        }
    }
}