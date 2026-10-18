// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using idunno.AtProto;

using JerryChart.Monitor;

namespace JerryChart.Tests;

/// <summary>Verifies one-off operator command parsing and public thread auditing.</summary>
[TestClass]
public sealed class OperatorJobTests
{
    internal const string RootUri = "at://did:plc:parent/app.bsky.feed.post/root";
    internal const string ThreadJson = """
        {"thread":{"post":{"uri":"at://did:plc:parent/app.bsky.feed.post/root","replyCount":5},
          "replies":[
            {"post":{"uri":"at://did:plc:author/app.bsky.feed.post/match","author":{"did":"did:plc:author"},
              "record":{"text":"Jerry, no. 😉","createdAt":"2026-01-01T00:00:00Z",
                "reply":{"parent":{"uri":"at://did:plc:parent/app.bsky.feed.post/root"}}}},
             "replies":[{"post":{"uri":"nested-matching-reply-not-a-direct-reply"}}]},
            {"post":{"uri":"at://did:plc:other/app.bsky.feed.post/ordinary","author":{"did":"did:plc:other"},
              "record":{"text":"Nice post!","createdAt":"2026-01-01T00:00:00Z",
                "reply":{"parent":{"uri":"at://did:plc:parent/app.bsky.feed.post/root"}}}}},
            {"blocked":true,"uri":"at://did:plc:hidden/app.bsky.feed.post/hidden"}
          ]}}
        """;

    internal static string PostsJson => new JsonObject
    {
        ["posts"] = new JsonArray(JsonNode.Parse(ThreadJson)!["thread"]!["replies"]!.AsArray()
            .Where(child => child!["post"] is not null)
            .Select(child => child!["post"]!.DeepClone()).ToArray())
    }.ToJsonString();

    internal static string PostsForRequest(HttpRequestMessage request)
    {
        string[] uris = request.RequestUri!.Query.TrimStart('?').Split('&')
            .Select(parameter => Uri.UnescapeDataString(parameter["uris=".Length..])).ToArray();
        return new JsonObject
        {
            ["posts"] = new JsonArray(JsonNode.Parse(PostsJson)!["posts"]!.AsArray()
                .Where(post => uris.Contains(post!["uri"]!.GetValue<string>(), StringComparer.Ordinal))
                .Select(post => post!.DeepClone()).ToArray())
        }.ToJsonString();
    }

