namespace Orleans.Testing.Simulation;

/// <summary>
/// Shared waiter bookkeeping for the rendezvous primitives (<see cref="SimulationGate"/>,
/// <see cref="SimulationLatch"/>, <see cref="SimulationBarrier"/>).
/// </summary>
internal static class SimulationRendezvousSupport
{
    private const string RendezvousApi = "Orleans.Testing.Simulation.SimulationRendezvous";

    /// <summary>
    /// Adds a waiter while the owning primitive's lock is held.
    /// </summary>
    public static Waiter AddWaiter(
        List<Waiter> waiters,
        Action<Waiter> onCancellation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waiters);
        ArgumentNullException.ThrowIfNull(onCancellation);

        var waiter = new Waiter(onCancellation, cancellationToken);
        waiters.Add(waiter);
        waiter.RegisterCancellation();
        return waiter;
    }

    /// <summary>
    /// Claims and removes all pending waiters for release while the owning primitive's lock is held.
    /// </summary>
    public static Waiter[] TakeAllForRelease(List<Waiter> waiters)
    {
        ArgumentNullException.ThrowIfNull(waiters);

        if (waiters.Count == 0)
        {
            return [];
        }

        var result = waiters.ToArray();
        waiters.Clear();
        foreach (var waiter in result)
        {
            if (!waiter.TryRelease())
            {
                throw new InvalidOperationException("A terminal waiter remained in the pending waiter list.");
            }
        }

        return result;
    }

    /// <summary>
    /// Disposes cancellation registrations and queues claimed releases in FIFO order.
    /// Must be called without the owning primitive's lock held.
    /// </summary>
    public static void ScheduleReleases(SimulationSchedulerLane queue, IReadOnlyList<Waiter> waiters)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(waiters);

        foreach (var waiter in waiters)
        {
            waiter.ScheduleRelease(queue);
        }
    }

    /// <summary>
    /// A single rendezvous wait and its cancellation registration.
    /// </summary>
    internal sealed class Waiter
    {
        private const int Pending = 0;
        private const int Released = 1;
        private const int Canceled = 2;

        private readonly TaskCompletionSource _completion = new();
        private readonly CancellationToken _cancellationToken;
        private readonly object _registrationLock = new();
        private readonly Action<Waiter> _onCancellation;
        private CancellationTokenRegistration _registration;
        private bool _hasRegistration;
        private bool _registrationDisposalRequested;
        private int _state;

        public Waiter(Action<Waiter> onCancellation, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _onCancellation = onCancellation;
        }

        public Task Task => _completion.Task;

        public void RegisterCancellation()
        {
            if (!_cancellationToken.CanBeCanceled)
            {
                return;
            }

            var registration = _cancellationToken.Register(
                static state => ((Waiter)state!).CancellationRequested(),
                this);
            SetRegistration(registration);
        }

        public bool TryRelease()
        {
            if (Interlocked.CompareExchange(ref _state, Released, Pending) != Pending)
            {
                return false;
            }

            return true;
        }

        public bool TryCancel()
        {
            if (Interlocked.CompareExchange(ref _state, Canceled, Pending) != Pending)
            {
                return false;
            }

            return true;
        }

        public void CompleteCancellation()
        {
            DisposeRegistration();
            SimulationCompletion.TrySetCanceled(_completion, _cancellationToken, RendezvousApi);
        }

        public void ScheduleRelease(SimulationSchedulerLane queue)
        {
            DisposeRegistration();
            queue.Enqueue(() => SimulationCompletion.TrySetResult(_completion, RendezvousApi));
        }

        private void CancellationRequested() => _onCancellation(this);

        private void SetRegistration(CancellationTokenRegistration registration)
        {
            var dispose = false;
            lock (_registrationLock)
            {
                if (_registrationDisposalRequested)
                {
                    dispose = true;
                }
                else
                {
                    _registration = registration;
                    _hasRegistration = true;
                }
            }

            if (dispose)
            {
                registration.Dispose();
            }
        }

        private void DisposeRegistration()
        {
            CancellationTokenRegistration registration = default;
            lock (_registrationLock)
            {
                _registrationDisposalRequested = true;
                if (_hasRegistration)
                {
                    registration = _registration;
                    _registration = default;
                    _hasRegistration = false;
                }
            }

            registration.Dispose();
        }
    }
}
