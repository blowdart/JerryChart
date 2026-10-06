// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using idunno.AtProto;

namespace JerryChart.Monitor;

internal sealed class ActorProfileClient(
    HttpClient httpClient,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    private readonly AppViewRateLimiter _rateLimiter = new(timeProvider, wait);
    private readonly AppViewRateLimiter _statusRateLimiter = new(timeProvider, wait);

    internal async Task<ActorResolution> ResolveMissingAsync(Did did, CancellationToken cancellationToken)
    {
        // The relay reports its own hosting/moderation state, not necessarily AppView or PDS policy.
        // Use a fixed SSRF-protected origin rather than trusting arbitrary service URLs from DID documents.
        using HttpResponseMessage response = await _statusRateLimiter.SendAsync(
            token => httpClient.GetAsync(
                $"https://bsky.network/xrpc/com.atproto.sync.getRepoStatus?did={Uri.EscapeDataString(did.ToString())}", token),
            cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        JsonElement root = document.RootElement;
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound &&
            root.TryGetProperty("error", out JsonElement error) && error.GetString() == "RepoNotFound")
        {
            // The relay uses HTTP 404 for RepoNotFound; other implementations may use 400.
            // Absence from this relay does not prove deletion. Keep the normal unknown-profile retry.
            return new(null, null);
        }

        response.EnsureSuccessStatusCode();
        if (!root.TryGetProperty("did", out JsonElement returnedDid) || returnedDid.ValueKind != JsonValueKind.String ||
            returnedDid.GetString() != did.ToString() ||
            !root.TryGetProperty("active", out JsonElement active) ||
            active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("The relay returned an invalid repository status.");
        }

        if (active.GetBoolean())
        {
            return new(null, null);
        }

        string? status = null;
        if (root.TryGetProperty("status", out JsonElement statusValue))
        {
            if (statusValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The relay returned a non-string repository status.");
            }
            status = statusValue.GetString();
        }

        return new(null, status switch
        {
            "deleted" or "deactivated" or "suspended" or "takendown" or "desynchronized" or "throttled" => status,
            _ => "inactive"
        });
    }

    internal async Task<IReadOnlyDictionary<Did, Handle?>> GetAsync(
        IReadOnlyList<ActorRefreshRequest> actors, CancellationToken cancellationToken)
    {
        if (actors.Count is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(nameof(actors), "Profile batches must contain between 1 and 25 DIDs.");
        }

        string query = string.Join("&", actors.Select(actor => $"actors={Uri.EscapeDataString(actor.Did.ToString())}"));
        using HttpResponseMessage response = await _rateLimiter.SendAsync(
            token => httpClient.GetAsync($"https://public.api.bsky.app/xrpc/app.bsky.actor.getProfiles?{query}", token),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("profiles", out JsonElement profiles) || profiles.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The AppView response has no profiles array.");
        }

        var handles = actors.ToDictionary(actor => actor.Did, _ => (Handle?)null);
        var seen = new HashSet<Did>();
        foreach (JsonElement profile in profiles.EnumerateArray())
        {
            if (profile.ValueKind != JsonValueKind.Object ||
                !profile.TryGetProperty("did", out JsonElement didValue) || didValue.ValueKind != JsonValueKind.String ||
                !profile.TryGetProperty("handle", out JsonElement handleValue) || handleValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("An AppView profile has no string DID or handle.");
            }

            string returnedDid = didValue.GetString()
                ?? throw new InvalidDataException("AppView returned a malformed DID.");
            if (!Did.TryParse(returnedDid, out Did? did))
            {
                throw new InvalidDataException("AppView returned a malformed DID.");
            }

            if (!handles.ContainsKey(did) || !seen.Add(did))
            {
                throw new InvalidDataException("AppView returned an unexpected or duplicate DID.");
            }

            if (!Handle.TryParse(handleValue.GetString()!, out Handle? handle))
            {
                throw new InvalidDataException("AppView returned a malformed handle.");
            }

            handles[did] = handle.IsValid ? handle : null;
        }

        return handles;
    }

}