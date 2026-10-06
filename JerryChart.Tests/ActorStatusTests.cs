// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;

using idunno.AtProto;

using JerryChart.Monitor;

namespace JerryChart.Tests;

/// <summary>Verifies repository status classification without inferring deletion from absence.</summary>
[TestClass]
public sealed class ActorStatusTests
{
    /// <summary>Verifies inactive status mapping and refresh intervals.</summary>
    /// <param name="status">The relay reason.</param>
    /// <param name="expected">The persisted bounded status.</param>
    /// <param name="seconds">The refresh interval.</param>
    /// <returns>A task representing the lookup.</returns>
    [TestMethod]
    [DataRow("deactivated", "deactivated", 86400)]
    [DataRow("deleted", "deleted", 86400)]
    [DataRow("suspended", "suspended", 86400)]
    [DataRow("takendown", "takendown", 86400)]
    [DataRow("desynchronized", "desynchronized", 900)]
    [DataRow("throttled", "throttled", 900)]
    [DataRow("future-status", "inactive", 86400)]
    public async Task InactiveReasonsHaveSuitableRefreshIntervals(string status, string expected, int seconds)
    {
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            Assert.IsNotNull(request.RequestUri);
            Assert.AreEqual("bsky.network", request.RequestUri.Host);
            Assert.AreEqual("/xrpc/com.atproto.sync.getRepoStatus", request.RequestUri.AbsolutePath);
            Assert.Contains("did=did%3Aplc%3Atest", request.RequestUri.Query);
            return Json($$"""{"did":"did:plc:test","active":false,"status":"{{status}}"}""");
        }));
        ActorResolution result = await new ActorProfileClient(http).ResolveMissingAsync(new Did("did:plc:test"), CancellationToken.None);
        Assert.IsNull(result.Handle);
        Assert.AreEqual(expected, result.AccountStatus);
        Assert.AreEqual(seconds, result.RefreshSeconds);
    }

    /// <summary>Verifies missing reasons, active repositories, and absent repositories remain distinct.</summary>
    /// <param name="json">The response body.</param>
    /// <param name="httpStatus">The HTTP status.</param>
    /// <param name="expected">The bounded inactive status, if any.</param>
    /// <returns>A task representing the lookup.</returns>
    [TestMethod]
    [DataRow("""{"did":"did:plc:test","active":false}""", 200, "inactive")]
    [DataRow("""{"did":"did:plc:test","active":true}""", 200, null)]
    [DataRow("""{"error":"RepoNotFound"}""", 400, null)]
    [DataRow("""{"error":"RepoNotFound","message":"account not found"}""", 404, null)]
    public async Task UnknownDoesNotMeanDeleted(string json, int httpStatus, string? expected)
    {
        using var http = new HttpClient(new ResponseHandler(_ => Json(json, httpStatus)));
        ActorResolution result = await new ActorProfileClient(http).ResolveMissingAsync(new Did("did:plc:test"), CancellationToken.None);
        Assert.AreEqual(expected, result.AccountStatus);
        Assert.AreEqual(expected is null ? 900 : 86400, result.RefreshSeconds);
    }

    /// <summary>Verifies malformed success responses propagate rather than produce a placeholder.</summary>
    /// <param name="json">The malformed response.</param>
    /// <returns>A task representing the lookup.</returns>
    [TestMethod]
    [DataRow("""{"did":"did:plc:other","active":false,"status":"deleted"}""")]
    [DataRow("""{"did":"did:plc:test","active":"false"}""")]
    [DataRow("""{"did":"did:plc:test","active":false,"status":null}""")]
    public async Task InvalidStatusResponsesFail(string json)
    {
        using var http = new HttpClient(new ResponseHandler(_ => Json(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ActorProfileClient(http).ResolveMissingAsync(new Did("did:plc:test"), CancellationToken.None));
    }

    /// <summary>Verifies server errors do not become inactive-account classifications.</summary>
    /// <param name="status">The unsuccessful HTTP status.</param>
    /// <returns>A task representing the failed lookup.</returns>
    [TestMethod]
    [DataRow(503)]
    [DataRow(404)]
    public async Task ServerFailureIsNotAccountStatus(int status)
    {
        using var http = new HttpClient(new ResponseHandler(_ => Json("""{"error":"Unavailable"}""", status)));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ActorProfileClient(http).ResolveMissingAsync(new Did("did:plc:test"), CancellationToken.None));
    }

    private static HttpResponseMessage Json(string json, int status = 200) =>
        new((HttpStatusCode)status) { Content = new StringContent(json) };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
