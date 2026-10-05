// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net.WebSockets;
using System.Text.Json;

using idunno.AtProto.Jetstream;

using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal static class RetryLoop
{
    private static readonly int[] s_retrySeconds = [1, 5, 15, 30, 90, 150, 300];

    internal static TimeSpan Delay(int consecutiveFailures)
    {
        return TimeSpan.FromSeconds(s_retrySeconds[Math.Clamp(consecutiveFailures - 1, 0, s_retrySeconds.Length - 1)]);
    }

    // This service deliberately retries more broadly than the SDK recovery sample, including all InvalidDataException
    // failures (not just ETag mismatches). Permanent faults require operator correction; retries cannot repair them.
    internal static bool IsRetryable(Exception exception) =>
        exception is IOException or HttpRequestException or WebSocketException
            or JetstreamConnectionException or MySqlException or InvalidDataException or JsonException
            or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException;

    internal static async Task RunAsync(Func<Action, CancellationToken, Task> attempt, ILogger logger,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? wait = null,
        Func<CancellationToken, Task>? retrying = null, bool retryDatabase = true)
    {
        wait ??= (delay, token) => Task.Delay(delay, token);
        int failures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Progress resets consecutive-failure backoff, not a finite retry budget: retries remain indefinite.
                await attempt(() => failures = 0, cancellationToken);
                throw new IOException("Jetstream ended unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when ((retryDatabase || exception is not MySqlException) && IsRetryable(exception))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                failures = Math.Min(failures + 1, s_retrySeconds.Length);
                TimeSpan delay = Delay(failures);
                MonitorLog.RetryingMonitor(logger, exception, delay.TotalSeconds);
                try
                {
                    if (retrying is not null)
                    {
                        await retrying(cancellationToken);
                    }
                    await wait(delay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}