// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using idunno.AtProto;
using idunno.AtProto.Jetstream.Archive;

using JerryChart.Monitor;

using MySqlConnector;

namespace JerryChart.Tests;

public sealed partial class ApplicationTests
{
    private static async Task VerifyOperatorJobsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using (var admin = new MySqlConnection(options.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using MySqlCommand create = admin.CreateCommand();
            create.CommandText = "CREATE DATABASE operator_jobs_test";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        options.Database = "operator_jobs_test";
        await using MySqlDataSource source = new MySqlDataSourceBuilder(options.ConnectionString).Build();
        await using MySqlConnection connection = await source.OpenConnectionAsync(cancellationToken);
        var store = new MonitorStore(connection);
        await store.InitializeAsync(cancellationToken);
        var hit = new Hit(DateTimeOffset.UtcNow, new AtUri("at://did:plc:author/app.bsky.feed.post/match"),
            new Did("did:plc:author"), new Did("did:plc:parent"), new AtUri(OperatorJobTests.RootUri));
        Assert.IsTrue(await store.SaveHitAsync(hit, cancellationToken));
        var progress = new MonitorProgress
        {
            Service = "wss://example.test?secret=must-not-print",
            AfterSeq = 123,
            ArchiveHighWaterSeq = 456,
            ArchiveCheckpoint = new SnapshotCheckpoint
            {
                RequestFingerprint = "must-not-print", PlanAfterSeq = 123, SealedTipSeq = 1000, SegmentName = "test-segment",
                SegmentChecksum = "checksum", NextBlockIndex = 4, NextByteOffset = 100
            }
        };
        store.SaveProgress(progress);
        using var report = new StringWriter(CultureInfo.InvariantCulture);
        var jobs = new OperatorJobs(source, report);
        Assert.AreEqual(0, await jobs.CheckConsistencyAsync(cancellationToken));
        Assert.Contains("Invalid parent URI/hash/status combinations: 0", report.ToString());
        report.GetStringBuilder().Clear();
        Assert.AreEqual(0, await jobs.ReplayStatusAsync(cancellationToken));
        Assert.Contains("Archive resume sequence: 123", report.ToString());
        Assert.Contains("archive high-water sequence: 456", report.ToString());
        Assert.Contains("test-segment; next block: 4; next byte offset: 100", report.ToString());
        Assert.DoesNotContain("must-not-print", report.ToString());
        Assert.AreEqual(progress, await store.LoadProgressAsync(cancellationToken));

        using var client = new HttpClient(new OperatorHttpHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("getPosts", StringComparison.Ordinal)
                    ? OperatorJobTests.PostsForRequest(request) : OperatorJobTests.ThreadJson)
            })));
        report.GetStringBuilder().Clear();
        Assert.AreEqual(0, await jobs.AuditThreadAsync(OperatorJobTests.RootUri, client, cancellationToken));
        Assert.Contains("Stored for this parent: 1; confirmed stored matches: 1; stored now nonmatching or different parent: 0; stored unavailable/unverifiable: 0", report.ToString());
        Assert.Contains("Confirmed qualifying replies absent from database and not excluded: 0", report.ToString());
        Assert.Contains("MATCH | stored | at://did:plc:author", report.ToString());
        Assert.Contains("NO MATCH | absent", report.ToString());
        Assert.Contains("Public thread results may be incomplete", report.ToString());

        using var omitted = new HttpClient(new OperatorHttpHandler((request, _) =>
        {
            JsonNode thread = JsonNode.Parse(OperatorJobTests.ThreadJson)!;
            thread["thread"]!["replies"] = new JsonArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("getPosts", StringComparison.Ordinal)
                    ? OperatorJobTests.PostsForRequest(request) : thread.ToJsonString())
            });
        }));
        report.GetStringBuilder().Clear();
        await jobs.AuditThreadAsync(OperatorJobTests.RootUri, omitted, cancellationToken);
        Assert.Contains("confirmed stored matches: 1", report.ToString());
        Assert.Contains("absent from thread response", report.ToString());
        Assert.DoesNotContain("STORED, NOT VISIBLE", report.ToString());
        Assert.Contains("Discovery completeness: NOT ESTABLISHED", report.ToString());

        foreach (string scenario in new[] { "unavailable", "edited", "different parent" })
        {
            using var changed = new HttpClient(new OperatorHttpHandler((request, _) =>
            {
                string json = OperatorJobTests.ThreadJson;
                if (request.RequestUri!.AbsolutePath.EndsWith("getPosts", StringComparison.Ordinal))
                {
                    JsonNode body = JsonNode.Parse(OperatorJobTests.PostsForRequest(request))!;
                    JsonArray posts = body["posts"]!.AsArray();
                    if (scenario == "unavailable")
                    {
                        posts.RemoveAt(0);
                    }
                    else if (scenario == "edited")
                    {
                        posts[0]!["record"]!["text"] = "Edited reply";
                    }
                    else
                    {
                        posts[0]!["record"]!["reply"]!["parent"]!["uri"] =
                            "at://did:plc:parent/app.bsky.feed.post/different";
                    }
                    json = body.ToJsonString();
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }));
            report.GetStringBuilder().Clear();
            await jobs.AuditThreadAsync(OperatorJobTests.RootUri, changed, cancellationToken);
            Assert.Contains("confirmed stored matches: 0", report.ToString());
            Assert.Contains(scenario == "unavailable"
                ? "stored now nonmatching or different parent: 0; stored unavailable/unverifiable: 1"
                : "stored now nonmatching or different parent: 1; stored unavailable/unverifiable: 0", report.ToString());
        }

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Hits SET ParentAtUri = NULL, ParentAtUriHash = NULL, ParentUriBackfillStatus = 0
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        report.GetStringBuilder().Clear();
        await jobs.AuditThreadAsync(OperatorJobTests.RootUri, client, cancellationToken);
        Assert.Contains("stored, parent unresolved or different", report.ToString());
        Assert.Contains("Confirmed qualifying replies absent from database and not excluded: 0", report.ToString());
        report.GetStringBuilder().Clear();
        Assert.AreEqual(1, await jobs.CheckConsistencyAsync(cancellationToken));
        Assert.Contains("Unresolved parent URIs (legacy/retry queue): 1", report.ToString());

        command.CommandText = """
            UPDATE Hits SET ParentAtUri = @parent, ParentAtUriHash = UNHEX(SHA2(@parent, 256)), ParentUriBackfillStatus = 1;
            DELETE FROM ActorRefresh WHERE Did = 'did:plc:parent'
            """;
        command.Parameters.AddWithValue("@parent", OperatorJobTests.RootUri);
        await command.ExecuteNonQueryAsync(cancellationToken);
        report.GetStringBuilder().Clear();
        Assert.AreEqual(1, await jobs.CheckConsistencyAsync(cancellationToken));
        Assert.Contains("Tracked actors missing refresh jobs: 1", report.ToString());
        command.CommandText = "INSERT INTO ActorRefresh (Did) VALUES ('did:plc:parent')";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "UPDATE Hits SET ParentAtUriHash = UNHEX(SHA2('wrong-parent', 256))";
        await command.ExecuteNonQueryAsync(cancellationToken);
        report.GetStringBuilder().Clear();
        Assert.AreEqual(1, await jobs.CheckConsistencyAsync(cancellationToken));
        Assert.Contains("Invalid parent URI/hash/status combinations: 1", report.ToString());
        command.CommandText = "UPDATE Hits SET ParentAtUriHash = UNHEX(SHA2(@parent, 256))";
        await command.ExecuteNonQueryAsync(cancellationToken);

        var actors = new ActorStore(connection);
        ActorRefreshRequest oldRequest = await actors.GetTrackedAsync(hit.AuthorDid, cancellationToken);
        using var profiles = new HttpClient(new OperatorHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"profiles":[{"did":"did:plc:author","handle":"renamed.example"}]}""")
            })));
        report.GetStringBuilder().Clear();
        Assert.AreEqual(0, await jobs.RefreshActorAsync(hit.AuthorDid, new ActorProfileClient(profiles), cancellationToken));
        Assert.Contains("handle=renamed.example", report.ToString());
        Assert.IsFalse(await actors.SaveResolutionAsync(oldRequest, new(new Handle("stale.example"), null), cancellationToken),
            "An updater response started before a manual refresh must not overwrite it.");
        command.CommandText = "SELECT Handle FROM Actor WHERE Did = 'did:plc:author'";
        Assert.AreEqual("renamed.example", await command.ExecuteScalarAsync(cancellationToken));
        using var missingProfile = new HttpClient(new OperatorHttpHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Host == "bsky.network"
                    ? """{"did":"did:plc:author","active":false,"status":"deactivated"}"""
                    : """{"profiles":[]}""")
            })));
        report.GetStringBuilder().Clear();
        Assert.AreEqual(0, await jobs.RefreshActorAsync(hit.AuthorDid, new ActorProfileClient(missingProfile), cancellationToken));
        Assert.Contains("account status=deactivated", report.ToString());
        command.CommandText = "SELECT AccountStatus FROM Actor WHERE Did = 'did:plc:author'";
        Assert.AreEqual("deactivated", await command.ExecuteScalarAsync(cancellationToken));
        command.CommandText = "SELECT Handle FROM Actor WHERE Did = 'did:plc:author'";

        using var invalidated = new HttpClient(new OperatorHttpHandler(async (_, token) =>
        {
            await actors.InvalidateAsync(hit.AuthorDid, token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"profiles":[{"did":"did:plc:author","handle":"outdated.example"}]}""")
            };
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            jobs.RefreshActorAsync(hit.AuthorDid, new ActorProfileClient(invalidated), cancellationToken));
        Assert.IsTrue(await command.ExecuteScalarAsync(cancellationToken) is DBNull,
            "Identity invalidation must win over an in-flight manual refresh.");

        using var failing = new HttpClient(new OperatorHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))));
        ActorRefreshRequest beforeFailure = await actors.GetTrackedAsync(hit.AuthorDid, cancellationToken);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            jobs.RefreshActorAsync(hit.AuthorDid, new ActorProfileClient(failing), cancellationToken));
        Assert.AreEqual(beforeFailure, await actors.GetTrackedAsync(hit.AuthorDid, cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            jobs.RefreshActorAsync(new Did("did:plc:untracked"), new ActorProfileClient(failing), cancellationToken));

        command.CommandText = "DELETE FROM Hits";
        await command.ExecuteNonQueryAsync(cancellationToken);
        report.GetStringBuilder().Clear();
        await jobs.AuditThreadAsync(OperatorJobTests.RootUri, client, cancellationToken);
        Assert.Contains("Confirmed qualifying replies absent from database and not excluded: 1", report.ToString());
        await new ActorExclusionStore(connection).ExcludeAsync(hit.AuthorDid, cancellationToken);
        report.GetStringBuilder().Clear();
        await jobs.AuditThreadAsync(OperatorJobTests.RootUri, client, cancellationToken);
        Assert.Contains("Confirmed qualifying replies absent from database and not excluded: 0", report.ToString());
        Assert.Contains("MATCH | excluded", report.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            jobs.RefreshActorAsync(hit.AuthorDid, new ActorProfileClient(failing), cancellationToken));
        Assert.AreEqual(progress, await store.LoadProgressAsync(cancellationToken),
            "Operator jobs must never advance or reset replay progress.");
    }
}
