// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

using Coravel.Scheduling.Schedule;
using Coravel.Scheduling.Schedule.Interfaces;

using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.AtProto.Jetstream.Archive;

using JerryChart.Data;
using JerryChart.Monitor;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MySqlConnector;

namespace JerryChart.Tests;

/// <summary>Exercises the Aspire application against a real, isolated MySQL container.</summary>
[TestClass]
public sealed class ApplicationTests
{
    /// <summary>Verifies monitor durability without starting or installing frontend dependencies.</summary>
    /// <returns>A task representing the database integration test.</returns>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task MonitorStateAndHitsSurviveRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellationToken = timeout.Token;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.JerryChart_AppHost>(
            ["Parameters:jetstream-api-key=integration-test-unused",
                "Parameters:mysql-password=integration-test-only-password"], cancellationToken);
        var monitorEndpoint = builder.Resources.Single(resource => resource.Name == "monitor")
            .Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "http");
        Assert.AreEqual(8081, monitorEndpoint.TargetPort);
        Assert.IsFalse(monitorEndpoint.IsExternal,
            "The monitor's container probe endpoint must not be advertised as an external public endpoint.");
        var apiResource = builder.Resources.OfType<ProjectResource>().Single(resource => resource.Name == "api");
        builder.CreateResourceBuilder(apiResource)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", Environments.Production)
            .WithEnvironment("DOTNET_ENVIRONMENT", Environments.Production);
        foreach (var resource in builder.Resources.Where(resource => resource.Name is "monitor" or "web" or "web-installer").ToArray())
        {
            builder.Resources.Remove(resource);
        }

        var mysql = builder.Resources.OfType<MySqlServerResource>().Single();
        foreach (var mount in mysql.Annotations.OfType<ContainerMountAnnotation>().ToArray())
        {
            mysql.Annotations.Remove(mount);
        }

        await using var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);
        using var api = app.CreateHttpClient("api", "http");
        Assert.AreEqual("Healthy", await api.GetStringAsync("/alive", cancellationToken));
        Assert.AreEqual("Healthy", await api.GetStringAsync("/health", cancellationToken));
        var emptySummary = await api.GetFromJsonAsync<ReplySummary>("/statistics/reply-summary", cancellationToken);
        Assert.AreEqual(new ReplySummary(0, 0, 0), emptySummary,
            "An empty database must be queryable before the monitor creates any hits.");
        var emptyAuthors = await api.GetFromJsonAsync<TopReplyAuthor[]>(
            "/statistics/right-jerry/top-authors", cancellationToken);
        Assert.IsNotNull(emptyAuthors);
        Assert.IsEmpty(emptyAuthors);
        using var emptyPostsResponse = await api.GetAsync("/statistics/right-jerry/top-posts", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, emptyPostsResponse.StatusCode);
        Assert.AreEqual("[]", await emptyPostsResponse.Content.ReadAsStringAsync(cancellationToken));
        var emptyAllAuthors = await api.GetFromJsonAsync<TopReplyAuthor[]>(
            "/statistics/right-jerry/authors", cancellationToken);
        Assert.IsNotNull(emptyAllAuthors);
        Assert.IsEmpty(emptyAllAuthors);
        var emptyMonths = await api.GetFromJsonAsync<MonthlyReplyCount[]>(
            "/statistics/right-jerry/monthly-replies", cancellationToken);
        Assert.IsNotNull(emptyMonths);
        Assert.AreEqual(6, emptyMonths.Length);
        Assert.IsTrue(emptyMonths.All(month => month.ReplyCount == 0));
        var emptyUpdate = await api.GetFromJsonAsync<StatisticsLastUpdated>("/statistics/last-updated", cancellationToken);
        Assert.IsNotNull(emptyUpdate);
        Assert.IsNull(emptyUpdate.UpdatedAt);
        var connectionString = await app.GetConnectionStringAsync("jerrychart", cancellationToken);
        Assert.IsNotNull(connectionString);
        using (var statusResponse = await api.GetAsync("/statistics/processing-status", cancellationToken))
        {
            Assert.IsTrue(statusResponse.Headers.CacheControl?.NoStore);
            var status = await statusResponse.Content.ReadFromJsonAsync<ProcessingStatus>(cancellationToken);
            Assert.IsNotNull(status);
            Assert.AreEqual("not-started", status.Monitor.Activity.State);
            Assert.AreEqual("not-started", status.ParentUriBackfill.Activity.State);
            Assert.IsFalse(status.Monitor.Activity.IsRunning);
            Assert.IsFalse(status.ParentUriBackfill.Activity.IsRunning);
            Assert.IsNull(status.Monitor.CheckpointUpdatedAt);
            Assert.AreEqual(0L, status.ParentUriBackfill.Pending);
            Assert.AreEqual(0L, status.ParentUriBackfill.RetryPending);
            string json = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain("runId", json);
            Assert.DoesNotContain("checkpointJson", json);
        }
        await VerifyProcessingStatusAsync(connectionString, cancellationToken);
        await VerifyScheduledBackfillAsync(connectionString, cancellationToken);
        await VerifyActorUpdaterStartupAsync(connectionString, cancellationToken);
        await VerifyMonitorStorageAsync(connectionString, cancellationToken);
        await VerifyParentUriBackfillAsync(connectionString, cancellationToken);
        await VerifyReplySummaryAsync(api, connectionString, cancellationToken);
        await VerifyTopReplyAuthorsAsync(api, connectionString, cancellationToken);
        await VerifyMonthlyRepliesAsync(api, connectionString, cancellationToken);
        await VerifyTopReplyPostsAsync(api, connectionString, cancellationToken);
        await VerifyStatisticsTimestampAsync(connectionString, cancellationToken);
        using var updatedResponse = await api.GetAsync("/statistics/last-updated", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);
        var updated = await updatedResponse.Content.ReadFromJsonAsync<StatisticsLastUpdated>(cancellationToken);
        Assert.IsNotNull(updated?.UpdatedAt);
        Assert.AreEqual(TimeSpan.Zero, updated.UpdatedAt.Value.Offset);
        using var updatedJson = System.Text.Json.JsonDocument.Parse(
            await updatedResponse.Content.ReadAsStringAsync(cancellationToken));
        Assert.AreEqual(updated.UpdatedAt.Value,
            DateTimeOffset.Parse(updatedJson.RootElement.GetProperty("updatedAt").GetString()!, CultureInfo.InvariantCulture));
    }

    /// <summary>Verifies statistics API responses and Next.js report rendering.</summary>
    /// <returns>A task representing the integration test.</returns>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task ApplicationDisplaysStatisticsThroughMySql()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var cancellationToken = timeout.Token;
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.JerryChart_AppHost>(
                ["Parameters:jetstream-api-key=integration-test-unused",
                    "Parameters:mysql-password=integration-test-only-password"], cancellationToken);
        builder.Resources.Remove(builder.Resources.Single(resource => resource.Name == "monitor"));

        var mysql = builder.Resources.OfType<MySqlServerResource>().Single();
        foreach (var mount in mysql.Annotations.OfType<ContainerMountAnnotation>().ToArray())
        {
            mysql.Annotations.Remove(mount);
        }

        await using var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);
        using var api = app.CreateHttpClient("api", "http");
        var connectionString = await app.GetConnectionStringAsync("jerrychart", cancellationToken);
        Assert.IsNotNull(connectionString);

        await app.ResourceNotifications.WaitForResourceAsync("web", KnownResourceStates.Running, cancellationToken);
        using var web = app.CreateHttpClient("web", "http");
        web.Timeout = TimeSpan.FromMinutes(2);
        var html = await web.GetStringAsync("/", cancellationToken);
        Assert.Contains("Jerry No", html);
        Assert.Contains("Statistics on Bluesky's collective failure to make Jerry Chen reconsider his choices",
            WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("Add a note", html);
        Assert.DoesNotContain("Shared notes", html);
        Assert.Contains("Monthly Jerry no replies to the right Jerry", html);

        await VerifyMonitorStorageAsync(connectionString, cancellationToken);
        await VerifyReplySummaryAsync(api, connectionString, cancellationToken);
        var summaryHtml = await web.GetStringAsync("/", cancellationToken);
        Assert.Contains("Jerry no reply summary", summaryHtml);
        Assert.Contains("Total Jerry no replies", summaryHtml);
        Assert.Contains("\u2937 the right Jerry", WebUtility.HtmlDecode(summaryHtml));
        Assert.Contains("\u2937 the wrong Jerry", WebUtility.HtmlDecode(summaryHtml));
        Assert.IsTrue(Regex.IsMatch(summaryHtml, @"Total Jerry no replies</th><td[^>]*>5</td>"));
        Assert.IsTrue(Regex.IsMatch(WebUtility.HtmlDecode(summaryHtml), "\u2937 the right Jerry</th><td[^>]*>2</td>"));
        Assert.IsTrue(Regex.IsMatch(WebUtility.HtmlDecode(summaryHtml), "\u2937 the wrong Jerry</th><td[^>]*>3</td>"));
        await VerifyTopReplyAuthorsAsync(api, connectionString, cancellationToken);
        var rankedHtml = await web.GetStringAsync("/", cancellationToken);
        Assert.Contains("Top 10 users telling Jerry \"No\"", WebUtility.HtmlDecode(rankedHtml));
        Assert.Contains("@renamed.example", rankedHtml);
        Assert.Contains("did:plc:rank01", rankedHtml);
        Assert.Contains("Handle unavailable", rankedHtml);
    }

    private static async Task VerifyProcessingStatusAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using (var admin = new MySqlConnection(options.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using var command = admin.CreateCommand();
            command.CommandText = "CREATE DATABASE processing_status_test";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        options.Database = "processing_status_test";
        // Worker locks are non-pooled; heartbeats and API queries exercise separate pooled connections.
        var poolOptions = new MySqlConnectionStringBuilder(options.ConnectionString) { Pooling = true, UseAffectedRows = true };
        await using var source = new MySqlDataSourceBuilder(poolOptions.ConnectionString).Build();
        var clock = new ProcessingTestClock();
        var statusStore = new ProcessingStatusStore(source, clock);
        var estimatedProgress = new MonitorProgress
        {
            Service = "https://fixture.invalid",
            ArchiveHighWaterSeq = 1000,
            ArchiveEstimate = new(123.5, clock.GetUtcNow())
        };
        const string firstRun = "00000000-0000-0000-0000-000000000001";
        const string secondRun = "00000000-0000-0000-0000-000000000002";
        await using (var owner = new MySqlConnection(options.ConnectionString))
        {
            await owner.OpenAsync(cancellationToken);
            var store = new MonitorStore(owner);
            await store.InitializeAsync(cancellationToken);
            await MonitorSchema.InitializeAsync(owner, cancellationToken);
            Assert.IsTrue(await statusStore.StartAsync("monitor", firstRun, "archive", ":jerry-no-v1",
                owner.ServerThread, cancellationToken));
            Assert.AreEqual("archive", (await statusStore.GetAsync(cancellationToken)).Monitor.Activity.State);
            store.SaveProgress(estimatedProgress);
            Assert.AreEqual(estimatedProgress.ArchiveEstimate,
                (await statusStore.GetAsync(cancellationToken)).Monitor.ArchiveEstimate,
                "Status must expose the checkpoint's fractional duration and UTC measurement without changing the cursor.");
            Assert.AreEqual(estimatedProgress, await store.LoadProgressAsync(cancellationToken));
            Assert.IsTrue(await statusStore.ChangeAsync("monitor", firstRun, "retrying", false, cancellationToken));
            Assert.IsNull((await statusStore.GetAsync(cancellationToken)).Monitor.ArchiveEstimate);
            Assert.IsTrue(await statusStore.ChangeAsync("monitor", firstRun, "live", false, cancellationToken));
            DateTimeOffset changedAt = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(15));
            Assert.IsTrue(await statusStore.ChangeAsync("monitor", firstRun, "live", false, cancellationToken));
            Assert.AreEqual(changedAt, (await statusStore.GetAsync(cancellationToken)).Monitor.Activity.ChangedAt,
                "Repeated phase writes must preserve the actual last phase-change time.");
            Assert.IsTrue(await statusStore.HeartbeatAsync("monitor", firstRun, ":jerry-no-v1",
                owner.ServerThread, cancellationToken));
            ProcessingStatus live = await statusStore.GetAsync(cancellationToken);
            Assert.AreEqual("live", live.Monitor.Activity.State);
            Assert.IsNull(live.Monitor.ArchiveEstimate);
            Assert.AreEqual(clock.GetUtcNow(), live.Monitor.Activity.HeartbeatAt);
            Assert.AreEqual(TimeSpan.Zero, live.Monitor.Activity.HeartbeatAt!.Value.Offset);

            await using var competitor = new MySqlConnection(options.ConnectionString);
            await competitor.OpenAsync(cancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new MonitorStore(competitor).InitializeAsync(cancellationToken));
            Assert.IsFalse(await statusStore.StartAsync("monitor", secondRun, "archive", ":jerry-no-v1",
                competitor.ServerThread, cancellationToken));
            Assert.AreEqual("live", (await statusStore.GetAsync(cancellationToken)).Monitor.Activity.State);

            await using var command = owner.CreateCommand();
            command.CommandText = """
                    INSERT INTO Hits (AtUriHash, AtUri, CreatedAt, AuthorDid, ParentAuthorDid,
                        ParentUriBackfillStatus, ParentUriBackfillNextAttemptAt)
                    VALUES (UNHEX(SHA2('pending', 256)), 'at://pending', @now, 'did:plc:a', 'did:plc:wrong', 0, @future),
                        (UNHEX(SHA2('due', 256)), 'at://due', @now, 'did:plc:a', 'did:plc:wrong', 3, @now),
                        (UNHEX(SHA2('future', 256)), 'at://future', @now, 'did:plc:a', 'did:plc:wrong', 3, @future),
                        (UNHEX(SHA2('resolved', 256)), 'at://resolved', @now, 'did:plc:a', 'did:plc:wrong', 1, @now),
                        (UNHEX(SHA2('unavailable', 256)), 'at://unavailable', @now, 'did:plc:a', 'did:plc:wrong', 2, @now);
                    """;
            command.Parameters.AddWithValue("@now", clock.GetUtcNow().UtcDateTime);
            command.Parameters.AddWithValue("@future", clock.GetUtcNow().AddHours(1).UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
            ParentUriProcessingStatus queue = (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill;
            Assert.AreEqual("not-started", queue.Activity.State);
            Assert.IsFalse(queue.Activity.IsRunning);
            Assert.AreEqual(1L, queue.Pending, "Pending rows are eligible even with a future retry timestamp.");
            Assert.AreEqual(2L, queue.RetryPending);
            Assert.AreEqual(1L, queue.RetryDue);
            Assert.AreEqual(1L, queue.Resolved);
            Assert.AreEqual(1L, queue.Unavailable);

            await using var backfillOwner = new MySqlConnection(options.ConnectionString);
            await backfillOwner.OpenAsync(cancellationToken);
            await new ParentUriBackfillStore(backfillOwner).InitializeAsync(cancellationToken);
            await VerifyIndependentHeartbeatAsync(source, backfillOwner, clock, cancellationToken);
            foreach (string outcome in new[] { "completed", "stopped", "failure" })
            {
                await using var activity = await ProcessingActivity.StartAsync(source, NullLogger.Instance,
                    "parent-uri-backfill", "backfill-running", ":parent-uri-backfill", backfillOwner, clock,
                    cancellationToken);
                Assert.AreEqual("backfill-running", (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);
                if (outcome == "completed")
                {
                    await activity.ExecuteAsync(() => Task.CompletedTask, "completed", cancellationToken);
                }
                else if (outcome == "stopped")
                {
                    using var shutdown = new CancellationTokenSource();
                    await shutdown.CancelAsync();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => activity.ExecuteAsync(
                        () => Task.FromCanceled(shutdown.Token), "completed", shutdown.Token));
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => activity.ExecuteAsync(
                        () => throw new InvalidDataException("Simulated unrecoverable worker failure."),
                        "completed", cancellationToken));
                }

                WorkerActivity terminal = (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity;
                Assert.AreEqual(outcome, terminal.State);
                Assert.IsFalse(terminal.IsRunning);
                Assert.IsNotNull(terminal.FinishedAt);
            }

            command.Parameters.Clear();
            command.CommandText = """
                    UPDATE Hits SET ParentUriBackfillStatus = 2 WHERE ParentUriBackfillStatus IN (0, 3);
                    DELETE FROM ProcessingActivity WHERE Resource = 'parent-uri-backfill'
                    """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            queue = (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill;
            Assert.AreEqual(0L, queue.Pending + queue.RetryPending);
            Assert.AreEqual("not-started", queue.Activity.State,
                "Legacy terminal rows prove queue completeness, not a completed or active worker run.");
        }

        int replacementConnectionId;
        await using (var replacement = new MySqlConnection(options.ConnectionString))
        {
            await replacement.OpenAsync(cancellationToken);
            replacementConnectionId = replacement.ServerThread;
            await new MonitorStore(replacement).InitializeAsync(cancellationToken);
            Assert.IsTrue(await statusStore.StartAsync("monitor", secondRun, "live", ":jerry-no-v1",
                replacement.ServerThread, cancellationToken));
            Assert.IsFalse(await statusStore.ChangeAsync("monitor", firstRun, "stopped", true, cancellationToken));
            Assert.IsFalse(await statusStore.HeartbeatAsync("monitor", firstRun, ":jerry-no-v1",
                replacement.ServerThread, cancellationToken));
            Assert.AreEqual("live", (await statusStore.GetAsync(cancellationToken)).Monitor.Activity.State);
        }

        Assert.IsFalse(await statusStore.HeartbeatAsync("monitor", secondRun, ":jerry-no-v1",
            replacementConnectionId, cancellationToken), "A closed resource session must not keep its heartbeat fresh.");
        clock.Advance(TimeSpan.FromSeconds(60));
        WorkerActivity stale = (await statusStore.GetAsync(cancellationToken)).Monitor.Activity;
        Assert.AreEqual("stale", stale.State);
        Assert.AreEqual("live", stale.Phase);
        Assert.IsFalse(stale.IsRunning);
        await using var read = await source.OpenConnectionAsync(cancellationToken);
        await using var verify = read.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM JetstreamReplayProgress";
        Assert.AreEqual(1L, Convert.ToInt64(await verify.ExecuteScalarAsync(cancellationToken)),
            "Status updates must not add replay cursors beyond the explicitly saved fixture.");
        await using var checkpointOwner = new MySqlConnection(options.ConnectionString);
        await checkpointOwner.OpenAsync(cancellationToken);
        var checkpointStore = new MonitorStore(checkpointOwner);
        await checkpointStore.InitializeAsync(cancellationToken);
        Assert.AreEqual(estimatedProgress, await checkpointStore.LoadProgressAsync(cancellationToken),
            "Activity updates and status reads must preserve the complete saved progress.");
        var saved = new MonitorProgress { Service = "wss://fixture.invalid", AfterSeq = 123 };
        checkpointStore.SaveProgress(saved);
        verify.CommandText = "DELETE FROM ProcessingActivity WHERE Resource = 'monitor'";
        await verify.ExecuteNonQueryAsync(cancellationToken);
        ProcessingStatus legacy = await statusStore.GetAsync(cancellationToken);
        Assert.AreEqual("not-started", legacy.Monitor.Activity.State);
        Assert.IsFalse(legacy.Monitor.Activity.IsRunning);
        Assert.IsNotNull(legacy.Monitor.CheckpointUpdatedAt);
        Assert.IsNull(legacy.Monitor.ArchiveEstimate);
        Assert.AreEqual(saved, await checkpointStore.LoadProgressAsync(cancellationToken),
            "Reading status must not change existing cursor values.");
    }

    private static async Task VerifyIndependentHeartbeatAsync(MySqlDataSource source, MySqlConnection owner,
        ProcessingTestClock clock, CancellationToken cancellationToken)
    {
        var logger = new ProcessingFailureLogger();
        var statusStore = new ProcessingStatusStore(source, clock);
        await using var activity = await ProcessingActivity.StartAsync(source, logger, "parent-uri-backfill",
            "backfill-running", ":parent-uri-backfill", owner, clock, cancellationToken);
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task execution = activity.ExecuteAsync(() => work.Task, "completed", cancellationToken);
        await using var command = owner.CreateCommand();
        try
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            await WaitForHeartbeatAsync(statusStore, clock.GetUtcNow(), cancellationToken);
            await activity.ChangeAsync("retrying", cancellationToken);
            DateTimeOffset lastHeartbeat = clock.GetUtcNow();
            command.CommandText = """
                ALTER TABLE ProcessingActivity ADD CONSTRAINT CK_Processing_TestFailure
                CHECK (Resource <> 'parent-uri-backfill' OR HeartbeatAt <= @heartbeat)
                """;
            command.Parameters.AddWithValue("@heartbeat", lastHeartbeat.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
            try
            {
                clock.Advance(TimeSpan.FromSeconds(15));
                Exception failure = await logger.Failures.Reader.ReadAsync(cancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                Assert.IsInstanceOfType<MySqlException>(failure);
                clock.Advance(TimeSpan.FromSeconds(45));
                await logger.Failures.Reader.ReadAsync(cancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                WorkerActivity stale = (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity;
                Assert.AreEqual(lastHeartbeat, stale.HeartbeatAt);
                Assert.AreEqual("stale", stale.State);
                Assert.IsFalse(stale.IsRunning);
                Assert.IsFalse(execution.IsCompleted, "Heartbeat errors must not abort independent worker progress.");
            }

            finally
            {
                command.Parameters.Clear();
                command.CommandText = "ALTER TABLE ProcessingActivity DROP CHECK CK_Processing_TestFailure";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            clock.Advance(TimeSpan.FromSeconds(15));
            await WaitForHeartbeatAsync(statusStore, clock.GetUtcNow(), cancellationToken);
            Assert.AreEqual("retrying", (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);
        }
        finally
        {
            work.TrySetResult();
            await execution;
        }

        DateTimeOffset? finishedHeartbeat = (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity.HeartbeatAt;
        clock.Advance(TimeSpan.FromMinutes(1));
        ParentUriProcessingStatus completed = (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill;
        Assert.AreEqual("completed", completed.Activity.State);
        Assert.AreEqual(finishedHeartbeat, completed.Activity.HeartbeatAt,
            "The awaited heartbeat must not outlive a completed worker.");
    }

    private static async Task VerifyScheduledBackfillAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using (var admin = new MySqlConnection(options.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using var create = admin.CreateCommand();
            create.CommandText = "CREATE DATABASE scheduled_backfill_test";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        options.Database = "scheduled_backfill_test";
        await using var source = new MySqlDataSourceBuilder(options.ConnectionString).Build();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await MonitorSchema.InitializeAsync(connection, cancellationToken);
        var statusStore = new ProcessingStatusStore(source, TimeProvider.System);
        var log = new ScheduledBackfillLogger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new List<ScheduledBackfillHandler>();
        bool fail = false;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(log);
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Func<HttpClient>>(() =>
        {
            var handler = new ScheduledBackfillHandler(async token =>
            {
                if (fail)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new AssertFailedException("Blocked request should be canceled by host shutdown.");
            });
            clients.Add(handler);
            return new HttpClient(handler);
        });
        builder.Services.AddParentUriBackfillScheduler();
        using var host = builder.Build();
        host.Services.UseParentUriBackfillScheduler();
        var scheduler = (Scheduler)host.Services.GetRequiredService<IScheduler>();
        await scheduler.RunAtAsync(new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual("completed", (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);
        Assert.AreEqual(0, clients[0].Calls, "An empty scheduled queue must not access the public AppView.");
        Assert.IsTrue(clients[0].Disposed);

        await using (var manualOwner = new MySqlConnection(options.ConnectionString))
        {
            await manualOwner.OpenAsync(cancellationToken);
            await new ParentUriBackfillStore(manualOwner).InitializeAsync(cancellationToken);
            await using var manual = await ProcessingActivity.StartAsync(source, NullLogger.Instance,
                "parent-uri-backfill", "backfill-running", ":parent-uri-backfill", manualOwner,
                TimeProvider.System, cancellationToken);
            WorkerActivity before = (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity;
            await scheduler.RunAtAsync(new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc));
            Assert.AreEqual(1, log.Skipped);
            Assert.AreEqual(before, (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity,
                "A skipped schedule must not overwrite the manual owner's status.");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.Services.GetRequiredService<ParentUriBackfillInvocation>().RunAsync(false, cancellationToken));
            Assert.AreEqual(before, (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity);
            Assert.IsTrue(clients.All(client => client.Disposed));
            await manual.FinishAsync("stopped");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
                    INSERT INTO Hits (AtUriHash, AtUri, CreatedAt, AuthorDid, ParentAuthorDid)
                    VALUES (UNHEX(SHA2('scheduled', 256)), 'at://did:plc:reply/app.bsky.feed.post/scheduled',
                        UTC_TIMESTAMP(6), 'did:plc:reply', 'did:plc:parent')
                    """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        Task running = scheduler.RunAtAsync(new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.AreEqual("backfill-running", (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);
            int allocated = clients.Count;
            await scheduler.RunAtAsync(new DateTime(2026, 10, 8, 3, 0, 0, DateTimeKind.Utc));
            Assert.AreEqual(allocated, clients.Count, "Coravel PreventOverlapping must suppress another due invocation.");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.Services.GetRequiredService<ParentUriBackfillInvocation>().RunAsync(false, cancellationToken));
        }
        finally
        {
            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await running.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        Assert.AreEqual("stopped", (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);
        Assert.IsTrue(clients.All(client => client.Disposed), "Each invocation must dispose its fresh HTTP handler.");
        await using (var released = new MySqlConnection(options.ConnectionString))
        {
            await released.OpenAsync(cancellationToken);
            Assert.IsTrue(await new ParentUriBackfillStore(released).TryInitializeAsync(cancellationToken),
                "Host cancellation must release the dedicated database advisory lock.");
        }

        // A later invocation still uses the same entrypoint and reports known failures without killing scheduling.
        using var freshHost = Host.CreateApplicationBuilder().Build();
        fail = true;
        var invocation = host.Services.GetRequiredService<ParentUriBackfillInvocation>();
        var next = new ScheduledParentUriBackfill(invocation,
            freshHost.Services.GetRequiredService<IHostApplicationLifetime>(), log);
        await next.Invoke();
        Assert.AreEqual("failure", (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);
        Assert.AreEqual(1, log.Failed);
        Assert.IsTrue(clients.All(client => client.Disposed));
        command.CommandText = "UPDATE Hits SET ParentUriBackfillStatus = 2";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await next.Invoke();
        ParentUriProcessingStatus completed = (await statusStore.GetAsync(cancellationToken)).ParentUriBackfill;
        Assert.AreEqual("completed", completed.Activity.State);
        Assert.AreEqual(1L, completed.Unavailable);
        Assert.AreEqual(0, clients[^1].Calls, "Terminal unavailable rows must not be fetched again.");
        Assert.IsTrue(clients[^1].Disposed);
    }

    private sealed class ScheduledBackfillHandler(Func<CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ScheduledBackfillLogger : ILoggerProvider, ILogger<ScheduledParentUriBackfill>
    {
        internal int Skipped { get; private set; }
        internal int Failed { get; private set; }
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 30)
            {
                Skipped++;
            }

            if (eventId.Id == 23)
            {
                Failed++;
            }
        }

        public void Dispose() { }
    }
    private static async Task WaitForHeartbeatAsync(ProcessingStatusStore store, DateTimeOffset expected,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while ((await store.GetAsync(timeout.Token)).ParentUriBackfill.Activity.HeartbeatAt != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class ProcessingFailureLogger : ILogger
    {
        internal Channel<Exception> Failures { get; } = Channel.CreateUnbounded<Exception>();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 27 && exception is not null)
            {
                Failures.Writer.TryWrite(exception);
            }
        }
    }

    private static async Task VerifyStatisticsTimestampAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using (var admin = new MySqlConnection(options.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using var command = admin.CreateCommand();
            command.CommandText = "CREATE DATABASE statistics_timestamp_test";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        options.Database = "statistics_timestamp_test";
        await using var source = new MySqlDataSourceBuilder(options.ConnectionString).Build();
        var statistics = new StatisticsStore(source, TimeProvider.System);
        DateTimeOffset? persisted;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        {
            var monitor = new MonitorStore(connection);
            await monitor.InitializeAsync(cancellationToken);
            Assert.IsNull((await statistics.GetLastUpdatedAsync(cancellationToken)).UpdatedAt);
            var hit = new Hit(new DateTimeOffset(2006, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new AtUri("at://did:plc:timestamp/app.bsky.feed.post/first"), new Did("did:plc:timestamp"),
                StatisticsStore.RightJerryDid, ParentPost(StatisticsStore.RightJerryDid, "timestamp-parent"));
            await monitor.SaveHitAsync(hit, cancellationToken);
            var first = (await statistics.GetLastUpdatedAsync(cancellationToken)).UpdatedAt;
            Assert.IsNotNull(first);
            Assert.IsTrue(Math.Abs((DateTimeOffset.UtcNow - first.Value).TotalMinutes) < 1);
            Assert.AreEqual(TimeSpan.Zero, first.Value.Offset);

            await using var command = connection.CreateCommand();
            // Simulate upgrading a database with hits but no recorded ingestion time.
            command.CommandText = "DELETE FROM StatisticsUpdate";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await monitor.SaveHitAsync(hit, cancellationToken);
            Assert.IsNull((await statistics.GetLastUpdatedAsync(cancellationToken)).UpdatedAt);

            await monitor.SaveHitAsync(hit with { AtUri = new AtUri("at://did:plc:timestamp/app.bsky.feed.post/second") },
                cancellationToken);
            command.CommandText = "UPDATE StatisticsUpdate SET UpdatedAt = '2020-01-01 00:00:00' WHERE Id = 1";
            await command.ExecuteNonQueryAsync(cancellationToken);
            var sentinel = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await monitor.SaveHitAsync(hit, cancellationToken);
            Assert.AreEqual(sentinel, (await statistics.GetLastUpdatedAsync(cancellationToken)).UpdatedAt);
            command.CommandText = """
                ALTER TABLE Hits
                ADD CONSTRAINT CK_Hits_TestFailure CHECK (
                    AtUri <> 'at://did:plc:timestamp/app.bsky.feed.post/forced-failure')
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await Assert.ThrowsAsync<MySqlException>(() => monitor.SaveHitAsync(
                hit with { AtUri = new AtUri("at://did:plc:timestamp/app.bsky.feed.post/forced-failure") },
                cancellationToken));
            command.CommandText = "ALTER TABLE Hits DROP CHECK CK_Hits_TestFailure";
            await command.ExecuteNonQueryAsync(cancellationToken);
            Assert.AreEqual(sentinel, (await statistics.GetLastUpdatedAsync(cancellationToken)).UpdatedAt,
                "The timestamp must roll back when the hit insertion fails.");

            await monitor.SaveHitAsync(hit with { AtUri = new AtUri("at://did:plc:timestamp/app.bsky.feed.post/third") },
                cancellationToken);
            persisted = (await statistics.GetLastUpdatedAsync(cancellationToken)).UpdatedAt;
            Assert.IsTrue(persisted > sentinel);
            await new ActorStore(connection).SaveAsync(new ActorRefreshRequest(hit.AuthorDid, 0),
                new Handle("timestamp.example"), cancellationToken);
            Assert.AreEqual(persisted, (await statistics.GetLastUpdatedAsync(cancellationToken)).UpdatedAt);
        }

        await using var restarted = await source.OpenConnectionAsync(cancellationToken);
        await new MonitorStore(restarted).InitializeAsync(cancellationToken);
        Assert.AreEqual(persisted, (await statistics.GetLastUpdatedAsync(cancellationToken)).UpdatedAt,
            "Restart must retain the persisted statistics timestamp.");
    }

    private static async Task VerifyActorUpdaterStartupAsync(string connectionString,
        CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using (var admin = new MySqlConnection(options.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using var command = admin.CreateCommand();
            command.CommandText = "CREATE DATABASE actor_updater_startup";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        options.Database = "actor_updater_startup";
        await using var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE notes (id INT PRIMARY KEY); INSERT INTO notes VALUES (1)";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var store = await ActorHandleUpdater.InitializeStoreAsync(connection, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'notes'
                """;
            Assert.AreEqual(0L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
        }
        Assert.IsEmpty(await store.GetDueAsync(cancellationToken),
            "The updater must be able to start against a fresh database before either the API or replay loop.");
        store = await ActorHandleUpdater.InitializeStoreAsync(connection, cancellationToken);
        Assert.IsEmpty(await store.GetDueAsync(cancellationToken), "Schema initialization must remain idempotent.");
        await using var competitor = new MySqlConnection(options.ConnectionString);
        await competitor.OpenAsync(cancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ActorHandleUpdater.InitializeStoreAsync(competitor, cancellationToken));
    }

    private static async Task VerifyParentUriBackfillAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using (var admin = new MySqlConnection(options.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using var command = admin.CreateCommand();
            command.CommandText = "CREATE DATABASE parent_uri_backfill_test";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        options.Database = "parent_uri_backfill_test";
        const string availableHit = "at://did:plc:reply/app.bsky.feed.post/available";
        const string retryHit = "at://did:plc:reply/app.bsky.feed.post/retry";
        const string unavailableHit = "at://did:plc:reply/app.bsky.feed.post/unavailable";
        const string expectedParent = "at://did:plc:parent/app.bsky.feed.post/parent";
        await using var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE Hits (
                    AtUriHash BINARY(32) NOT NULL PRIMARY KEY,
                    AtUri VARCHAR(8192) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    CreatedAt DATETIME(6) NOT NULL,
                    AuthorDid VARCHAR(2048) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    ParentAuthorDid VARCHAR(2048) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    INDEX IX_Hits_AuthorDid (AuthorDid),
                    INDEX IX_Hits_ParentAuthorDid (ParentAuthorDid)
                ) ENGINE=InnoDB
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            foreach ((string uri, string parentDid) in new[]
            {
                (availableHit, "did:plc:parent"),
                (retryHit, "did:plc:parent"),
                (unavailableHit, "did:plc:goneparent")
            })
            {
                command.CommandText = """
                    INSERT INTO Hits (AtUriHash, AtUri, CreatedAt, AuthorDid, ParentAuthorDid)
                    VALUES (@hash, @uri, UTC_TIMESTAMP(6), 'did:plc:reply', @parentDid)
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@hash", SHA256.HashData(Encoding.UTF8.GetBytes(uri)));
                command.Parameters.AddWithValue("@uri", uri);
                command.Parameters.AddWithValue("@parentDid", parentDid);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await MonitorSchema.InitializeAsync(connection, cancellationToken);
        await MonitorSchema.InitializeAsync(connection, cancellationToken);
        await VerifyBackfillStoreAsync(options.ConnectionString, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM Hits
                WHERE ParentAtUri IS NULL AND ParentAtUriHash IS NULL AND ParentUriBackfillStatus = 0
                """;
            Assert.AreEqual(3L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)),
                "An existing Hits table must migrate to clearly unattempted rows.");
        }

        await using var dataSource = new MySqlDataSourceBuilder(options.ConnectionString).Build();
        var processing = new ProcessingStatusStore(dataSource, TimeProvider.System);
        var firstCalls = 0;
        using (var firstHttp = new HttpClient(new StubHandler(_ =>
        {
            firstCalls++;
            Assert.AreEqual("backfill-running", processing.GetAsync(cancellationToken).GetAwaiter().GetResult()
                .ParentUriBackfill.Activity.State, "Activity must be visible during an actual network request.");
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            return response;
        })))
        using (var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var firstClient = new ParentPostClient(firstHttp);
            var backfiller = new ParentUriBackfiller(dataSource, firstClient, NullLogger.Instance,
                wait: (_, _) =>
                {
                    shutdown.Cancel();
                    return Task.FromCanceled(shutdown.Token);
                });
            await Assert.ThrowsAsync<OperationCanceledException>(() => backfiller.RunAsync(shutdown.Token));
        }

        Assert.AreEqual(1, firstCalls);
        Assert.AreEqual("stopped", (await processing.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ParentUriBackfillStatus, ParentUriBackfillAttemptCount,
                    TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), ParentUriBackfillNextAttemptAt)
                FROM Hits WHERE AtUri = @uri
                """;
            command.Parameters.AddWithValue("@uri", retryHit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.IsTrue(await reader.ReadAsync(cancellationToken));
            Assert.AreEqual((byte)ParentUriBackfillStatus.RetryPending, reader.GetByte(0));
            Assert.AreEqual(1, reader.GetInt32(1));
            Assert.IsTrue(reader.GetInt64(2) is >= 0 and <= 1);
        }

        using var retryHttp = new HttpClient(new StubHandler(_ => Json(new
        {
            posts = new[]
            {
                new { uri = availableHit, record = ParentPostRecord(expectedParent) },
                new { uri = retryHit, record = ParentPostRecord(expectedParent) }
            }
        })));
        var retryClient = new ParentPostClient(retryHttp);
        await new ParentUriBackfiller(dataSource, retryClient, NullLogger.Instance)
            .RunAsync(cancellationToken);
        ParentUriProcessingStatus completed = (await processing.GetAsync(cancellationToken)).ParentUriBackfill;
        Assert.AreEqual("completed", completed.Activity.State);
        Assert.AreEqual(0L, completed.Pending + completed.RetryPending);
        Assert.AreEqual(2L, completed.Resolved);
        Assert.AreEqual(1L, completed.Unavailable);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT AtUri, ParentAtUri, ParentAtUriHash, ParentUriBackfillStatus,
                    ParentUriBackfillAttemptCount
                FROM Hits ORDER BY AtUri
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new Dictionary<string, (string? ParentUri, byte Status, int Attempts)>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                string uri = reader.GetString(0);
                string? parentUri = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (uri == availableHit || uri == retryHit)
                {
                    Assert.AreEqual(expectedParent, parentUri);
                    CollectionAssert.AreEqual(SHA256.HashData(Encoding.UTF8.GetBytes(expectedParent)),
                        (byte[])reader.GetValue(2));
                    Assert.AreEqual((byte)ParentUriBackfillStatus.Resolved, reader.GetByte(3));
                    Assert.AreEqual(0, reader.GetInt32(4));
                }
                else
                {
                    Assert.IsNull(parentUri);
                    Assert.IsTrue(reader.IsDBNull(2));
                    Assert.AreEqual((byte)ParentUriBackfillStatus.Unavailable, reader.GetByte(3),
                        "An omitted post must be persisted as unavailable, not retried as a transient failure.");
                }

                rows.Add(uri, (parentUri, reader.GetByte(3), reader.GetInt32(4)));
            }

            Assert.AreEqual(3, rows.Count);
        }

        var unexpectedRequests = 0;
        using var noRetryHttp = new HttpClient(new StubHandler(_ =>
        {
            unexpectedRequests++;
            return Json(new { posts = Array.Empty<object>() });
        }));
        await new ParentUriBackfiller(dataSource, new ParentPostClient(noRetryHttp), NullLogger.Instance)
            .RunAsync(cancellationToken);
        Assert.AreEqual(0, unexpectedRequests,
            "Resolved and unavailable hits must not be fetched on a later backfill invocation.");

        const string rateLimitedHit = "at://did:plc:reply/app.bsky.feed.post/rate-limited";
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Hits (AtUriHash, AtUri, CreatedAt, AuthorDid, ParentAuthorDid)
                VALUES (@hash, @uri, UTC_TIMESTAMP(6), 'did:plc:reply', 'did:plc:parent')
                """;
            command.Parameters.AddWithValue("@hash", SHA256.HashData(Encoding.UTF8.GetBytes(rateLimitedHit)));
            command.Parameters.AddWithValue("@uri", rateLimitedHit);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var rateLimitHttp = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        })))
        using (var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var backfiller = new ParentUriBackfiller(dataSource,
                new ParentPostClient(rateLimitHttp), NullLogger.Instance, wait: async (_, _) =>
                {
                    Assert.AreEqual("retrying", (await processing.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);
                    await shutdown.CancelAsync();
                    await Task.FromCanceled(shutdown.Token);
                });
            await Assert.ThrowsAsync<OperationCanceledException>(() => backfiller.RunAsync(shutdown.Token));
        }

        Assert.AreEqual("stopped", (await processing.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ParentUriBackfillStatus, ParentUriBackfillAttemptCount,
                    TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), ParentUriBackfillNextAttemptAt)
                FROM Hits WHERE AtUri = @uri
                """;
            command.Parameters.AddWithValue("@uri", rateLimitedHit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.IsTrue(await reader.ReadAsync(cancellationToken));
            Assert.AreEqual((byte)ParentUriBackfillStatus.RetryPending, reader.GetByte(0));
            Assert.AreEqual(1, reader.GetInt32(1));
            Assert.IsTrue(reader.GetInt64(2) is >= 599 and <= 600,
                "A Retry-After cooldown must survive restarting the one-off command.");
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE Hits SET ParentUriBackfillNextAttemptAt = UTC_TIMESTAMP(6)
                WHERE ParentUriBackfillStatus = 3
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        using (var failedHttp = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest))))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                new ParentUriBackfiller(dataSource, new ParentPostClient(failedHttp), NullLogger.Instance)
                    .RunAsync(cancellationToken));
        }
        Assert.AreEqual("failure", (await processing.GetAsync(cancellationToken)).ParentUriBackfill.Activity.State);

        await using var cursor = connection.CreateCommand();
        cursor.CommandText = "SELECT COUNT(*) FROM JetstreamReplayProgress";
        Assert.AreEqual(0L, Convert.ToInt64(await cursor.ExecuteScalarAsync(cancellationToken)),
            "The explicit backfill must not create or modify Jetstream replay cursors.");
    }

    private static object ParentPostRecord(string parentUri)
    {
        return new
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
        };
    }

    private static async Task VerifyBackfillStoreAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var store = new ParentUriBackfillStore(connection);
            await store.InitializeAsync(cancellationToken);
            IReadOnlyList<ParentUriBackfillRequest> due = await store.GetDueBatchAsync(cancellationToken);
            Assert.AreEqual(3, due.Count);
            Assert.IsNull(await store.GetNextRetryAtAsync(cancellationToken));
            var missing = new ParentUriBackfillRequest(
                new AtUri("at://did:plc:missing/app.bsky.feed.post/missing"), new Did("did:plc:parent"), 0);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveResultsAsync(
                [new(due[0], null), new(missing, null)], cancellationToken));
            Assert.AreEqual(3, (await store.GetDueBatchAsync(cancellationToken)).Count,
                "A failed result batch must roll back earlier updates.");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.SaveRetryTimesAsync([due[0], missing], TimeSpan.Zero, cancellationToken));
            Assert.IsNull(await store.GetNextRetryAtAsync(cancellationToken),
                "A failed retry batch must not leave partial durable cooldowns.");

            await using var competitor = new MySqlConnection(connectionString);
            await competitor.OpenAsync(cancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ParentUriBackfillStore(competitor).InitializeAsync(cancellationToken));

            foreach (int number in new[] { 1045, 1146 })
            {
                int waits = 0;
                MySqlException failure = await Assert.ThrowsAsync<MySqlException>(() =>
                    BackfillDatabaseRetry.RunAsync(async (_, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = $"SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = {number}";
                        await command.ExecuteNonQueryAsync(token);
                    }, NullLogger.Instance, cancellationToken, (_, _) =>
                    {
                        waits++;
                        return Task.CompletedTask;
                    }));
                Assert.AreEqual(number, failure.Number);
                Assert.IsFalse(failure.IsTransient);
                Assert.AreEqual(0, waits, "Authentication/schema failures must not be retried.");
            }
        }

        int attempts = 0;
        var delays = new List<double>();
        await BackfillDatabaseRetry.RunAsync(async (_, token) =>
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(token);
            var store = new ParentUriBackfillStore(connection);
            await store.InitializeAsync(token);
            if (++attempts <= 2)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SIGNAL SQLSTATE '40001' SET MYSQL_ERRNO = 1213";
                await command.ExecuteNonQueryAsync(token);
            }

            Assert.AreEqual(3, (await store.GetDueBatchAsync(token)).Count,
                "Recovery must reacquire the session lock and resume the untouched durable queue.");
        }, NullLogger.Instance, cancellationToken, (delay, _) =>
        {
            delays.Add(delay.TotalSeconds);
            return Task.CompletedTask;
        });
        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(new double[] { 1, 5 }, delays);
    }

    private static HttpResponseMessage Json(object body)
    {
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8,
                "application/json")
        };
    }

    private static AtUri ParentPost(string did, string key)
    {
        return new AtUri($"at://{did}/app.bsky.feed.post/{key}");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }

    private static async Task VerifyMonthlyRepliesAsync(HttpClient api, string connectionString,
        CancellationToken cancellationToken)
    {
        await using var dataSource = new MySqlDataSourceBuilder(connectionString).Build();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var monitor = new MonitorStore(connection);
        // The fixed window crosses a year boundary and includes leap-day February.
        var now = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var clock = new StatisticsClock(now);
        var store = new StatisticsStore(dataSource, clock);
        var before = await store.GetMonthlyRightJerryRepliesAsync(cancellationToken);
        Assert.IsTrue(before.All(month => month.ReplyCount == 0));
        Assert.IsEmpty(await store.GetAllTimeRightJerryRepliesAsync(cancellationToken));
        var timestamps = new[]
        {
            new DateTimeOffset(2023, 9, 30, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2023, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2023, 12, 31, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2024, 3, 1, 1, 0, 0, TimeSpan.FromHours(2)),
            now,
            now.AddSeconds(1),
            new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero)
        };
        for (var index = 0; index < timestamps.Length; index++)
        {
            var hit = new Hit(timestamps[index], new AtUri($"at://did:plc:monthly/app.bsky.feed.post/{index}"),
                new Did("did:plc:monthly"), StatisticsStore.RightJerryDid,
                ParentPost(StatisticsStore.RightJerryDid, $"monthly-parent-{index}"));
            await monitor.SaveHitAsync(hit, cancellationToken);
            await monitor.SaveHitAsync(hit, cancellationToken);
        }

        await monitor.SaveHitAsync(new Hit(now, new AtUri("at://did:plc:monthly/app.bsky.feed.post/wrong"),
            new Did("did:plc:monthly"), new Did("did:plc:wrongjerry"),
            ParentPost("did:plc:wrongjerry", "monthly-wrong-parent")), cancellationToken);
        var months = await store.GetMonthlyRightJerryRepliesAsync(cancellationToken);
        CollectionAssert.AreEqual(new[]
        {
            new MonthlyReplyCount(new DateOnly(2023, 10, 1), 1),
            new MonthlyReplyCount(new DateOnly(2023, 11, 1), 0),
            new MonthlyReplyCount(new DateOnly(2023, 12, 1), 1),
            new MonthlyReplyCount(new DateOnly(2024, 1, 1), 1),
            new MonthlyReplyCount(new DateOnly(2024, 2, 1), 2),
            new MonthlyReplyCount(new DateOnly(2024, 3, 1), 1)
        }, months.ToArray());
        var history = await store.GetAllTimeRightJerryRepliesAsync(cancellationToken);
        Assert.AreEqual(7, history.Count);
        Assert.AreEqual(new MonthlyReplyCount(new DateOnly(2023, 9, 1), 1), history[0]);
        CollectionAssert.AreEqual(months.ToArray(), history.Skip(1).ToArray());

        var recent = DateTimeOffset.UtcNow.AddDays(-1);
        await monitor.SaveHitAsync(new Hit(recent, new AtUri("at://did:plc:monthly/app.bsky.feed.post/recent"),
            new Did("did:plc:monthly"), StatisticsStore.RightJerryDid,
            ParentPost(StatisticsStore.RightJerryDid, "monthly-recent-parent")), cancellationToken);
        using var response = await api.GetAsync("/statistics/right-jerry/monthly-replies", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var apiMonths = await response.Content.ReadFromJsonAsync<MonthlyReplyCount[]>(cancellationToken);
        Assert.IsNotNull(apiMonths);
        Assert.AreEqual(6, apiMonths.Length);
        Assert.IsTrue(apiMonths.Any(month =>
            month.Month == new DateOnly(recent.Year, recent.Month, 1) && month.ReplyCount > 0));
        for (var index = 1; index < apiMonths.Length; index++)
        {
            Assert.AreEqual(apiMonths[index - 1].Month.AddMonths(1), apiMonths[index].Month);
        }

        using var json = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.AreEqual(apiMonths[0].Month.ToString("yyyy-MM-dd"), json.RootElement[0].GetProperty("month").GetString());
        Assert.AreEqual(apiMonths[0].ReplyCount, json.RootElement[0].GetProperty("replyCount").GetInt64());
        Assert.AreEqual(2, json.RootElement[0].EnumerateObject().Count());
        using var historyResponse = await api.GetAsync(
            "/statistics/right-jerry/all-time-monthly-replies", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, historyResponse.StatusCode);
        var apiHistory = await historyResponse.Content.ReadFromJsonAsync<MonthlyReplyCount[]>(cancellationToken);
        Assert.IsNotNull(apiHistory);
        Assert.AreEqual(new DateOnly(2023, 9, 1), apiHistory[0].Month);
        Assert.AreEqual(apiMonths[^1].Month, apiHistory[^1].Month);
        for (var index = 1; index < apiHistory.Length; index++)
        {
            Assert.AreEqual(apiHistory[index - 1].Month.AddMonths(1), apiHistory[index].Month);
        }
    }

    private sealed class StatisticsClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task VerifyTopReplyAuthorsAsync(HttpClient api, string connectionString,
        CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var store = new MonitorStore(connection);
        await store.InitializeAsync(cancellationToken);
        var expected = new List<TopReplyAuthor>();
        var expectedAll = new List<TopReplyAuthor>();
        for (var index = 0; index < 12; index++)
        {
            var did = $"did:plc:rank{index:D2}";
            var count = index switch { 0 => 6, 1 => 5, _ => 3 };
            for (var post = 0; post < count; post++)
            {
                var hit = new Hit(DateTimeOffset.UtcNow, new AtUri($"at://{did}/app.bsky.feed.post/{post}"),
                    new Did(did), StatisticsStore.RightJerryDid,
                    ParentPost(StatisticsStore.RightJerryDid, $"rank-parent-{index}-{post}"));
                await store.SaveHitAsync(hit, cancellationToken);
                await store.SaveHitAsync(hit, cancellationToken);
            }

            if (index < 10)
            {
                expected.Add(new TopReplyAuthor(did, index == 0 ? "first.example" : null, count));
            }
            expectedAll.Add(new TopReplyAuthor(did, index == 0 ? "first.example" : null, count));
        }

        for (var post = 0; post < 20; post++)
        {
            await store.SaveHitAsync(new Hit(DateTimeOffset.UtcNow,
                new AtUri($"at://did:plc:wrongonly/app.bsky.feed.post/{post}"),
                new Did("did:plc:wrongonly"), new Did("did:plc:otherjerry"),
                ParentPost("did:plc:otherjerry", $"wrong-parent-{post}")),
                cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Actor SET Handle = 'first.example' WHERE Did = 'did:plc:rank00'";
        await command.ExecuteNonQueryAsync(cancellationToken);
        using var response = await api.GetAsync("/statistics/right-jerry/top-authors", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var authors = await response.Content.ReadFromJsonAsync<TopReplyAuthor[]>(cancellationToken);
        Assert.IsNotNull(authors);
        CollectionAssert.AreEqual(expected, authors,
            "Only ten authors should appear, ranked by deduplicated right-Jerry posts, with deterministic ties.");
        using var document = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var first = document.RootElement[0];
        Assert.AreEqual("did:plc:rank00", first.GetProperty("did").GetString());
        Assert.AreEqual("first.example", first.GetProperty("handle").GetString());
        Assert.AreEqual(6L, first.GetProperty("replyCount").GetInt64());
        Assert.AreEqual(3, first.EnumerateObject().Count());
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, document.RootElement[1].GetProperty("handle").ValueKind);
        using var allResponse = await api.GetAsync("/statistics/right-jerry/authors", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, allResponse.StatusCode);
        var allAuthors = await allResponse.Content.ReadFromJsonAsync<TopReplyAuthor[]>(cancellationToken);
        Assert.IsNotNull(allAuthors);
        CollectionAssert.AreEqual(expectedAll, allAuthors.Where(author => author.Did.StartsWith("did:plc:rank")).ToArray(),
            "The full list must include authors beyond the top ten, with deduplicated counts and deterministic ties.");
        CollectionAssert.AreEqual(authors, allAuthors.Take(10).ToArray());
        Assert.IsFalse(allAuthors.Any(author => author.Did == "did:plc:wrongonly"));
        Assert.AreEqual(allAuthors.Length, allAuthors.Select(author => author.Did).Distinct().Count());
        var summary = await api.GetFromJsonAsync<ReplySummary>("/statistics/reply-summary", cancellationToken);
        Assert.IsNotNull(summary);
        Assert.AreEqual(summary.RightJerryReplies, allAuthors.Sum(author => author.ReplyCount));

        command.CommandText = "UPDATE Actor SET Handle = 'renamed.example' WHERE Did = 'did:plc:rank00'";
        await command.ExecuteNonQueryAsync(cancellationToken);
        var renamed = await api.GetFromJsonAsync<TopReplyAuthor[]>("/statistics/right-jerry/top-authors", cancellationToken);
        Assert.IsNotNull(renamed);
        Assert.AreEqual(new TopReplyAuthor("did:plc:rank00", "renamed.example", 6), renamed[0],
            "Handle updates should not change counts or create a second ranking entry.");
        var renamedAll = await api.GetFromJsonAsync<TopReplyAuthor[]>("/statistics/right-jerry/authors", cancellationToken);
        Assert.IsNotNull(renamedAll);
        Assert.AreEqual(renamed[0], renamedAll[0]);
    }

    private static async Task VerifyTopReplyPostsAsync(HttpClient api, string connectionString,
        CancellationToken cancellationToken)
    {
        await using var source = new MySqlDataSourceBuilder(connectionString).Build();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var monitor = new MonitorStore(connection);
        var expected = new List<TopReplyPost>();
        for (var index = 6; index >= 0; index--)
        {
            var parent = ParentPost(StatisticsStore.RightJerryDid, $"top-post-{index}");
            var count = index switch { 0 => 9, 1 => 8, _ => 7 };
            for (var reply = 0; reply < count; reply++)
            {
                var author = new Did($"did:plc:postrank{reply}");
                var hit = new Hit(DateTimeOffset.UtcNow,
                    new AtUri($"at://{author}/app.bsky.feed.post/top-{index}"),
                    author, StatisticsStore.RightJerryDid, parent);
                await monitor.SaveHitAsync(hit, cancellationToken);
                await monitor.SaveHitAsync(hit, cancellationToken);
            }

            if (index < 5)
            {
                expected.Insert(0, new TopReplyPost(parent.ToString(), count));
            }
        }

        for (var reply = 0; reply < 20; reply++)
        {
            var author = new Did("did:plc:postexcluded");
            await monitor.SaveHitAsync(new Hit(DateTimeOffset.UtcNow,
                new AtUri($"at://{author}/app.bsky.feed.post/wrong-{reply}"),
                author, new Did("did:plc:wrongjerry"), ParentPost("did:plc:wrongjerry", "excluded")),
                cancellationToken);
            await monitor.SaveHitAsync(new Hit(DateTimeOffset.UtcNow,
                new AtUri($"at://{author}/app.bsky.feed.post/unresolved-{reply}"),
                author, StatisticsStore.RightJerryDid, ParentPost(StatisticsStore.RightJerryDid, "unresolved")),
                cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Hits SET ParentAtUri = NULL, ParentAtUriHash = NULL, ParentUriBackfillStatus = 2
            WHERE AuthorDid = @excluded AND ParentAuthorDid = @rightJerry;
            UPDATE Hits SET ParentAtUriHash = @collisionHash
            WHERE ParentAtUri IN (@firstParent, @secondParent);
            """;
        command.Parameters.AddWithValue("@excluded", "did:plc:postexcluded");
        command.Parameters.AddWithValue("@rightJerry", StatisticsStore.RightJerryDid.ToString());
        command.Parameters.AddWithValue("@collisionHash", SHA256.HashData(Encoding.UTF8.GetBytes("simulated collision")));
        command.Parameters.AddWithValue("@firstParent", expected[2].AtUri);
        command.Parameters.AddWithValue("@secondParent", expected[3].AtUri);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var statistics = new StatisticsStore(source, TimeProvider.System);
        CollectionAssert.AreEqual(expected, (await statistics.GetTopRightJerryPostsAsync(cancellationToken)).ToArray(),
            "Repeated replies group by parent, not author; duplicate deliveries and unresolved/wrong-Jerry rows are excluded. " +
            "Full URIs separate hash collisions, and ties at the five-post cutoff sort by URI.");
        using var response = await api.GetAsync("/statistics/right-jerry/top-posts", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var posts = await response.Content.ReadFromJsonAsync<TopReplyPost[]>(cancellationToken);
        Assert.IsNotNull(posts);
        CollectionAssert.AreEqual(expected, posts);
        using var json = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        for (var index = 0; index < expected.Count; index++)
        {
            var post = json.RootElement[index];
            Assert.AreEqual(expected[index].AtUri, post.GetProperty("atUri").GetString());
            Assert.AreEqual(expected[index].ReplyCount, post.GetProperty("replyCount").GetInt64());
            Assert.AreEqual(2, post.EnumerateObject().Count());
        }
    }

    private static async Task VerifyReplySummaryAsync(HttpClient api, string connectionString,
        CancellationToken cancellationToken)
    {
        var options = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new MySqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var store = new MonitorStore(connection);
        await store.InitializeAsync(cancellationToken);
        var hit = new Hit(DateTimeOffset.UtcNow,
            new AtUri($"at://{StatisticsStore.RightJerryDid}/app.bsky.feed.post/right-one"),
            StatisticsStore.RightJerryDid, StatisticsStore.RightJerryDid,
            ParentPost(StatisticsStore.RightJerryDid, "summary-parent"));
        await store.SaveHitAsync(hit, cancellationToken);
        await store.SaveHitAsync(hit, cancellationToken);
        await store.SaveHitAsync(hit with
        {
            AtUri = new AtUri("at://did:plc:anotherauthor/app.bsky.feed.post/right-two"),
            AuthorDid = new Did("did:plc:anotherauthor")
        }, cancellationToken);

        using var response = await api.GetAsync("/statistics/reply-summary", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<ReplySummary>(cancellationToken);
        Assert.AreEqual(new ReplySummary(5, 2, 3), summary);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual(5, document.RootElement.GetProperty("totalReplies").GetInt64());
        Assert.AreEqual(2, document.RootElement.GetProperty("rightJerryReplies").GetInt64());
        Assert.AreEqual(3, document.RootElement.GetProperty("wrongJerryReplies").GetInt64());
        Assert.AreEqual(3, document.RootElement.EnumerateObject().Count());
    }

    private static async Task VerifyMonitorStorageAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connectionOptions = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        var hit = ReplyMatcher.Match(ReplyMatcherTests.CreateEvent(), NullLogger.Instance);
        Assert.IsNotNull(hit);
        var progress = new MonitorProgress
        {
            Service = "wss://jetstream.us-west.bsky.network",
            ArchiveCheckpoint = new SnapshotCheckpoint
            {
                SealedTipSeq = 500,
                PlanAfterSeq = 0,
                SegmentName = "segment-1",
                SegmentChecksum = "checksum-1",
                NextBlockIndex = 4,
                NextByteOffset = 123456,
                RequestFingerprint = "request-fingerprint"
            }
        };

        await using (var connection = new MySqlConnection(connectionOptions.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var monitor = new MonitorStore(connection);
            await monitor.InitializeAsync(cancellationToken);
            Assert.IsNull(await monitor.LoadProgressAsync(cancellationToken));
            await monitor.SaveHitAsync(hit, cancellationToken);
            monitor.SaveProgress(progress);

            await using var competitor = new MySqlConnection(connectionOptions.ConnectionString);
            await competitor.OpenAsync(cancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new MonitorStore(competitor).InitializeAsync(cancellationToken));
        }

        // A new physical connection represents a restarted monitor. Replayed hits must remain unique.
        await using var restarted = new MySqlConnection(connectionOptions.ConnectionString);
        await restarted.OpenAsync(cancellationToken);
        var resumed = new MonitorStore(restarted);
        await resumed.InitializeAsync(cancellationToken);
        Assert.AreEqual(progress, await resumed.LoadProgressAsync(cancellationToken));
        await resumed.SaveHitAsync(hit with
        {
            ParentAtUri = ParentPost(hit.ParentAuthorDid.ToString(), "duplicate-must-not-replace-parent")
        }, cancellationToken);

        await using var command = restarted.CreateCommand();
        command.CommandText = """
            SELECT AtUri, CreatedAt, AuthorDid, ParentAuthorDid, ParentAtUri, ParentAtUriHash,
                ParentUriBackfillStatus
            FROM Hits
            """;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            Assert.IsTrue(await reader.ReadAsync(cancellationToken));
            Assert.AreEqual(hit.AtUri.ToString(), reader.GetString(0));
            Assert.AreEqual(hit.CreatedAt.UtcDateTime, reader.GetDateTime(1));
            Assert.AreEqual(hit.AuthorDid.ToString(), reader.GetString(2));
            Assert.AreEqual(hit.ParentAuthorDid.ToString(), reader.GetString(3));
            Assert.AreEqual(hit.ParentAtUri.ToString(), reader.GetString(4));
            CollectionAssert.AreEqual(SHA256.HashData(Encoding.UTF8.GetBytes(hit.ParentAtUri.ToString())),
                (byte[])reader.GetValue(5));
            Assert.AreEqual((byte)ParentUriBackfillStatus.Resolved, reader.GetByte(6));
            Assert.IsFalse(await reader.ReadAsync(cancellationToken), "Repeated archive events must not duplicate hits.");
        }

        command.CommandText = """
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Hits' AND NON_UNIQUE = 1
            ORDER BY COLUMN_NAME
            """;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            var indexedColumns = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                indexedColumns.Add(reader.GetString(0));
            }

            CollectionAssert.AreEqual(new[]
            {
                "AuthorDid", "ParentAtUriHash", "ParentAuthorDid", "ParentUriBackfillNextAttemptAt",
                "ParentUriBackfillStatus"
            }, indexedColumns);
        }

        command.CommandText = "SELECT Did, Handle, UpdatedAt FROM Actor ORDER BY Did";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            var actors = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                actors.Add(reader.GetString(0));
                Assert.IsTrue(reader.IsDBNull(1), "Unknown handles must remain null.");
                Assert.IsFalse(reader.IsDBNull(2), "An actor must have its row creation time.");
                Assert.IsTrue(Math.Abs((DateTime.UtcNow - reader.GetDateTime(2)).TotalMinutes) < 5,
                    "UpdatedAt must be the row creation time, not the historical post time.");
            }

            CollectionAssert.AreEquivalent(new[] { hit.AuthorDid.ToString(), hit.ParentAuthorDid.ToString() }, actors);
        }

        command.CommandText = """
            UPDATE Actor SET Handle = 'actor.example', UpdatedAt = '2026-01-01 00:00:00'
            WHERE Did = @did
            """;
        command.Parameters.AddWithValue("@did", hit.AuthorDid.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await resumed.SaveHitAsync(hit, cancellationToken);
        command.CommandText = "SELECT Handle, UpdatedAt FROM Actor WHERE Did = @did";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            Assert.IsTrue(await reader.ReadAsync(cancellationToken));
            Assert.AreEqual("actor.example", reader.GetString(0));
            Assert.AreEqual(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), reader.GetDateTime(1));
        }

        await resumed.SaveHitAsync(hit with
        {
            AtUri = new AtUri("at://did:plc:author/app.bsky.feed.post/newparent"),
            ParentAuthorDid = new Did("did:plc:newparent"),
            ParentAtUri = ParentPost("did:plc:newparent", "newparent")
        }, cancellationToken);
        command.CommandText = "SELECT COUNT(*) FROM Actor WHERE Did = 'did:plc:newparent' AND UpdatedAt IS NOT NULL";
        Assert.AreEqual(1L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)),
            "A missing parent actor must be added even when the author already exists.");

        var selfReply = hit with
        {
            AtUri = new AtUri("at://did:plc:self/app.bsky.feed.post/self"),
            AuthorDid = new Did("did:plc:self"),
            ParentAuthorDid = new Did("did:plc:self"),
            ParentAtUri = ParentPost("did:plc:self", "self-parent")
        };
        await resumed.SaveHitAsync(selfReply, cancellationToken);
        await resumed.SaveHitAsync(selfReply, cancellationToken);
        command.CommandText = "SELECT COUNT(*) FROM Actor WHERE Did = 'did:plc:self'";
        Assert.AreEqual(1L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));

        var invalidHit = hit with
        {
            AtUri = new AtUri("at://did:plc:timestamp/app.bsky.feed.post/rollback"),
            AuthorDid = new Did("did:plc:rollbackauthor"),
            ParentAuthorDid = new Did("did:plc:rollbackparent"),
            ParentAtUri = ParentPost("did:plc:rollbackparent", "rollback-parent")
        };
        command.CommandText = """
            ALTER TABLE Hits
            ADD CONSTRAINT CK_Hits_TestFailure CHECK (AtUri <> 'at://did:plc:timestamp/app.bsky.feed.post/rollback')
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await Assert.ThrowsAsync<MySqlException>(() => resumed.SaveHitAsync(invalidHit, cancellationToken));
        command.CommandText = "ALTER TABLE Hits DROP CHECK CK_Hits_TestFailure";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            SELECT COUNT(*) FROM Actor WHERE Did IN ('did:plc:rollbackauthor', 'did:plc:rollbackparent')
            """;
        Assert.AreEqual(0L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)),
            "Actor inserts must roll back if the hit cannot be saved.");

        var actorStore = new ActorStore(restarted);
        var pendingActors = await actorStore.GetDueAsync(cancellationToken);
        Assert.AreEqual(4, pendingActors.Count, "Duplicate hits must not create duplicate refresh jobs.");
        var authorRequest = pendingActors.Single(actor => actor.Did == hit.AuthorDid);
        await actorStore.SaveAsync(authorRequest, new Handle("renamed.example"), cancellationToken);
        command.CommandText = "SELECT Handle, UpdatedAt FROM Actor WHERE Did = @did";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            Assert.IsTrue(await reader.ReadAsync(cancellationToken));
            Assert.AreEqual("renamed.example", reader.GetString(0));
            Assert.IsTrue(reader.GetDateTime(1) > new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                "Writing a handle must refresh UpdatedAt.");
        }

        Assert.IsFalse((await actorStore.GetDueAsync(cancellationToken)).Any(actor => actor.Did == hit.AuthorDid));
        await actorStore.InvalidateAsync(new Did("did:plc:untracked"), cancellationToken);
        command.CommandText = "SELECT COUNT(*) FROM Actor WHERE Did = 'did:plc:untracked'";
        Assert.AreEqual(0L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
        await JetstreamMonitor.ProcessAsync(new JetstreamIdentityEvent
        {
            Did = new Did(hit.AuthorDid),
            Kind = JetStreamEventKind.Identity,
            TimeStamp = 0,
            Sequence = 601,
            Identity = new JetstreamIdentity
            {
                Did = new Did(hit.AuthorDid),
                Handle = new Handle("unverified.example")
            }
        }, resumed, NullLogger.Instance, cancellationToken);
        await actorStore.InvalidateAsync(hit.AuthorDid, cancellationToken);
        var invalidated = (await actorStore.GetDueAsync(cancellationToken))
            .Single(actor => actor.Did == hit.AuthorDid);
        Assert.AreEqual(authorRequest.Revision + 2, invalidated.Revision);
        await actorStore.SaveAsync(authorRequest, new Handle("stale.example"), cancellationToken);
        command.CommandText = "SELECT Handle FROM Actor WHERE Did = @did";
        Assert.AreEqual(DBNull.Value, await command.ExecuteScalarAsync(cancellationToken),
            "A response started before invalidation must not restore a stale handle.");
        await actorStore.SaveAsync(invalidated, new Handle("current.example"), cancellationToken);
        Assert.AreEqual("current.example", await command.ExecuteScalarAsync(cancellationToken));

        var parentRequest = (await actorStore.GetDueAsync(cancellationToken))
            .Single(actor => actor.Did == hit.ParentAuthorDid);
        await actorStore.SaveAsync(parentRequest, null, cancellationToken);
        command.CommandText = """
            SELECT TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), NextAttemptAt)
            FROM ActorRefresh WHERE Did = @parent
            """;
        command.Parameters.AddWithValue("@parent", hit.ParentAuthorDid.ToString());
        var retrySeconds = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        Assert.IsTrue(retrySeconds is >= 895 and <= 900);
        command.CommandText = """
            SELECT TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), NextAttemptAt)
            FROM ActorRefresh WHERE Did = @did
            """;
        var refreshSeconds = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        Assert.IsTrue(refreshSeconds is >= 86395 and <= 86400);

        await resumed.InitializeAsync(cancellationToken);
        Assert.IsFalse((await actorStore.GetDueAsync(cancellationToken))
            .Any(actor => actor.Did == hit.AuthorDid || actor.Did == hit.ParentAuthorDid),
            "Restart must retain successful refresh schedules and failed-lookup backoff.");
        await resumed.SaveHitAsync(hit, cancellationToken);
        Assert.IsFalse((await actorStore.GetDueAsync(cancellationToken)).Any(actor => actor.Did == hit.AuthorDid),
            "A duplicate hit must not reset a resolved actor's refresh schedule.");

        command.CommandText = """
            DELETE FROM MonitorSchemaMigration WHERE MigrationId = 'actor-refresh-v1';
            DELETE FROM ActorRefresh WHERE Did = @parent;
            DELETE FROM Actor WHERE Did = @parent
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await resumed.InitializeAsync(cancellationToken);
        Assert.IsTrue((await actorStore.GetDueAsync(cancellationToken)).Any(actor => actor.Did == hit.ParentAuthorDid),
            "Upgrading an existing hits database must seed missing actors and durable refresh jobs.");
        command.CommandText = "SELECT Handle FROM Actor WHERE Did = @did";
        Assert.AreEqual("current.example", await command.ExecuteScalarAsync(cancellationToken),
            "Backfill must preserve existing resolved handles.");
        command.CommandText = "SELECT COUNT(*) FROM MonitorSchemaMigration WHERE MigrationId = 'actor-refresh-v1'";
        Assert.AreEqual(1L, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));

        var liveProgress = progress with { LiveAfterSeq = 600 };
        resumed.SaveProgress(liveProgress);
        Assert.AreEqual(liveProgress, await resumed.LoadProgressAsync(cancellationToken));
        var recovery = liveProgress.ReturnToArchive();
        Assert.AreEqual(600, recovery.AfterSeq);
        Assert.IsNull(recovery.LiveAfterSeq);
        Assert.IsNull(recovery.ArchiveCheckpoint);
        resumed.SaveProgress(recovery);
        Assert.AreEqual(recovery, await resumed.LoadProgressAsync(cancellationToken));
    }
}