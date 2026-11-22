using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// <para>
/// Controlled replacement for <see cref="RegisteredWaitHandle"/>, the token returned by
/// <see cref="ThreadPool.RegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, int, bool)"/>
/// and its unsafe sibling. The rewriter substitutes every <see cref="RegisteredWaitHandle"/> reference with
/// this type and redirects the eight factory overloads to <see cref="ControlledThreadPool"/>, so a
/// registered wait runs as a controlled operation on the deterministic scheduler instead of on a physical
/// thread-pool wait thread.
/// </para>
/// <para>
/// Inside a simulation the registration is a passive, event-driven waiter on the target handle's modelled
/// signalled state (via <see cref="ControlledWaitHandle"/>): it never blocks the single logical thread, so
/// other controlled work runs while it is pending. When the handle is signalled the callback fires with
/// <c>timedOut: false</c> (an auto-reset handle consumes exactly one signal, in FIFO order); when the
/// virtual-time deadline elapses it fires with <c>timedOut: true</c>. A registration created with
/// <c>executeOnlyOnce: true</c> fires once; otherwise it re-arms until <see cref="Unregister(WaitHandle)"/>
/// cancels it. The safe factory flows the caller's <see cref="ExecutionContext"/> to the callback; the unsafe
/// factory does not. If <see cref="Unregister(WaitHandle)"/> is passed a controlled event it is signalled once
/// the registration has stopped firing.
/// </para>
/// <para>
/// Adapted for Clockwork from the Microsoft Coyote (MIT) controlled-synchronization model, extended to the
/// thread-pool registered-wait surface that Coyote does not intercept.
/// </para>
/// </summary>
public sealed class ControlledRegisteredWaitHandle
{
    private const string RegisterApi = "System.Threading.ThreadPool.RegisterWaitForSingleObject";

    private readonly WaitHandle _waitObject = null!;
    private readonly WaitOrTimerCallback _callback = null!;
    private readonly object? _state;
    private readonly int _timeoutMs;
    private readonly bool _executeOnlyOnce;
    private readonly ExecutionContext? _context;

    private bool _unregistered;
    private bool _finished;
    private bool _stopped;
    private int _inFlightCallbacks;
    private WaitHandle? _completion;

    // The current pending waiter and its target handle state, or null between iterations.
    private ControlledWaitHandle.Waiter? _pending;
    private ControlledWaitHandle.HandleState? _pendingState;

    /// <summary>Creates a controlled registration and arms the first passive wait iteration.</summary>
    internal ControlledRegisteredWaitHandle(
        WaitHandle waitObject,
        WaitOrTimerCallback callback,
        object? state,
        int timeoutMs,
        bool executeOnlyOnce,
        ExecutionContext? context)
    {
        _waitObject = waitObject;
        _callback = callback;
        _state = state;
        _timeoutMs = timeoutMs;
        _executeOnlyOnce = executeOnlyOnce;
        _context = context;
        Arm();
    }

    /// <summary>Controlled <see cref="RegisteredWaitHandle.Unregister(WaitHandle)"/>.</summary>
    /// <param name="waitObject">
    /// An optional controlled event signalled once the registration has stopped firing, or
    /// <see langword="null"/>. Uncontrolled handles are ignored (best-effort completion signalling).
    /// </param>
    /// <returns><see langword="true"/> when this call cancelled the registration.</returns>
    public bool Unregister(WaitHandle? waitObject)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.RegisteredWaitHandle.Unregister");
        if (_unregistered)
        {
            return false;
        }

        _unregistered = true;
        if (_finished)
        {
            // The registration already stopped firing (executeOnlyOnce fired); signal completion now.
            ControlledWaitHandle.TrySignal(waitObject);
            return true;
        }

        _completion = waitObject;

        // Cancel any pending waiter so its already-scheduled continuation runs and observes the cancellation.
        if (_pending is not null && _pendingState is not null)
        {
            ControlledWaitHandle.CancelRegisteredWaiter(_pendingState, _pending);
        }
        else
        {
            FinishWhenQuiescent();
        }

        return true;
    }

    private void Arm()
    {
        if (_unregistered)
        {
            FinishWhenQuiescent();
            return;
        }

        ControlledWaitHandle.HandleState target = ControlledWaitHandle.StateForWaitOperation(_waitObject, RegisterApi);

        // Fast paths schedule the resume as controlled work (never inline): an already-set handle consumes
        // its signal immediately; a zero timeout resolves to an immediate timeout.
        if (target.TryAcquire(SimulationSynchronizationFlow.CurrentId))
        {
            ScheduleResume(signaled: true);
            return;
        }

        if (_timeoutMs == 0)
        {
            ScheduleResume(signaled: false);
            return;
        }

        _pendingState = target;
        _pending = ControlledWaitHandle.ArmRegisteredWaiter(target, _timeoutMs, RegisterApi);
        SimulationTaskRuntime.ScheduleContinuation(
            _pending.Task, OnWaiterCompleted, RegisterApi, flowExecutionContext: false);
    }

    private void ScheduleResume(bool signaled) =>
        SimulationTaskRuntime.ScheduleContinuation(
            Task.CompletedTask, () => OnWaitCompleted(signaled), RegisterApi, flowExecutionContext: false);

    private void OnWaiterCompleted()
    {
        ControlledWaitHandle.Waiter pending = _pending!;
        _pending = null;
        _pendingState = null;

        // A successful completion with result true means the handle was signalled; false means the deadline
        // elapsed or Unregister cancelled the waiter (disambiguated by the _unregistered flag below).
        bool signaled = pending.Task.IsCompletedSuccessfully && pending.Task.Result;
        OnWaitCompleted(signaled);
    }

    private void OnWaitCompleted(bool signaled)
    {
        if (_unregistered)
        {
            FinishWhenQuiescent();
            return;
        }

        if (_executeOnlyOnce)
        {
            _stopped = true;
        }
        else
        {
            // Keep an independent passive wait armed before user code can block. Signals which arrive
            // while a callback is running then complete the next iteration instead of being coalesced.
            Arm();
        }

        DispatchCallback(timedOut: !signaled);
    }

    private void FinishWhenQuiescent()
    {
        if (_finished ||
            _pending is not null ||
            _inFlightCallbacks != 0 ||
            (!_unregistered && !_stopped))
        {
            return;
        }

        _finished = true;
        ControlledWaitHandle.TrySignal(_completion);
    }

    private void DispatchCallback(bool timedOut)
    {
        _inFlightCallbacks++;
        SimulationTaskRuntime.QueueWork(
            () =>
            {
                try
                {
                    Invoke(timedOut);
                }
                finally
                {
                    _inFlightCallbacks--;
                    FinishWhenQuiescent();
                }
            },
            RegisterApi,
            flowExecutionContext: false);
    }

    private void Invoke(bool timedOut)
    {
        if (_context is null)
        {
            _callback(_state, timedOut);
            return;
        }

        SimulationTaskRuntime.RunWithCapturedExecutionContext(
            _context,
            () => _callback(_state, timedOut));
    }
}
