// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

using JerryChart.Data;
using JerryChart.Monitor;

using Microsoft.Extensions.Logging.Abstractions;

using MySqlConnector;

namespace JerryChart.Tests;

/// <summary>Verifies handle refresh visibility without public network traffic or the application's database.</summary>
[TestClass]
public sealed class ActorHandleUpdaterTests
{
    /// <summary>Verifies pacing and server cooldowns report the actual wait without changing its duration.</summary>
    /// <param name="seconds">The selected limiter delay in seconds.</param>
    /// <returns>A task representing the wait visibility test.</returns>
    [TestMethod]
    [DataRow(5)]
    [DataRow(300)]
    [DataRow(86400)]
    public async Task RequestWaitReportsWaitingThenRunning(int seconds)
    {
        var phases = new List<string>();
        TimeSpan duration = TimeSpan.FromSeconds(seconds);
        await ActorHandleUpdater.WaitForRequestAsync(duration, (phase, token) =>
        {
            Assert.AreEqual(TestContext.CancellationToken, token);
            phases.Add(phase);
            return Task.CompletedTask;
        }, (delay, token) =>
        {
            Assert.AreEqual(duration, delay);
            Assert.AreEqual(TestContext.CancellationToken, token);
            CollectionAssert.AreEqual(new[] { "handle-refresh-waiting" }, phases);
            return Task.CompletedTask;
        }, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "handle-refresh-waiting", "handle-refresh-running" }, phases);
    }

    /// <summary>Verifies cancellation never publishes resumed work or swallows the canceled delay.</summary>
    /// <returns>A task representing cancellation testing.</returns>
    [TestMethod]
    public async Task CanceledRequestWaitDoesNotReportRunning()
    {
        var phases = new List<string>();
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ActorHandleUpdater.WaitForRequestAsync(
            TimeSpan.FromMinutes(5), (phase, _) =>
            {
                phases.Add(phase);
                return Task.CompletedTask;
            }, (_, token) => Task.FromCanceled(token), shutdown.Token));
        CollectionAssert.AreEqual(new[] { "handle-refresh-waiting" }, phases);
    }

    /// <summary>Verifies real worker idle/shutdown, queue counts, schema idempotence, freshness and ownership fencing.</summary>
    /// <returns>A task representing isolated MySQL validation.</returns>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task HandleRefreshActivityIsIndependentOfRecurringQueue()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        CancellationToken token = timeout.Token;
        IDistributedApplicationTestingBuilder builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.JerryChart_AppHost>(
                ["Parameters:jetstream-api-key=integration-test-unused",
                    "Parameters:mysql-password=handle-visibility-isolated-test"], token);
        foreach (IResource resource in builder.Resources.Where(resource =>
            resource.Name is "api" or "monitor" or "web" or "web-installer").ToArray())
        {
            builder.Resources.Remove(resource);
        }

        MySqlServerResource mysql = builder.Resources.OfType<MySqlServerResource>().Single();
        foreach (ContainerMountAnnotation mount in mysql.Annotations.OfType<ContainerMountAnnotation>().ToArray())
        {
            mysql.Annotations.Remove(mount);
        }

        await using DistributedApplication app = await builder.BuildAsync(token);
        await app.StartAsync(token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(mysql.Name, token);
        string? connectionString = await app.GetConnectionStringAsync("jerrychart", token);
        Assert.IsNotNull(connectionString);
        var poolOptions = new MySqlConnectionStringBuilder(connectionString) { UseAffectedRows = true };
        await using MySqlDataSource source = new MySqlDataSourceBuilder(poolOptions.ConnectionString).Build();
        var clock = new ProcessingTestClock();
        var statusStore = new ProcessingStatusStore(source, clock);
        await using (MySqlConnection schema = await source.OpenConnectionAsync(token))
        {
            await MonitorSchema.InitializeAsync(schema, token);
            await MonitorSchema.InitializeAsync(schema, token);
        }

        HandleRefreshProcessingStatus empty = (await statusStore.GetAsync(token)).HandleRefresh;
        Assert.AreEqual("not-started", empty.Activity.State);
        Assert.AreEqual(0L, empty.Pending);
        Assert.AreEqual(0L, empty.Due);
        Assert.IsNull(empty.NextDueAt);

        using (var worker = new ActorHandleUpdater(source, NullLogger<ActorHandleUpdater>.Instance, clock))
        {
            await worker.StartAsync(token);
            WorkerActivity idle = await WaitForActivityAsync(statusStore,
                activity => activity.Phase == "handle-refresh-idle", token);
            Assert.IsTrue(idle.IsRunning, "An empty queue must not imply that the independently hosted updater is stopped.");
            Assert.IsNull(idle.FinishedAt, "An idle continuous worker has not completed.");
            clock.Advance(TimeSpan.FromSeconds(15));
            WorkerActivity heartbeat = await WaitForActivityAsync(statusStore,
                activity => activity.HeartbeatAt == clock.GetUtcNow(), token);
            Assert.AreEqual(idle.StartedAt, heartbeat.StartedAt);
            Assert.AreEqual(idle.ChangedAt, heartbeat.ChangedAt);
            await worker.StopAsync(token);
        }

        WorkerActivity stopped = (await statusStore.GetAsync(token)).HandleRefresh.Activity;
        Assert.AreEqual("stopped", stopped.State);
        Assert.IsFalse(stopped.IsRunning);
        Assert.AreEqual(clock.GetUtcNow(), stopped.FinishedAt);

        var options = new MySqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var owner = new MySqlConnection(options.ConnectionString);
        await owner.OpenAsync(token);
        await ActorHandleUpdater.InitializeStoreAsync(owner, token);
        await using (MySqlCommand command = owner.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Actor (Did) VALUES ('did:plc:due'), ('did:plc:future');
                INSERT INTO ActorRefresh (Did, NextAttemptAt) VALUES ('did:plc:due', @now), ('did:plc:future', @future)
                """;
            command.Parameters.AddWithValue("@now", clock.GetUtcNow().UtcDateTime);
            command.Parameters.AddWithValue("@future", clock.GetUtcNow().AddDays(1).UtcDateTime);
            await command.ExecuteNonQueryAsync(token);
        }

        HandleRefreshProcessingStatus queue = (await statusStore.GetAsync(token)).HandleRefresh;
        Assert.AreEqual(2L, queue.Pending);
        Assert.AreEqual(1L, queue.Due);
        Assert.AreEqual(clock.GetUtcNow(), queue.NextDueAt);
        Assert.AreEqual("stopped", queue.Activity.State, "Due work cannot revive a stopped worker.");
        await using (MySqlCommand command = owner.CreateCommand())
        {
            command.CommandText = "DELETE FROM ProcessingActivity WHERE Resource = 'handle-refresh'";
            await command.ExecuteNonQueryAsync(token);
        }
        queue = (await statusStore.GetAsync(token)).HandleRefresh;
        Assert.AreEqual("not-started", queue.Activity.State);
        Assert.IsFalse(queue.Activity.IsRunning, "An unrecorded worker is unknown even when refreshes are due.");
        Assert.AreEqual(1L, queue.Due);

        await using (ProcessingActivity activity = await ProcessingActivity.StartAsync(source, NullLogger.Instance,
            "handle-refresh", "handle-refresh-idle", ":actor-handles", owner, clock, token))
        {
            await using MySqlCommand command = owner.CreateCommand();
            command.CommandText = """
                ALTER TABLE ProcessingActivity ADD CONSTRAINT CK_HandleRefresh_VisibilityTest
                    CHECK (Phase <> 'handle-refresh-running')
                """;
            await command.ExecuteNonQueryAsync(token);
            try
            {
                Assert.IsFalse(await activity.ChangeBestEffortAsync("handle-refresh-running", token),
                    "A failed visibility write must not throw into actor resolution or change the retry policy.");
                Assert.AreEqual("handle-refresh-idle", (await statusStore.GetAsync(token)).HandleRefresh.Activity.Phase);
            }
            finally
            {
                command.CommandText = "ALTER TABLE ProcessingActivity DROP CHECK CK_HandleRefresh_VisibilityTest";
                await command.ExecuteNonQueryAsync(token);
            }
            Assert.IsTrue(await activity.ChangeBestEffortAsync("handle-refresh-running", token));
            await Assert.ThrowsAsync<IOException>(() => activity.ExecuteAsync(
                () => throw new IOException("Simulated retryable actor transport failure."),
                "stopped", token, _ => true));
            WorkerActivity retrying = (await statusStore.GetAsync(token)).HandleRefresh.Activity;
            Assert.AreEqual("retrying", retrying.State);
            Assert.IsNull(retrying.FinishedAt, "A reconnect is not continuous-worker completion.");
        }

        const string firstRun = "00000000-0000-0000-0000-000000000011";
        const string secondRun = "00000000-0000-0000-0000-000000000012";
        Assert.IsTrue(await statusStore.StartAsync("handle-refresh", firstRun, "handle-refresh-running",
            ":actor-handles", owner.ServerThread, token));
        await using var competitor = new MySqlConnection(options.ConnectionString);
        await competitor.OpenAsync(token);
        Assert.IsFalse(await statusStore.StartAsync("handle-refresh", secondRun, "handle-refresh-idle",
            ":actor-handles", competitor.ServerThread, token));
        Assert.IsTrue(await statusStore.ChangeAsync("handle-refresh", firstRun, "handle-refresh-waiting", false, token));
        Assert.IsTrue(await statusStore.HeartbeatAsync("handle-refresh", firstRun, ":actor-handles", owner.ServerThread, token));
        Assert.IsNull((await statusStore.GetAsync(token)).HandleRefresh.Activity.FinishedAt);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual("stale", (await statusStore.GetAsync(token)).HandleRefresh.Activity.State);
        Assert.IsTrue(await statusStore.HeartbeatAsync("handle-refresh", firstRun, ":actor-handles", owner.ServerThread, token));
        Assert.IsTrue(await statusStore.ChangeAsync("handle-refresh", firstRun, "retrying", false, token));
        Assert.IsNull((await statusStore.GetAsync(token)).HandleRefresh.Activity.FinishedAt);

        int ownerId = owner.ServerThread;
        await owner.CloseAsync();
        Assert.IsFalse(await statusStore.HeartbeatAsync("handle-refresh", firstRun, ":actor-handles", ownerId, token),
            "Reconnect waits cannot fabricate a heartbeat after the advisory lock connection closes.");
        await ActorHandleUpdater.InitializeStoreAsync(competitor, token);
        Assert.IsTrue(await statusStore.StartAsync("handle-refresh", secondRun, "handle-refresh-idle",
            ":actor-handles", competitor.ServerThread, token));
        Assert.IsFalse(await statusStore.ChangeAsync("handle-refresh", firstRun, "failure", true, token));
        Assert.IsFalse(await statusStore.HeartbeatAsync("handle-refresh", firstRun, ":actor-handles", ownerId, token));
        Assert.IsTrue(await statusStore.ChangeAsync("handle-refresh", secondRun, "failure", true, token));
        WorkerActivity failed = (await statusStore.GetAsync(token)).HandleRefresh.Activity;
        Assert.AreEqual("failure", failed.State);
        Assert.IsNotNull(failed.FinishedAt);
        await MonitorSchema.InitializeAsync(competitor, token);
        Assert.AreEqual(failed, (await statusStore.GetAsync(token)).HandleRefresh.Activity,
            "Repeated schema initialization must preserve worker lifecycle and recurring jobs.");
        Assert.AreEqual(2L, (await statusStore.GetAsync(token)).HandleRefresh.Pending);
    }

    private static async Task<WorkerActivity> WaitForActivityAsync(ProcessingStatusStore store,
        Func<WorkerActivity, bool> predicate, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            WorkerActivity activity = (await store.GetAsync(timeout.Token)).HandleRefresh.Activity;
            if (predicate(activity))
            {
                return activity;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    /// <summary>Gets or sets the current test's cancellation and diagnostic context.</summary>
    public TestContext TestContext { get; set; }
}
