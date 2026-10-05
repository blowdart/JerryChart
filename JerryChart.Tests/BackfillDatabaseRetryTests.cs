// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;

using JerryChart.Monitor;

using Microsoft.Extensions.Logging.Abstractions;

using MySqlConnector;

namespace JerryChart.Tests;

/// <summary>Verifies backfill database retries without a production database or real retry waits.</summary>
[TestClass]
public sealed class BackfillDatabaseRetryTests
{
    /// <summary>Verifies transient failures retry indefinitely with the exact capped schedule and can recover.</summary>
    /// <returns>A task representing the retry test.</returns>
    [TestMethod]
    public async Task TransientFailuresRetryUntilRecovery()
    {
        MySqlException failure = await ConnectionFailureAsync();
        var waits = new List<double>();
        int attempts = 0;
        await BackfillDatabaseRetry.RunAsync((_, _) =>
        {
            if (++attempts <= 10)
            {
                throw failure;
            }

            return Task.CompletedTask;
        }, NullLogger.Instance, CancellationToken.None, (delay, _) =>
        {
            waits.Add(delay.TotalSeconds);
            return Task.CompletedTask;
        });
        CollectionAssert.AreEqual(new double[] { 1, 5, 15, 30, 90, 150, 300, 300, 300, 300 }, waits);
        Assert.AreEqual(11, attempts);
    }

    /// <summary>Verifies durably completed work resets database failure backoff.</summary>
    /// <returns>A task representing the retry test.</returns>
    [TestMethod]
    public async Task PersistedWorkResetsBackoff()
    {
        MySqlException failure = await ConnectionFailureAsync();
        int attempts = 0;
        var waits = new List<double>();
        await BackfillDatabaseRetry.RunAsync((persisted, _) =>
        {
            if (++attempts == 5)
            {
                return Task.CompletedTask;
            }

            if (attempts == 3)
            {
                persisted();
            }

            throw failure;
        }, NullLogger.Instance, CancellationToken.None, (delay, _) =>
        {
            waits.Add(delay.TotalSeconds);
            return Task.CompletedTask;
        });
        CollectionAssert.AreEqual(new double[] { 1, 5, 1, 5 }, waits);
    }

    /// <summary>Verifies malformed data, configuration, and duplicate-lock errors are not retried.</summary>
    /// <returns>A task representing the failure test.</returns>
    [TestMethod]
    public async Task PermanentFailuresPropagate()
    {
        Exception[] failures =
        [
            new InvalidDataException("Malformed durable data"),
            new ArgumentException("Invalid configuration"),
            new InvalidOperationException("Another backfill owns the lock")
        ];
        foreach (Exception failure in failures)
        {
            int waits = 0;
            Exception actual = await Assert.ThrowsAsync<Exception>(() =>
                BackfillDatabaseRetry.RunAsync((_, _) => throw failure, NullLogger.Instance,
                    CancellationToken.None, (_, _) =>
                    {
                        waits++;
                        return Task.CompletedTask;
                    }));
            Assert.AreSame(failure, actual);
            Assert.AreEqual(0, waits);
        }
    }

    /// <summary>Verifies cancellation interrupts the database wait and never reports completion.</summary>
    /// <returns>A task representing the cancellation test.</returns>
    [TestMethod]
    public async Task CancellationInterruptsWait()
    {
        MySqlException failure = await ConnectionFailureAsync();
        using var shutdown = new CancellationTokenSource();
        int attempts = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BackfillDatabaseRetry.RunAsync((_, _) =>
            {
                attempts++;
                throw failure;
            }, NullLogger.Instance, shutdown.Token, async (_, token) =>
            {
                await shutdown.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }));
        Assert.AreEqual(1, attempts);
    }

    /// <summary>Verifies an already canceled operation does not open a connection.</summary>
    /// <returns>A task representing the cancellation test.</returns>
    [TestMethod]
    public async Task CancellationBeforeAttemptDoesNotRun()
    {
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        int attempts = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BackfillDatabaseRetry.RunAsync((_, _) =>
            {
                attempts++;
                return Task.CompletedTask;
            }, NullLogger.Instance, shutdown.Token, (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, attempts);
    }

    /// <summary>Verifies database cancellation errors propagate as cancellation rather than a permanent failure.</summary>
    /// <returns>A task representing the cancellation test.</returns>
    [TestMethod]
    public async Task DatabaseFailureDuringCancellationIsCancellation()
    {
        MySqlException failure = await ConnectionFailureAsync();
        using var shutdown = new CancellationTokenSource();
        int waits = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BackfillDatabaseRetry.RunAsync((_, _) =>
            {
                shutdown.Cancel();
                throw failure;
            }, NullLogger.Instance, shutdown.Token, (_, _) =>
            {
                waits++;
                return Task.CompletedTask;
            }));
        Assert.AreEqual(0, waits);
    }

    private static async Task<MySqlException> ConnectionFailureAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var connection = new MySqlConnection(
            $"Server=127.0.0.1;Port={port};User ID=backfill-retry-test;Connection Timeout=1;Pooling=false");
        MySqlException failure = await Assert.ThrowsAsync<MySqlException>(() => connection.OpenAsync());
        Assert.IsTrue(failure.IsTransient, "A refused local connection must be classified as transient.");

        return failure;
    }
}