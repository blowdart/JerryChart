// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Text;

using idunno.AtProto;

using JerryChart.Monitor;

namespace JerryChart.Tests;

/// <summary>Verifies public AppView parent-post responses without network access.</summary>
[TestClass]
public sealed class ParentPostClientTests
{
    /// <summary>Verifies SDK record parsing and explicit unavailable results for omitted posts.</summary>
    [TestMethod]
    public async Task ResolvesTypedParentAndReturnsMissingPostAsUnavailable()
    {
        const string hitUri = "at://did:plc:reply/app.bsky.feed.post/hit";
        const string parentUri = "at://did:plc:parent/app.bsky.feed.post/parent";
        Uri? requested = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return Json(RecordResponse(hitUri, parentUri));
        }));
        var requests = new[]
        {
            Request(hitUri, "did:plc:parent"),
            Request("at://did:plc:gone/app.bsky.feed.post/gone", "did:plc:goneparent")
        };

        IReadOnlyList<ParentUriBackfillResult> results =
            await new ParentPostClient(http).GetAsync(requests, CancellationToken.None);

        Assert.AreEqual(new AtUri(parentUri), results[0].ParentAtUri);
        Assert.IsNull(results[1].ParentAtUri);
        Assert.IsNotNull(requested);
        Assert.AreEqual("public.api.bsky.app", requested.Host);
        Assert.AreEqual(2, requested.Query.Split('&').Length);
        Assert.Contains("uris=at%3A%2F%2Fdid%3Aplc%3Areply", requested.Query);
    }

    /// <summary>Verifies that a returned parent must match the recorded immediate-parent identity and collection.</summary>
    /// <param name="parentUri">The parent URI returned by AppView.</param>
    [TestMethod]
    [DataRow("at://did:plc:other/app.bsky.feed.post/parent")]
    [DataRow("at://did:plc:parent/app.bsky.feed.like/parent")]
    public async Task RejectsMismatchedParentIdentity(string parentUri)
    {
        const string hitUri = "at://did:plc:reply/app.bsky.feed.post/hit";
        using var http = new HttpClient(new StubHandler(_ => Json(RecordResponse(hitUri, parentUri))));

        await Assert.ThrowsAsync<InvalidDataException>(() => new ParentPostClient(http)
            .GetAsync([Request(hitUri, "did:plc:parent")], CancellationToken.None));
    }

    /// <summary>Verifies missing and malformed response shapes fail visibly instead of becoming unavailable.</summary>
    /// <param name="body">The malformed AppView response.</param>
    [TestMethod]
    [DataRow("""{}""")]
    [DataRow("""{"posts":[{"uri":"at://did:plc:reply/app.bsky.feed.post/hit","record":{}}]}""")]
    [DataRow("""{"posts":"not-an-array"}""")]
    public async Task RejectsMalformedAppViewResponses(string body)
    {
        using var http = new HttpClient(new StubHandler(_ => Json(body)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ParentPostClient(http)
            .GetAsync([Request("at://did:plc:reply/app.bsky.feed.post/hit", "did:plc:parent")],
                CancellationToken.None));
    }

    /// <summary>Verifies bounded AppView batches and temporary HTTP failure classification.</summary>
    [TestMethod]
    public async Task EnforcesBatchLimitAndRetriesTemporaryResponses()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            var response = Json("""{"posts":[]}""");
            response.StatusCode = HttpStatusCode.ServiceUnavailable;
            return response;
        }));
        var client = new ParentPostClient(http);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAsync([], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAsync(
            Enumerable.Range(0, 26).Select(index => Request(
                $"at://did:plc:reply/app.bsky.feed.post/{index}", "did:plc:parent")).ToArray(),
            CancellationToken.None));
        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(
            [Request("at://did:plc:reply/app.bsky.feed.post/hit", "did:plc:parent")],
            CancellationToken.None));
        Assert.IsTrue(ParentPostClient.IsTransient(failure));
        Assert.AreEqual(1, calls);
        Assert.IsFalse(ParentPostClient.IsTransient(new HttpRequestException("Bad request", null, HttpStatusCode.BadRequest)));
    }

    private static ParentUriBackfillRequest Request(string uri, string parentDid)
    {
        return new(new AtUri(uri), new Did(parentDid), 0);
    }

    private static string RecordResponse(string hitUri, string parentUri)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            posts = new[]
            {
                new
                {
                    uri = hitUri,
                    record = new
                    {
                        text = "Jerry no",
                        createdAt = "2026-10-04T12:00:00Z",
                        reply = new
                        {
                            root = new
                            {
                                uri = "at://did:plc:root/app.bsky.feed.post/root",
                                cid = "bafyreihdwdcefgh4dqkjv67uzcmw7ojee6xedzdetojuzjevtenxquvyku"
                            },
                            parent = new
                            {
                                uri = parentUri,
                                cid = "bafyreihdwdcefgh4dqkjv67uzcmw7ojee6xedzdetojuzjevtenxquvyku"
                            }
                        }
                    }
                }
            }
        });
    }

    private static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}