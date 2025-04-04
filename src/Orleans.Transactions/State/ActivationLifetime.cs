using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Transactions.State;

internal sealed class ActivationLifetime : ILifecycleObserver
{
    private readonly CancellationTokenSource _onDeactivating = new();
    private int _pendingDeactivationLocks;

    public ActivationLifetime(IGrainContext activationContext)
    {
        activationContext.ObservableLifecycle.Subscribe(GrainLifecycleStage.First, this);
        activationContext.ObservableLifecycle.Subscribe(GrainLifecycleStage.Last, this);
    }

    public CancellationToken OnDeactivating => _onDeactivating.Token;

    public Task OnStart(CancellationToken ct) => Task.CompletedTask;

    public Task OnStop(CancellationToken ct)
    {
        _onDeactivating.Cancel(throwOnFirstException: false);

        if (!ct.IsCancellationRequested && _pendingDeactivationLocks > 0)
        {
            return OnStopAsync(ct);
        }

        return Task.CompletedTask;
    }

    private async Task OnStopAsync(CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var maxTime = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested && _pendingDeactivationLocks > 0 && DateTime.UtcNow - startTime < maxTime)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    public DeactivationBlocker BlockDeactivation() => new(this);

    internal readonly struct DeactivationBlocker : IDisposable
    {
        private readonly ActivationLifetime _owner;

        public DeactivationBlocker(ActivationLifetime owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._pendingDeactivationLocks);
        }

        public void Dispose()
        {
            Interlocked.Decrement(ref _owner._pendingDeactivationLocks);
        }
    }
}
