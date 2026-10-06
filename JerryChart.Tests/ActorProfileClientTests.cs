// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;

using idunno.AtProto;

using JerryChart.Monitor;

namespace JerryChart.Tests;

/// <summary>Verifies profile batching, conservative pacing, and server-directed waits without network access.</summary>
[TestClass]
public sealed class ActorProfileClientTests
{
    /// <summary>Verifies normalization and unknown handles without substituting unrequested actors.</summary>
    /// <returns>A task representing the response test.</returns>
    [TestMethod]
    public async Task ProfilesAreMappedByDidAndUnknownHandlesStayNull()
    {
        Uri? requested = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return Json("""
                {"profiles":[
                    {"did":"did:plc:second","handle":"handle.invalid"},
                    {"did":"did:plc:first","handle":"FIRST.Example"}
                ]}
                """);
        }));
        var result = await new ActorProfileClient(http).GetAsync(
            [Actor("did:plc:first"), Actor("did:plc:second"), Actor("did:plc:missing")], CancellationToken.None);

        Assert.AreEqual(new Handle("first.example"), result[new Did("did:plc:first")]);
        Assert.IsNull(result[new Did("did:plc:second")]);
        Assert.IsNull(result[new Did("did:plc:missing")]);
        Assert.IsNotNull(requested);
        Assert.AreEqual("public.api.bsky.app", requested.Host);
        Assert.Contains("actors=did%3Aplc%3Afirst", requested.Query);
        Assert.HasCount(3, requested.Query.Split('&'));
    }

    /// <summary>Verifies that the endpoint's 25-actor limit is enforced before sending a request.</summary>
    /// <returns>A task representing the batch test.</returns>
    [TestMethod]
    public async Task BatchesCannotExceedTwentyFiveActors()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return Json("""{"profiles":[]}""");
        }));
        var client = new ActorProfileClient(http);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAsync([], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAsync(
            Enumerable.Range(0, 26).Select(index => Actor($"did:plc:{index}")).ToArray(),
            CancellationToken.None));
        await client.GetAsync(
            Enumerable.Range(0, 25).Select(index => Actor($"did:plc:{index}")).ToArray(),
            CancellationToken.None);
        Assert.AreEqual(1, calls);
    }

    /// <summary>Verifies malformed and unexpected identities fail rather than contaminating reports.</summary>
    /// <param name="json">The invalid response body.</param>
    /// <returns>A task representing the validation test.</returns>
    [TestMethod]
    [DataRow("""{"profiles":[{"did":"did:plc:other","handle":"other.example"}]}""")]
    [DataRow("""{"profiles":[{"did":"did:plc:first","handle":"not a handle"}]}""")]
    [DataRow("""{"profiles":[{"did":"did:plc:first","handle":"first.example"},{"did":"did:plc:first","handle":"second.example"}]}""")]
    [DataRow("""{"profiles":[{"did":"did:plc:first"}]}""")]
    [DataRow("""{"profiles":[{"did":"not-a-did","handle":"first.example"}]}""")]
    [DataRow("""{"profiles":{}}""")]
    public async Task RejectsInvalidResponses(string json)
    {
        using var http = new HttpClient(new StubHandler(_ => Json(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ActorProfileClient(http)
        .GetAsync([Actor("did:plc:first")], CancellationToken.None));
    }

    /// <summary>Verifies minimum request spacing even after successful responses.</summary>
    /// <returns>A task representing the pacing test.</returns>
    [TestMethod]
    public async Task RequestsAreSpacedByAtLeastFiveSeconds()
    {
        var clock = new TestClock();
        var waits = new List<TimeSpan>();
        using var http = new HttpClient(new StubHandler(_ => Json("""{"profiles":[]}""")));
        var client = new ActorProfileClient(http, clock, (duration, _) =>
        {
            waits.Add(duration);
            clock.Advance(duration);
            return Task.CompletedTask;
        });

        await client.GetAsync([Actor("did:plc:first")], CancellationToken.None);
        await client.GetAsync([Actor("did:plc:second")], CancellationToken.None);

        Assert.AreSequenceEqual(new[] { TimeSpan.FromSeconds(5) }, waits);
    }

    /// <summary>Verifies server rate-limit waits are global and are never capped to five minutes.</summary>
    /// <param name="mode">The rate-limit response variant.</param>
    /// <param name="expectedSeconds">The expected minimum wait.</param>
    /// <returns>A task representing the rate-limit test.</returns>
    [TestMethod]
    [DataRow("seconds", 600)]
    [DataRow("date", 900)]
    [DataRow("reset", 1200)]
    [DataRow("missing", 300)]
    [DataRow("success", 1200)]
    public async Task HonorsServerCooldowns(string mode, int expectedSeconds)
    {
        var clock = new TestClock();
        var calls = 0;
        var waits = new List<TimeSpan>();
        using var http = new HttpClient(new StubHandler(_ =>
        {
            if (++calls != 1)
            {
                return Json("""{"profiles":[]}""");
            }

            var response = Json("""{"profiles":[]}""");
            response.StatusCode = mode == "success" ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests;
            if (mode == "seconds")
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
            }
            else if (mode == "date")
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(900));
            }
            else if (mode is "reset" or "success")
            {
                response.Headers.Add("RateLimit-Remaining", "0");
                response.Headers.Add("RateLimit-Reset", clock.GetUtcNow().AddSeconds(1200).ToUnixTimeSeconds().ToString());
            }

            return response;
        }));
        var client = new ActorProfileClient(http, clock, (duration, _) =>
        {
            waits.Add(duration);
            clock.Advance(duration);
            return Task.CompletedTask;
        });
        if (mode == "success")
        {
            await client.GetAsync([Actor("did:plc:first")], CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(
                [Actor("did:plc:first")], CancellationToken.None));
        }

        await client.GetAsync([Actor("did:plc:second")], CancellationToken.None);
        Assert.AreSequenceEqual(new[] { TimeSpan.FromSeconds(expectedSeconds) }, waits);
        Assert.AreEqual(2, calls);
    }

    /// <summary>Verifies shutdown interrupts a server-directed wait before issuing another request.</summary>
    /// <returns>A task representing the cancellation test.</returns>
    [TestMethod]
    public async Task ShutdownCancelsCooldown()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return Json("""{"profiles":[]}""");
        }));
        var client = new ActorProfileClient(http, new TestClock(), (_, token) => Task.Delay(Timeout.Infinite, token));
        await client.GetAsync([Actor("did:plc:first")], CancellationToken.None);
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync([Actor("did:plc:second")], shutdown.Token));
        Assert.AreEqual(1, calls);
    }

    private static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }

    private static ActorRefreshRequest Actor(string did)
    {
        return new ActorRefreshRequest(new Did(did), 0);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        internal void Advance(TimeSpan duration) => now += duration;
    }
}