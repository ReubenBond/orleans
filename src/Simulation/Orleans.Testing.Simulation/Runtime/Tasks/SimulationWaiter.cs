using Orleans.Testing.Simulation.Runtime.Execution;

namespace Orleans.Testing.Simulation.Runtime.Tasks;

/// <summary>
/// <para>
/// Shared lifecycle for a waiter owned by a simulated synchronization primitive.
/// The owning primitive serializes queue mutation; this type owns completion and
/// detaches cancellation and deadline registrations when the waiter is claimed.
/// </para>
/// <para>
/// A waiter whose task the BCL completes with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> (the task
/// <c>SemaphoreSlim.WaitAsync</c> returns) continues asynchronously: its outcome is fixed when it is claimed, and a
/// controlled operation of its own completes its task, so no continuation runs inside the call which releases it, as in
/// the BCL. Any other waiter's task completes inside that call. Either way it completes through
/// <see cref="SimulationCompletion"/>.
/// </para>
/// </summary>
/// <param name="asynchronousApi">
/// The API whose task continues asynchronously, for a waiter whose task completes in a controlled operation of its own;
/// otherwise <see langword="null"/>.
/// </param>
internal class SimulationWaiter(string? asynchronousApi = null)
{
    private const string Api = "System.Threading.Tasks.TaskCompletionSource`1";

    private readonly TaskCompletionSource<bool> _completion = new();
    private CancellationTokenRegistration _cancellationRegistration;
    private ISimulationTimer? _deadline;
    private readonly string? _asynchronousApi = asynchronousApi;
    private bool _pending = true;

    /// <summary>Gets the task completed by the first terminal waiter outcome.</summary>
    public Task<bool> Task => _completion.Task;

    /// <summary>Gets whether no terminal outcome has claimed this waiter.</summary>
    public bool IsPending => _pending;

    /// <summary>Attaches a deterministic deadline while the owning primitive is serialized.</summary>
    public static void AttachDeadline<TWaiter>(
        TWaiter waiter,
        TimeSpan timeout,
        string api,
        Action<TWaiter> onElapsed)
        where TWaiter : SimulationWaiter
    {
        ArgumentNullException.ThrowIfNull(waiter);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(onElapsed);
        waiter._deadline = SimulationTaskRuntime.RegisterTimeout(
            timeout,
            () => onElapsed(waiter),
            api);
    }

    /// <summary>
    /// Attaches cancellation without losing cancellation which races registration.
    /// Registration disposal always occurs outside <paramref name="gate"/>.
    /// </summary>
    public static void AttachCancellation<TWaiter>(
        object gate,
        TWaiter waiter,
        Action<TWaiter, CancellationToken> onCancellation,
        CancellationToken cancellationToken)
        where TWaiter : SimulationWaiter
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(waiter);
        ArgumentNullException.ThrowIfNull(onCancellation);
        if (!cancellationToken.CanBeCanceled)
        {
            return;
        }

