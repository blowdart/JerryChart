// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.WebSockets;

using idunno.AtProto.Jetstream;

using JerryChart.Monitor;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JerryChart.Tests;

/// <summary>Verifies indefinitely capped retries and cancellation without real waits.</summary>
[TestClass]
public sealed class RetryLoopTests
{
    /// <summary>Verifies jittered delays remain bounded by the schedule and retries continue after the cap.</summary>
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
        AssertDelays(waits, [1, 5, 15, 30, 90, 150, 300, 300, 300, 300]);
        Assert.AreEqual(10, attempts);
    }

    /// <summary>Verifies retry jitter stays within its lower bound and the global maximum.</summary>
    [TestMethod]
    public void DelayAppliesBoundedJitter()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(0.8), RetryLoop.Delay(1, 0));
        Assert.AreEqual(TimeSpan.FromSeconds(1.2), RetryLoop.Delay(1, 1));
        Assert.AreEqual(TimeSpan.FromSeconds(240), RetryLoop.Delay(7, 0));
        Assert.AreEqual(TimeSpan.FromSeconds(300), RetryLoop.Delay(7, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryLoop.Delay(1, -0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryLoop.Delay(1, 1.01));
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
        AssertDelays(waits, [1, 5, 1, 5]);
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

    /// <summary>Verifies only stream-read transport failures and completion receive the interruption classification.</summary>
    /// <param name="failure">The enumerator outcome.</param>
    /// <returns>A task representing stream-read classification.</returns>
    [TestMethod]
    [DataRow("io")]
    [DataRow("websocket")]
    [DataRow("end")]
    [DataRow("invalid")]
    [DataRow("canceled")]
    [DataRow("refused")]
    public async Task LiveReadClassificationPreservesUnexpectedFailures(string failure)
    {
        Exception? original = failure switch
        {
            "io" => new IOException("Any SDK wording"),
            "websocket" => new WebSocketException(),
            "invalid" => new InvalidDataException("Malformed event"),
            "canceled" => new OperationCanceledException(),
            "refused" => new JetstreamConnectionException(HttpStatusCode.BadRequest, null, null),
            _ => null
        };
        async IAsyncEnumerable<JetstreamEvent> Stream()
        {
            await Task.Yield();
            if (original is not null)
            {
                throw original;
            }
            yield break;
        }

        await using IAsyncEnumerator<JetstreamEvent> events = Stream().GetAsyncEnumerator();
        if (failure is "io" or "websocket" or "end")
        {
            LiveStreamInterruptedException exception = await Assert.ThrowsAsync<LiveStreamInterruptedException>(
                () => JetstreamMonitor.ReadNextLiveAsync(events));
            Assert.AreSame(original, exception.InnerException);
        }
        else
        {
            Exception exception = await Assert.ThrowsAsync<Exception>(() => JetstreamMonitor.ReadNextLiveAsync(events));
            Assert.AreSame(original, exception);
        }
    }

    /// <summary>Verifies failure counts exceed the backoff cap and reset on durable progress.</summary>
    /// <returns>A task representing retry logging.</returns>
    [TestMethod]
    public async Task RetryLogsDistinguishTransportFromProcessingAndCountFailures()
    {
        using var shutdown = new CancellationTokenSource();
        var logger = new RetryLogger();
        int attempts = 0;
        var processingFailure = new IOException("Processing failed, not a stream read.");
        await RetryLoop.RunAsync((processed, _) =>
        {
            attempts++;
            if (attempts == 10)
            {
                processed();
                throw processingFailure;
            }
            throw new LiveStreamInterruptedException(new IOException("SDK diagnostic"));
        }, logger, shutdown.Token, (_, _) =>
        {
            if (attempts == 10)
            {
                shutdown.Cancel();
            }
            return Task.CompletedTask;
        });

        Assert.AreSequenceEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 1 }, logger.Failures);
        Assert.AreEqual(9, logger.Warnings);
        Assert.AreEqual(9, logger.DebugDetails);
        Assert.AreSame(processingFailure, logger.Error);
    }

    private sealed class RetryLogger : ILogger
    {
        internal List<int> Failures { get; } = [];
        internal int Warnings { get; private set; }
        internal int DebugDetails { get; private set; }
        internal Exception? Error { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            foreach (KeyValuePair<string, object?> field in (IEnumerable<KeyValuePair<string, object?>>)state!)
            {
                if (field.Key == "ConsecutiveFailures")
                {
                    Failures.Add((int)field.Value!);
                }
            }
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
                Assert.IsNull(exception);
            }
            else if (logLevel == LogLevel.Debug)
            {
                DebugDetails++;
                Assert.IsNotNull(exception);
            }
            else if (logLevel == LogLevel.Error)
            {
                Error = exception;
            }
        }
    }

    private static void AssertDelays(IReadOnlyList<double> actual, IReadOnlyList<double> nominal)
    {
        Assert.HasCount(nominal.Count, actual);
        for (int index = 0; index < nominal.Count; index++)
        {
            Assert.IsTrue(actual[index] >= nominal[index] * 0.8,
                $"Delay {actual[index]} is below the jittered lower bound for {nominal[index]} seconds.");
            Assert.IsTrue(actual[index] <= Math.Min(nominal[index] * 1.2, 300),
                $"Delay {actual[index]} exceeds the jittered upper bound for {nominal[index]} seconds.");
        }
    }
}