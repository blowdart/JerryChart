// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using idunno.AtProto;
using idunno.Bluesky;

namespace JerryChart.Monitor;

internal sealed record ParentUriBackfillRequest(AtUri AtUri, Did ParentAuthorDid, int AttemptCount);

internal sealed record ParentUriBackfillResult(ParentUriBackfillRequest Request, AtUri? ParentAtUri);

internal sealed class ParentPostClient(HttpClient httpClient, AppViewRateLimiter? rateLimiter = null)
{
    private readonly AppViewRateLimiter _rateLimiter = rateLimiter ?? new AppViewRateLimiter();

    internal TimeSpan MinimumRetryDelay => _rateLimiter.ServerCooldownRemaining;

    internal async Task<IReadOnlyList<ParentUriBackfillResult>> GetAsync(
        IReadOnlyList<ParentUriBackfillRequest> requests,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetCoreAsync(requests, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The AppView getPosts request timed out.", exception);
        }
    }

    private async Task<IReadOnlyList<ParentUriBackfillResult>> GetCoreAsync(
        IReadOnlyList<ParentUriBackfillRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(nameof(requests), "Post batches must contain between 1 and 25 AT URIs.");
        }

        var requested = requests.ToDictionary(request => request.AtUri.ToString(), StringComparer.Ordinal);
        string query = string.Join("&", requests.Select(request => $"uris={Uri.EscapeDataString(request.AtUri.ToString())}"));
        using HttpResponseMessage response = await _rateLimiter.SendAsync(
            token => httpClient.GetAsync($"https://public.api.bsky.app/xrpc/app.bsky.feed.getPosts?{query}", token),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"AppView getPosts returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("posts", out JsonElement posts) ||
            posts.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The AppView getPosts response has no posts array.");
        }

        var parentUris = new Dictionary<string, AtUri>(StringComparer.Ordinal);
        foreach (JsonElement postView in posts.EnumerateArray())
        {
            if (postView.ValueKind != JsonValueKind.Object ||
                !postView.TryGetProperty("uri", out JsonElement uriValue) ||
                uriValue.ValueKind != JsonValueKind.String ||
                !postView.TryGetProperty("record", out JsonElement record) ||
                record.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("An AppView post is missing its AT URI or record.");
            }

            string uri = uriValue.GetString()
                ?? throw new InvalidDataException("AppView returned a post with an invalid AT URI.");
            if (!requested.TryGetValue(uri, out ParentUriBackfillRequest? request))
            {
                throw new InvalidDataException($"AppView returned unexpected post URI {uri}.");
            }

            if (parentUris.ContainsKey(uri))
            {
                throw new InvalidDataException($"AppView returned duplicate post URI {uri}.");
            }

            AtUri parentAtUri;
            try
            {
                Post post = JsonSerializer.Deserialize(record,
                    (JsonTypeInfo<Post>)BlueskyJsonSerializerOptions.Options.GetTypeInfo(typeof(Post)))
                    ?? throw new JsonException("The AppView post record is null.");
                if (post.Reply?.Parent.Uri is not
                    { Authority: Did parentDid, Collection: { } collection, RecordKey: not null } resolvedParent ||
                    collection != CollectionNsid.Post ||
                    parentDid != request.ParentAuthorDid)
                {
                    throw new InvalidDataException(
                        $"AppView returned a parent URI inconsistent with the recorded parent DID for {uri}.");
                }

                parentAtUri = resolvedParent;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
            {
                throw new InvalidDataException($"AppView returned a malformed post record for {uri}.", exception);
            }

            parentUris.Add(uri, parentAtUri);
        }

        return requests.Select(request =>
        {
            parentUris.TryGetValue(request.AtUri.ToString(), out AtUri? parentAtUri);
            return new ParentUriBackfillResult(request, parentAtUri);
        }).ToArray();
    }

    internal static bool IsTransient(Exception exception)
    {
        return exception is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests }
            or HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError }
            or IOException
            or TimeoutException;
    }
}