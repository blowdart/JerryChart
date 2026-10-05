// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JerryChart.Monitor;

internal sealed class MonitorSupervisor(
    Func<CancellationToken, Task> monitor,
    IHostApplicationLifetime lifetime,
    ILogger<MonitorSupervisor> logger) : BackgroundService, IHealthCheck
{
    private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _running;

    internal Task<int> Completion => _completion.Task;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        CancellationToken token = shutdown.Token;
        int exitCode = 0;
        Interlocked.Exchange(ref _running, 1);
        try
        {
            await monitor(token);
            if (!token.IsCancellationRequested)
            {
                throw new InvalidOperationException("The monitor supervisor ended unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            MonitorLog.MonitorStopped(logger);
        }
        catch (Exception exception)
        {
            exitCode = 1;
            MonitorLog.MonitorFailed(logger, exception);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            _completion.TrySetResult(exitCode);
            lifetime.StopApplication();
        }
    }

    /// <summary>Checks the supervisor lifetime, not event throughput, heartbeat writes, or network wait duration.</summary>
    /// <param name="context">The health-check context.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>Healthy while the monitor task is supervised; unhealthy before start or after exit.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Volatile.Read(ref _running) == 1
            ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
}