using System;
using Orleans.Runtime.Placement.Repartitioning;
using System.Threading.Tasks;
using Orleans.Placement.Rebalancing;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using Orleans.Internal;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.Scheduler;

#nullable enable

namespace Orleans.Runtime.Placement.Rebalancing;

internal sealed partial class ActivationRebalancerMonitor :
    SystemTarget,
    IActivationRebalancerMonitor,
    IActivationRebalancerReportReceiver,
    ILifecycleParticipant<ISiloLifecycle>
{
    private readonly object _lock = new();
    private IGrainTimer? _monitorTimer;
    private RebalancingReport _latestReport;
    private long _lastHeartbeatTimestamp;

    private readonly TimeProvider _timeProvider;
    private readonly ActivationDirectory _activationDirectory;
    private readonly IActivationRebalancerWorker _rebalancerGrain;
    private readonly ISiloStatusOracle _siloStatusOracle;
    private readonly SiloAddress _localSilo;
    private readonly ILogger<ActivationRebalancerMonitor> _logger;
    private readonly List<IActivationRebalancerReportListener> _statusListeners = [];

    // Check on the worker with double the period the worker heartbeats to the designated monitor.
    private readonly static TimeSpan TimerPeriod = 2 * IActivationRebalancerMonitor.WorkerHeartbeatPeriod;

    public ActivationRebalancerMonitor(
        TimeProvider timeProvider,
        ActivationDirectory activationDirectory,
        ISiloStatusOracle siloStatusOracle,
        ILocalSiloDetails localSiloDetails,
        ILoggerFactory loggerFactory,
        IGrainFactory grainFactory,
        SystemTargetShared shared)
        : base(Constants.ActivationRebalancerMonitorType, shared)
    {
        _timeProvider = timeProvider;
        _activationDirectory = activationDirectory;
        _siloStatusOracle = siloStatusOracle;
        _localSilo = localSiloDetails.SiloAddress;
        _logger = loggerFactory.CreateLogger<ActivationRebalancerMonitor>();
        _rebalancerGrain = grainFactory.GetGrain<IActivationRebalancerWorker>(0);
        _lastHeartbeatTimestamp = _timeProvider.GetTimestamp();

        _latestReport = new()
        {
            ClusterImbalance = 1,
            Host = SiloAddress.Zero,
            Status = RebalancerStatus.Suspended,
            SuspensionDuration = Timeout.InfiniteTimeSpan,
            Statistics = []
        };
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
           nameof(ActivationRepartitioner),
           ServiceLifecycleStage.Active,
           OnStart,
           _ => Task.CompletedTask);

        observer.Subscribe(
           nameof(ActivationRepartitioner),
           ServiceLifecycleStage.ApplicationServices,
           _ => Task.CompletedTask,
           OnStop);
    }

    private async Task OnStart(CancellationToken cancellationToken)
    {
        await this.RunOrQueueTask(() =>
        {
            _monitorTimer = RegisterGrainTimer(async ct =>
            {
                long lastHeartbeatTimestamp;
                RebalancingReport latestReport;
                lock (_lock)
                {
                    lastHeartbeatTimestamp = _lastHeartbeatTimestamp;
                    latestReport = _latestReport;
                }

                var elapsedSinceHeartbeat = _timeProvider.GetElapsedTime(lastHeartbeatTimestamp);
                var needsInitialReport = SiloAddress.Zero.Equals(latestReport.Host);
                var shouldCheckWorker = IsDesignatedMonitor()
                    && elapsedSinceHeartbeat >= IActivationRebalancerMonitor.WorkerHeartbeatPeriod;

                if (needsInitialReport)
                {
                    try
                    {
                        var report = await _rebalancerGrain.GetReport().AsTask().WaitAsync(ct);
                        lock (_lock)
                        {
                            _lastHeartbeatTimestamp = _timeProvider.GetTimestamp();
                        }

                        ReceiveReportCore(report);
                    }
                    catch (OperationCanceledException oce) when (oce.CancellationToken == ct) { }
                    catch (Exception ex)
                    {
                        // This is to avoid crashing the silo due to issues like membership being
                        // full with dead silos after an ungraceful shutdown.
                        LogRebalancerReportFailed(ex);
                    }
                }
                else if (shouldCheckWorker)
                {
                    LogStartingRebalancer(elapsedSinceHeartbeat, IActivationRebalancerMonitor.WorkerHeartbeatPeriod);

                    try
                    {
                        await _rebalancerGrain.Ping().WaitAsync(ct);
                        lock (_lock)
                        {
                            _lastHeartbeatTimestamp = _timeProvider.GetTimestamp();
                        }
                    }
                    catch (OperationCanceledException oce) when (oce.CancellationToken == ct) { }
                    catch (Exception ex)
                    {
                        // This is to avoid crashing the silo due to issues like membership being
                        // full with dead silos after an ungraceful shutdown.
                        LogRebalancerWorkerCheckFailed(ex);
                    }
                }

            }, TimeSpan.Zero, TimerPeriod);

            return Task.CompletedTask;
        });
    }

    private async Task OnStop(CancellationToken cancellationToken)
    {
        await this.RunOrQueueTask(() =>
        {
            RebalancingReport report;
            lock (_lock)
            {
                report = _latestReport;
            }

            if (Silo.IsSameLogicalSilo(report.Host))
            {
                if (_activationDirectory.FindTarget(_rebalancerGrain.GetGrainId()) is { } activation)
                {
                    LogMigratingRebalancer(Silo);
                    activation.Migrate(null, cancellationToken); // migrate it anywhere else
                }
            }

            _monitorTimer?.Dispose();
            return Task.CompletedTask;
        });
    }

    public async Task ResumeRebalancing()
    {
        await _rebalancerGrain.ResumeRebalancing();
        await RefreshReport(notifyListeners: true);
    }

    public async Task SuspendRebalancing(TimeSpan? duration)
    {
        await _rebalancerGrain.SuspendRebalancing(duration);
        await RefreshReport(notifyListeners: true);
    }

    public async ValueTask<RebalancingReport> GetRebalancingReport(bool force = false)
    {
        if (force)
        {
            await RefreshReport(notifyListeners: false);
        }

        lock (_lock)
        {
            return _latestReport;
        }
    }

    public Task Heartbeat()
    {
        lock (_lock)
        {
            _lastHeartbeatTimestamp = _timeProvider.GetTimestamp();
        }

        return Task.CompletedTask;
    }

    void IActivationRebalancerReportReceiver.ReceiveReport(RebalancingReport report) =>
        this.RunOrQueueTask(() =>
        {
            ReceiveReportCore(report);
            return Task.CompletedTask;
        }).Ignore();

    public void SubscribeToReports(IActivationRebalancerReportListener listener)
    {
        lock (_lock)
        {
            if (!_statusListeners.Contains(listener))
            {
                _statusListeners.Add(listener);
            }
        }
    }

    public void UnsubscribeFromReports(IActivationRebalancerReportListener listener)
    {
        lock (_lock)
        {
            _statusListeners.Remove(listener);
        }
    }

    private async Task RefreshReport(bool notifyListeners)
    {
        try
        {
            var report = await _rebalancerGrain.GetReport();
            if (notifyListeners)
            {
                await this.RunOrQueueTask(() =>
                {
                    ReceiveReportCore(report);
                    return Task.CompletedTask;
                });
            }
            else
            {
                lock (_lock)
                {
                    _latestReport = report;
                }
            }
        }
        catch (Exception ex)
        {
            LogRebalancerReportFailed(ex);
        }
    }

    private void ReceiveReportCore(RebalancingReport report)
    {
        IActivationRebalancerReportListener[] listeners;
        lock (_lock)
        {
            _latestReport = report;
            listeners = [.. _statusListeners];
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener.OnReport(report);
            }
            catch (Exception ex)
            {
                LogErrorWhileNotifyingListener(ex);
            }
        }
    }

    private bool IsDesignatedMonitor()
    {
        var activeSilos = _siloStatusOracle.GetActiveSilos();
        if (activeSilos.Length == 0)
        {
            return false;
        }

        var designatedMonitor = activeSilos[0];
        for (var i = 1; i < activeSilos.Length; i++)
        {
            if (activeSilos[i].CompareTo(designatedMonitor) < 0)
            {
                designatedMonitor = activeSilos[i];
            }
        }

        return _localSilo.Equals(designatedMonitor);
    }

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "I have not received a heartbeat from the activation rebalancer for the last {Duration} which is more than the " +
        "allowed interval {Period}. I will now try to wake it up with the assumption that it has has been stopped ungracefully."
    )]
    private partial void LogStartingRebalancer(TimeSpan duration, TimeSpan period);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "My silo '{Silo}' is stopping now, and I am the host of the activation rebalancer. " +
        "I will attempt to migrate the rebalancer to another silo."
    )]
    private partial void LogMigratingRebalancer(SiloAddress silo);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "An unexpected error occurred while notifying rebalancer listener."
    )]
    private partial void LogErrorWhileNotifyingListener(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "An unexpected error occurred while trying to a grab report from the rebalancer."
    )]
    private partial void LogRebalancerReportFailed(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "An unexpected error occurred while checking the activation rebalancer worker."
    )]
    private partial void LogRebalancerWorkerCheckFailed(Exception exception);
}