        var callbackState = new CancellationCallbackState<TWaiter>(
            waiter,
            onCancellation,
            cancellationToken);
        CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                var callback = (CancellationCallbackState<TWaiter>)state!;
                callback.OnCancellation(callback.Waiter, callback.CancellationToken);
            },
            callbackState);

        bool disposeRegistration;
        lock (gate)
        {
            disposeRegistration = !waiter._pending;
            if (!disposeRegistration)
            {
                waiter._cancellationRegistration = registration;
            }
        }

        if (disposeRegistration)
        {
            registration.Dispose();
        }
    }

    /// <summary>Claims and removes one pending waiter while its owning queue is serialized.</summary>
    public static bool TryTake<TWaiter>(
        List<TWaiter> waiters,
        TWaiter waiter,
        out Claim claim,
        bool cancelDeadline = true)
        where TWaiter : SimulationWaiter
    {
        ArgumentNullException.ThrowIfNull(waiters);
        ArgumentNullException.ThrowIfNull(waiter);
        if (!waiter._pending || !waiters.Remove(waiter))
        {
            claim = default;
            return false;
        }

        claim = waiter.ClaimCore(cancelDeadline);
        return true;
    }

    /// <summary>Claims and removes every pending waiter while its owning queue is serialized.</summary>
    public static List<Claim> TakeAll<TWaiter>(List<TWaiter> waiters)
        where TWaiter : SimulationWaiter
    {
        ArgumentNullException.ThrowIfNull(waiters);
        var claims = new List<Claim>(waiters.Count);
        foreach (TWaiter waiter in waiters)
        {
            if (waiter._pending)
            {
                claims.Add(waiter.ClaimCore(cancelDeadline: true));
            }
        }

        waiters.Clear();
        return claims;
    }

    /// <summary>
    /// Claims a waiter whose owner needs to remove it from more than one queue.
    /// Must be called while all owning queues are serialized.
    /// </summary>
    public bool TryClaim(out Claim claim, bool cancelDeadline = true)
    {
        if (!_pending)
        {
            claim = default;
            return false;
        }

        claim = ClaimCore(cancelDeadline);
        return true;
    }

    private Claim ClaimCore(bool cancelDeadline)
    {
        _pending = false;
        CancellationTokenRegistration registration = _cancellationRegistration;
        _cancellationRegistration = default;
        ISimulationTimer? deadline = _deadline;
        _deadline = null;
        return new Claim(this, registration, cancelDeadline ? deadline : null);
    }

    private sealed class CancellationCallbackState<TWaiter>(
        TWaiter waiter,
        Action<TWaiter, CancellationToken> onCancellation,
        CancellationToken cancellationToken)
        where TWaiter : SimulationWaiter
    {
        public TWaiter Waiter { get; } = waiter;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public Action<TWaiter, CancellationToken> OnCancellation { get; } = onCancellation;
    }

    /// <summary>
    /// Resources detached by the first terminal outcome. Complete outside the owning
    /// primitive's lock so registration disposal and continuations cannot re-enter it.
    /// </summary>
    internal readonly struct Claim(
        SimulationWaiter waiter,
        CancellationTokenRegistration cancellationRegistration,
        ISimulationTimer? deadline)
    {
        private readonly SimulationWaiter _waiter = waiter;
        private readonly CancellationTokenRegistration _cancellationRegistration = cancellationRegistration;
        private readonly ISimulationTimer? _deadline = deadline;

        public Task<bool> Task => _waiter.Task;

        public void Complete(bool result) => Resolve(new Outcome(OutcomeKind.Result, result, null, default));

        public void Cancel(CancellationToken cancellationToken) =>
            Resolve(new Outcome(OutcomeKind.Canceled, Result: false, null, cancellationToken));

        public void Fault(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            Resolve(new Outcome(OutcomeKind.Faulted, Result: false, exception, default));
        }

        private static bool IsSimulationThread() =>
            SimulationExecutionContext.Current is { Runtime.Scheduler.IsSimulationThread: true };

        // A waiter which continues asynchronously is delivered by a controlled operation queued from the logical thread;
        // a cancellation which a thread outside the simulation makes completes it there, where the BCL would too.
        private void Resolve(Outcome outcome)
        {
            if (_waiter._asynchronousApi is not { } asynchronousApi || !IsSimulationThread())
            {
                Deliver(this, outcome, cleanup: true);
                return;
            }

            Cleanup();
            Claim claim = this;
            SimulationTaskRuntime.QueueWork(
                () => Deliver(claim, outcome, cleanup: false),
                asynchronousApi,
                flowExecutionContext: false);
        }

        // The registrations are released inside the completion, so a completion which fails the run does so first.
        private static void Deliver(Claim claim, Outcome outcome, bool cleanup) =>
            SimulationCompletion.Run(
                (claim, outcome, cleanup),
                static completion =>
                {
                    if (completion.cleanup)
                    {
                        completion.claim.Cleanup();
                    }

                    TaskCompletionSource<bool> source = completion.claim._waiter._completion;
                    _ = completion.outcome.Kind switch
                    {
                        OutcomeKind.Result => source.TrySetResult(completion.outcome.Result),
                        OutcomeKind.Canceled => source.TrySetCanceled(completion.outcome.CancellationToken),
                        _ => source.TrySetException(completion.outcome.Exception!),
                    };
                },
                Api);

        private void Cleanup()
        {
            _deadline?.Cancel();
            _cancellationRegistration.Dispose();
        }
    }

    private enum OutcomeKind
    {
        Result,
        Canceled,
        Faulted,
    }

    private readonly record struct Outcome(OutcomeKind Kind, bool Result, Exception? Exception, CancellationToken CancellationToken);
}
