// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Data;

using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ProcessingActivity : IAsyncDisposable
{
    private readonly ProcessingStatusStore _store;
    private readonly ILogger _logger;
    private readonly string _resource;
    private readonly int _connectionId;
    private readonly string _runId = Guid.NewGuid().ToString();
    private readonly CancellationTokenSource _heartbeatCancellation = new();
    private Task _heartbeat = Task.CompletedTask;
    private ArchiveStallTracker? _archiveStall;
    private readonly SemaphoreSlim _archiveReport = new(1, 1);
    private bool _disposed;

    private ProcessingActivity(ProcessingStatusStore store, ILogger logger, string resource,
        int connectionId)
    {
        _store = store;
        _logger = logger;
        _resource = resource;
        _connectionId = connectionId;
    }

    internal static async Task<ProcessingActivity> StartAsync(MySqlDataSource dataSource, ILogger logger,
        string resource, string phase, string lockName, MySqlConnection owner,
        TimeProvider clock, CancellationToken cancellationToken, ArchiveStallTracker? archiveStall = null)
    {
        var activity = new ProcessingActivity(new ProcessingStatusStore(dataSource, clock),
            logger, resource, owner.ServerThread);
        try
        {
            if (!await activity._store.StartAsync(resource, activity._runId, phase, lockName,
                activity._connectionId, cancellationToken))
            {
                throw new InvalidOperationException("The worker no longer owns its processing lock.");
            }

            MonitorLog.ProcessingPhaseChanged(logger, resource, phase);
            activity._archiveStall = archiveStall;
            if (archiveStall is not null)
            {
                MonitorMetrics.ObserveArchive(archiveStall, clock);
            }
            activity._heartbeat = RunHeartbeatAsync(
                async token =>
                {
                    bool owned = await activity._store.HeartbeatAsync(resource, activity._runId, lockName,
                        activity._connectionId, token);
                    if (owned)
                    {
                        await activity.ReportArchiveAsync(token);
                    }

                    return owned;
                }, logger, resource, clock, activity._heartbeatCancellation.Token);

            return activity;
        }
        catch
        {
            await activity.DisposeAsync();
            throw;
        }
    }

    internal async Task ReportArchiveAsync(CancellationToken cancellationToken, bool attemptStarted = false)
    {
        if (_archiveStall is null)
        {
            return;
        }

        try
        {
            await _archiveReport.WaitAsync(cancellationToken);
            try
            {
                if (!_archiveStall.IsInitialized)
                {
                    _archiveStall.Initialize(await _store.LoadArchiveReplayAsync(cancellationToken));
                }
                if (attemptStarted)
                {
                    _archiveStall.AttemptStarted();
                }
                await _store.ArchiveReplayAsync(_resource, _runId, _archiveStall.Snapshot(), cancellationToken);
            }
            finally
            {
                _archiveReport.Release();
            }
        }
        catch (Exception exception) when (exception is MySqlException or OperationCanceledException or InvalidOperationException)
        {
            MonitorLog.ProcessingStatusWriteFailed(_logger, exception, _resource, "archive-diagnostics");
        }
    }

    internal async Task ChangeAsync(string phase, CancellationToken cancellationToken)
    {
        if (!await _store.ChangeAsync(_resource, _runId, phase, false, cancellationToken))
        {
            throw new InvalidOperationException("The processing run has been replaced by another owner.");
        }

        MonitorLog.ProcessingPhaseChanged(_logger, _resource, phase);
    }

    internal async Task<bool> ChangeBestEffortAsync(string phase, CancellationToken cancellationToken)
    {
        try
        {
            await ChangeAsync(phase, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is MySqlException or OperationCanceledException or InvalidOperationException)
        {
            MonitorLog.ProcessingStatusWriteFailed(_logger, exception, _resource, phase);
            return false;
        }
    }

    internal async Task ExecuteAsync(Func<Task> work, string completedPhase, CancellationToken cancellationToken,
        Func<Exception, bool>? retryable = null)
    {
        string outcome = completedPhase;
        try
        {
            await work();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "stopped";
            throw;
        }
        catch (Exception exception)
        {
            outcome = exception is MySqlException { IsTransient: true } || retryable?.Invoke(exception) == true
                ? "retrying" : "failure";
            MonitorLog.ProcessingRunFailed(_logger, exception, _resource, outcome);
            throw;
        }
        finally
        {
            await DisposeAsync();
            await FinishAsync(cancellationToken.IsCancellationRequested ? "stopped" : outcome);
        }
    }

    internal async Task FinishAsync(string phase)
    {
        if (_archiveStall is not null)
        {
            using var diagnosticTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReportArchiveAsync(diagnosticTimeout.Token, attemptStarted: phase != "retrying");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (!await _store.ChangeAsync(_resource, _runId, phase, phase != "retrying", timeout.Token))
            {
                MonitorLog.ProcessingOwnershipLost(_logger, _resource);
                return;
            }

            MonitorLog.ProcessingPhaseChanged(_logger, _resource, phase);
        }
        catch (Exception exception) when (exception is MySqlException or OperationCanceledException or InvalidOperationException)
        {
            MonitorLog.ProcessingStatusWriteFailed(_logger, exception, _resource, phase);
        }
    }

    internal static async Task RunHeartbeatAsync(Func<CancellationToken, Task<bool>> heartbeat,
        ILogger logger, string resource, TimeProvider clock, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    if (!await heartbeat(timeout.Token))
                    {
                        MonitorLog.ProcessingOwnershipLost(logger, resource);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    MonitorLog.ProcessingStatusWriteFailed(logger, exception, resource, "heartbeat");
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal cancels and awaits the timer and any in-flight database request.
        }
    }

    /// <summary>Cancels and awaits the independent heartbeat before releasing its cancellation source.</summary>
    /// <returns>A task representing heartbeat shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _heartbeatCancellation.CancelAsync();
        await _heartbeat;
        _heartbeatCancellation.Dispose();
    }
}