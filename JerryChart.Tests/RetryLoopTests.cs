// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;

using idunno.AtProto.Jetstream;

using JerryChart.Monitor;

using Microsoft.Extensions.Logging.Abstractions;

namespace JerryChart.Tests;

/// <summary>Verifies indefinitely capped retries and cancellation without real waits.</summary>
[TestClass]
public sealed class RetryLoopTests
{
    /// <summary>Verifies the exact user-requested delay schedule and continued retries after the cap.</summary>
    /// <returns>A task representing the retry test.</returns>
    [TestMethod]
    public async Task FailuresRetryForeverAtFiveMinuteCap()
    {
        using var shutdown = new CancellationTokenSource();
        var waits = new List<double>();
        var attempts = 0;
        await RetryLoop.RunAsync((_, _) =>
        {
            attempts++;
            throw new IOException("Disconnected");
        }, NullLogger.Instance, shutdown.Token, (delay, _) =>
        {
            waits.Add(delay.TotalSeconds);
            if (waits.Count == 10)
            {
                shutdown.Cancel();
            }

            return Task.CompletedTask;
        });
        CollectionAssert.AreEqual(new double[] { 1, 5, 15, 30, 90, 150, 300, 300, 300, 300 }, waits);
        Assert.AreEqual(10, attempts);
    }

    /// <summary>Verifies successful event processing resets consecutive failure delays.</summary>
    /// <returns>A task representing the retry test.</returns>
    [TestMethod]
    public async Task ProcessedEventResetsBackoff()
    {
        using var shutdown = new CancellationTokenSource();
        var waits = new List<double>();
        var attempts = 0;
        await RetryLoop.RunAsync((processed, _) =>
        {
            if (++attempts == 3)
            {
                processed();
            }

            throw new IOException("Disconnected");
        }, NullLogger.Instance, shutdown.Token, (delay, _) =>
        {
            waits.Add(delay.TotalSeconds);
            if (waits.Count == 4)
            {
                shutdown.Cancel();
            }

            return Task.CompletedTask;
        });
        CollectionAssert.AreEqual(new double[] { 1, 5, 1, 5 }, waits);
    }

    /// <summary>Verifies authentication refusals are logged and retried rather than terminating.</summary>
    /// <returns>A task representing the retry test.</returns>
    [TestMethod]
    public async Task ServerRefusalsAndUnexpectedEndAreRetried()
    {
        using var shutdown = new CancellationTokenSource();
        var attempts = 0;
        await RetryLoop.RunAsync((_, _) =>
        {
            if (++attempts == 1)
            {
                throw new JetstreamConnectionException(HttpStatusCode.Unauthorized, null, null);
            }

            return Task.CompletedTask;
        }, NullLogger.Instance, shutdown.Token, (_, _) =>
        {
            if (attempts == 2)
            {
                shutdown.Cancel();
            }

            return Task.CompletedTask;
        });
        Assert.AreEqual(2, attempts);
    }

    /// <summary>Verifies shutdown cancels a retry wait immediately without launching another attempt.</summary>
    /// <returns>A task representing the retry test.</returns>
    [TestMethod]
    public async Task ShutdownInterruptsRetryWait()
    {
        using var shutdown = new CancellationTokenSource();
        var attempts = 0;
        await RetryLoop.RunAsync((_, _) =>
        {
            attempts++;
            throw new IOException("Disconnected");
        }, NullLogger.Instance, shutdown.Token, async (_, token) =>
        {
            await shutdown.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        Assert.AreEqual(1, attempts);
    }
}