    /// <summary>Verifies independent post verification batches and deduplicates URIs, including thread omissions.</summary>
    /// <returns>A task representing post verification.</returns>
    [TestMethod]
    public async Task VerifiesPostsInBoundedBatches()
    {
        string[] uris = Enumerable.Range(0, 26)
            .Select(index => $"at://did:plc:author/app.bsky.feed.post/post{index}").ToArray();
        var sizes = new List<int>();
        using var client = new HttpClient(new OperatorHttpHandler((request, _) =>
        {
            Assert.AreEqual("public.api.bsky.app", request.RequestUri!.Host);
            Assert.AreEqual("/xrpc/app.bsky.feed.getPosts", request.RequestUri.AbsolutePath);
            string[] batch = request.RequestUri.Query.TrimStart('?').Split('&')
                .Select(parameter => Uri.UnescapeDataString(parameter["uris=".Length..])).ToArray();
            sizes.Add(batch.Length);
            var posts = new JsonArray(batch.Select(uri =>
            {
                JsonNode post = JsonNode.Parse(PostsJson)!["posts"]![0]!.DeepClone();
                post["uri"] = uri;
                return post;
            }).ToArray());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["posts"] = posts }.ToJsonString())
            });
        }));

        IReadOnlyDictionary<string, VerifiedAuditPost> result = await new ThreadAudit(client)
            .VerifyAsync(uris.Concat(uris), TestContext.CancellationToken);

        Assert.AreSequenceEqual(new[] { 25, 1 }, sizes);
        Assert.HasCount(26, result);
        Assert.IsTrue(result.Values.All(post => post.Qualifies && post.ParentUri == RootUri));
    }

    /// <summary>Verifies omitted posts remain unknown and changed text or parentage is preserved for reporting.</summary>
    /// <returns>A task representing independent verification.</returns>
    [TestMethod]
    public async Task VerificationDoesNotConfuseOmittedPostsWithChangedPosts()
    {
        JsonNode body = JsonNode.Parse(PostsJson)!;
        body["posts"]![0]!["record"]!["text"] = "Edited reply";
        body["posts"]![1]!["record"]!["reply"]!["parent"]!["uri"] =
            "at://did:plc:parent/app.bsky.feed.post/different";
        using var client = new HttpClient(new OperatorHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) })));
        IReadOnlyDictionary<string, VerifiedAuditPost> result = await new ThreadAudit(client).VerifyAsync(
            ["at://did:plc:author/app.bsky.feed.post/match", "at://did:plc:other/app.bsky.feed.post/ordinary",
                "at://did:plc:missing/app.bsky.feed.post/unknown"], TestContext.CancellationToken);

        Assert.HasCount(2, result);
        Assert.IsFalse(result["at://did:plc:author/app.bsky.feed.post/match"].Qualifies);
        Assert.AreNotEqual(RootUri, result["at://did:plc:other/app.bsky.feed.post/ordinary"].ParentUri);
        Assert.IsFalse(result.ContainsKey("at://did:plc:missing/app.bsky.feed.post/unknown"));
    }

    /// <summary>Verifies duplicate, unsolicited and malformed responses cannot be accepted as verification.</summary>
    /// <param name="fault">The response fault.</param>
    /// <returns>A task representing response validation.</returns>
    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("unexpected")]
    [DataRow("author")]
    [DataRow("timestamp")]
    public async Task VerificationRejectsInvalidResponses(string fault)
    {
        JsonNode post = JsonNode.Parse(PostsJson)!["posts"]![0]!.DeepClone();
        var posts = new JsonArray(post);
        switch (fault)
        {
            case "duplicate": posts.Add(post.DeepClone()); break;
            case "unexpected": post["uri"] = "at://did:plc:author/app.bsky.feed.post/unexpected"; break;
            case "author": post["author"]!["did"] = "did:plc:forged"; break;
            case "timestamp": post["record"]!["createdAt"] = "not-a-date"; break;
        }
        using var client = new HttpClient(new OperatorHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["posts"] = posts }.ToJsonString())
            })));

        await Assert.ThrowsAsync<InvalidDataException>(() => new ThreadAudit(client)
            .VerifyAsync(["at://did:plc:author/app.bsky.feed.post/match"], TestContext.CancellationToken));
    }

    /// <summary>Verifies supported post identifiers and rejection of other origins or record types.</summary>
    /// <param name="value">The supplied post identifier.</param>
    /// <param name="valid">Whether the identifier is supported.</param>
    [TestMethod]
    [DataRow(RootUri, true)]
    [DataRow("at://jerry.example/app.bsky.feed.post/root", true)]
    [DataRow("https://bsky.app/profile/did:plc:parent/post/root", true)]
    [DataRow("https://bsky.app/profile/jerry.example/post/root?foo=bar#thread", true)]
    [DataRow("https://bluesky.app/profile/jerry.example/post/root", true)]
    [DataRow("https://evil.example/profile/jerry.example/post/root", false)]
    [DataRow("http://bsky.app/profile/jerry.example/post/root", false)]
    [DataRow("https://user@bsky.app/profile/jerry.example/post/root", false)]
    [DataRow("https://bsky.app:444/profile/jerry.example/post/root", false)]
    [DataRow("https://bsky.app/profile/jerry.example", false)]
    [DataRow("at://did:plc:parent/app.bsky.feed.like/root", false)]
    [DataRow("at://did:plc:parent/app.bsky.feed.post", false)]
    [DataRow("not-a-post", false)]
    public void ParsesPostIdentifiers(string value, bool valid)
    {
        Assert.AreEqual(valid, ThreadAudit.TryParsePost(value, out _));
    }

    /// <summary>Verifies direct replies use the ingestion matcher, count once, and exclude descendants.</summary>
    [TestMethod]
    public void AuditsOnlyVisibleDirectReplies()
    {
        using JsonDocument document = JsonDocument.Parse(ThreadJson);
        AuditedThread result = ThreadAudit.Parse(document.RootElement, RootUri);
        Assert.AreEqual(5, result.ReportedReplies);
        Assert.HasCount(2, result.Replies);
        Assert.AreEqual(1, result.HiddenReplies);
        Assert.HasCount(1, result.Replies.Where(reply => reply.Qualifies).ToArray());
        Assert.AreEqual("Jerry, no. 😉", result.Replies[0].Text);
    }

    /// <summary>Verifies mismatched roots and invalid reply parentage fail explicitly.</summary>
    /// <param name="differentRoot">Whether the root rather than a reply is changed.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RejectsUnexpectedThread(bool differentRoot)
    {
        JsonNode root = JsonNode.Parse(ThreadJson)!;
        if (differentRoot)
        {
            root["thread"]!["post"]!["uri"] = "at://did:plc:other/app.bsky.feed.post/other";
        }
        else
        {
            root["thread"]!["replies"]![0]!["post"]!["record"]!["reply"]!["parent"]!["uri"] =
                "at://did:plc:other/app.bsky.feed.post/other";
        }

        using JsonDocument document = JsonDocument.Parse(root.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ThreadAudit.Parse(document.RootElement, RootUri));
    }

    /// <summary>Verifies supported identifiers use the fixed AppView origin and resolve handle authorities.</summary>
    /// <param name="value">The supplied post identifier.</param>
    /// <param name="resolveHandle">Whether handle resolution is required.</param>
    /// <returns>A task representing the audit.</returns>
    [TestMethod]
    [DataRow("https://bsky.app/profile/jerry.example/post/root", true)]
    [DataRow("https://bluesky.app/profile/jerry.example/post/root", true)]
    [DataRow("at://jerry.example/app.bsky.feed.post/root", true)]
    [DataRow(RootUri, false)]
    [DataRow("https://bsky.app/profile/did:plc:parent/post/root", false)]
    public async Task ReadsSupportedIdentifiers(string value, bool resolveHandle)
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new OperatorHttpHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            string body = resolveHandle && requests.Count == 1 ? """{"did":"did:plc:parent"}""" : ThreadJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        AuditedThread result = await new ThreadAudit(client).ReadAsync(value, TestContext.CancellationToken);
        Assert.AreEqual(RootUri, result.Uri);
        Assert.HasCount(resolveHandle ? 2 : 1, requests);
        Assert.IsTrue(requests.All(uri => uri.Host == "public.api.bsky.app" && uri.Scheme == "https"));
        if (resolveHandle)
        {
            Assert.Contains("resolveHandle?handle=jerry.example", requests[0].ToString());
        }

        Assert.Contains(Uri.EscapeDataString(RootUri), requests[^1].AbsoluteUri);
    }

    /// <summary>Verifies HTTP failure and cancellation cannot produce a successful audit.</summary>
    /// <returns>A task representing the failure cases.</returns>
    [TestMethod]
    public async Task AuditPropagatesFailureAndCancellation()
    {
        using var client = new HttpClient(new OperatorHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ThreadAudit(client).ReadAsync(RootUri, TestContext.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ThreadAudit(client).VerifyAsync(
            [RootUri], TestContext.CancellationToken));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ThreadAudit(client).ReadAsync(RootUri, canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ThreadAudit(client).VerifyAsync([RootUri], canceled.Token));
    }

    /// <summary>Verifies each operator subcommand routes without running ingestion.</summary>
    /// <param name="arguments">The command arguments.</param>
    /// <param name="expected">The expected handler.</param>
    /// <returns>A task representing command execution.</returns>
    [TestMethod]
    [DataRow("audit-thread " + RootUri, "audit")]
    [DataRow("audit-thread https://bsky.app/profile/jerry.example/post/root", "audit")]
    [DataRow("refresh-actor did:plc:actor", "refresh")]
    [DataRow("check-consistency", "check")]
    [DataRow("replay-status", "status")]
    public async Task RoutesOperatorCommands(string arguments, string expected)
    {
        string? called = null;
        Task<int> Invoke(string name)
        {
            called = name;
            return Task.FromResult(7);
        }

        var command = MonitorCommand.Create(_ => throw new AssertFailedException("Must not start ingestion."),
            auditThread: (_, _) => Invoke("audit"), refreshActor: (_, _) => Invoke("refresh"),
            checkConsistency: _ => Invoke("check"), replayStatus: _ => Invoke("status"));
        int result = await command.Parse(arguments).InvokeAsync(cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(7, result);
        Assert.AreEqual(expected, called);
    }

    /// <summary>Verifies invalid arguments and help never invoke operator jobs or ingestion.</summary>
    /// <param name="arguments">The supplied command arguments.</param>
    /// <param name="exitCode">The expected parsing exit code.</param>
    /// <returns>A task representing parsing.</returns>
    [TestMethod]
    [DataRow("audit-thread", 1)]
    [DataRow("audit-thread https://evil.example/post", 1)]
    [DataRow("refresh-actor not-a-did", 1)]
    [DataRow("refresh-actor", 1)]
    [DataRow("check-consistency extra", 1)]
    [DataRow("replay-status extra", 1)]
    [DataRow("audit-thread --help", 0)]
    [DataRow("refresh-actor --help", 0)]
    [DataRow("check-consistency --help", 0)]
    [DataRow("replay-status --help", 0)]
    public async Task InvalidArgumentsAndHelpDoNotRunJobs(string arguments, int exitCode)
    {
        Task<int> Unexpected() => throw new AssertFailedException("No handler should run.");
        var command = MonitorCommand.Create(_ => Unexpected(), auditThread: (_, _) => Unexpected(),
            refreshActor: (_, _) => Unexpected(), checkConsistency: _ => Unexpected(), replayStatus: _ => Unexpected());
        Assert.AreEqual(exitCode, await command.Parse(arguments).InvokeAsync(cancellationToken: TestContext.CancellationToken));
    }

    /// <summary>Gets or sets the current test context.</summary>
    public TestContext TestContext { get; set; }
}

internal sealed class OperatorHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
}
