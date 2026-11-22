using System.Globalization;
using System.Runtime.ExceptionServices;
using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation;

/// <summary>
/// A synchronization context that routes all continuations through a <see cref="SimulationSchedulerLane"/>.
/// </summary>
public sealed class SimulationSynchronizationContext(SimulationSchedulerLane schedulerLane) : SynchronizationContext
{
    // The lane whose work the calling thread is executing: set while the lane runs one of its items, or while this
    // context is installed with Install. Which context is current says nothing about it: a completion which Clockwork
    // makes for simulation code runs with no context current, inside the lane's work (see SimulationCompletion).
    [ThreadStatic]
    private static SimulationSchedulerLane? t_executingLane;

    internal static SimulationSchedulerLane? ExecutingLane
    {
        get => t_executingLane;
        set => t_executingLane = value;
    }

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        schedulerLane.Enqueue(new ScheduledSyncContextItem(d, state));
    }

    /// <summary>
    /// <para>
    /// Synchronously executes <paramref name="d"/> using the simulation's cooperative scheduling.
    /// There are two supported cases:
    /// </para>
    /// <para>
    /// 1. The calling thread is executing this context's lane's work: one of the lane's items (a posted callback, a
    /// task on its <see cref="SimulationTaskScheduler"/>, or the root task of <c>SimulationCluster.RunToCompletion</c>),
    /// or code inside <see cref="Install"/> of a context backed by the same <see cref="SimulationSchedulerLane"/>.
    /// Ownership is a fact about execution, not about which context is current, so it holds inside a completion which
    /// Clockwork runs with no context current. In that case <paramref name="d"/> runs immediately, inline, on the
    /// calling thread - it is already safe because nothing else can be running concurrently with it.
    /// </para>
    /// <para>
    /// 2. The calling thread is not on the owning operation, but no other thread currently holds
    /// the queue's single-threaded guard. The callback is scheduled onto the queue (preserving
    /// deterministic FIFO ordering relative to anything already pending) and this call then
    /// synchronously pumps the queue - via repeated <see cref="SimulationSchedulerLane.RunOnce(CancellationToken)"/> calls
    /// on the calling thread - until the callback has executed, at which point <see cref="Send"/>
    /// returns. This can never deadlock: the calling thread does its own pumping rather than
    /// waiting for another thread to make progress, and every item already in the queue has a
    /// smaller sequence number than the scheduled callback, so pumping is guaranteed to reach it.
    /// </para>
    /// <para>
    /// Failures from earlier queued items do not orphan the sent callback: pumping continues until
    /// that callback has executed exactly once. Afterward, a single failure is rethrown directly;
    /// multiple failures (including a failure from the sent callback) are reported in deterministic
    /// FIFO order using <see cref="AggregateException"/>.
    /// </para>
    /// <para>
    /// If neither case applies - because another thread is genuinely, concurrently executing
    /// simulation work on this queue - this throws <see cref="InvalidOperationException"/> rather
    /// than attempting a real-time cross-thread wait (which nothing in the simulation would ever
    /// satisfy, since nothing pumps the queue autonomously). This is a real usage error: simulation
    /// code must not touch a queue from more than one thread at a time. Pumping from simulation work of another lane, or
    /// from a controlled operation, cannot drive the scheduler either: the scheduler's
    /// <see cref="Runtime.Scheduling.SimulationSchedulerException"/> ends the pump at once and fails the run.
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);

        if (IsOnOwningExecutionContext())
        {
            d(state);
            return;
        }

        ScheduleAndPumpUntilExecuted(d, state);
    }

    /// <summary>Determines whether the calling thread is executing this context's lane's work.</summary>
    private bool IsOnOwningExecutionContext() => ReferenceEquals(t_executingLane, schedulerLane);

    /// <summary>
    /// Schedules <paramref name="d"/> onto the queue and pumps the queue on the calling thread
    /// until it has executed. Throws <see cref="InvalidOperationException"/> (via the queue's
    /// single-threaded guard) if another thread is genuinely, concurrently inside the queue.
    /// </summary>
    private void ScheduleAndPumpUntilExecuted(SendOrPostCallback d, object? state)
    {
        var executed = false;
        ExceptionDispatchInfo? sentFailure = null;
        void WrappedCallback(object? s)
        {
            try
            {
                d(s);
            }
            catch (Exception ex)
            {
                sentFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                executed = true;
            }
        }

        try
        {
            schedulerLane.Enqueue(new ScheduledSyncContextItem(WrappedCallback, state));
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
#pragma warning disable EPC20 // Avoid using default ToString implementation
#pragma warning disable MA0150 // Do not call the default object.ToString explicitly
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Cannot synchronously execute callback {d} with state {state} via Send: another thread is concurrently using the simulation queue. {ex.Message}"),
                ex);
#pragma warning restore MA0150 // Do not call the default object.ToString explicitly
#pragma warning restore EPC20 // Avoid using default ToString implementation
        }

        List<ExceptionDispatchInfo>? failures = null;
        while (!executed)
        {
            bool ran;
            try
            {
                ran = schedulerLane.RunOnce(CancellationToken.None);
            }
            catch (ObjectDisposedException) when (schedulerLane.IsDetached)
            {
                throw;
            }
            catch (UnsupportedRuntimeException)
            {
                // No later step can succeed in this process, so the pump stops rather than retry.
                throw;
            }
            catch (SimulationSchedulerException exception)
            {
                // The calling thread cannot drive the scheduler (it is running a controlled operation, or another thread
                // drives it), so no retry can succeed. Fail the run as well as the call, in case the caller swallows it.
                if (SimulationScheduler.TryGetExecutingOperation(out SimulationScheduler? scheduler, out _))
                {
                    scheduler.ReportUnhandledCallbackException(exception);
                }

                throw;
            }
            catch (Exception ex)
            {
                if (schedulerLane.IsDetached)
                {
                    throw new InvalidOperationException(
                        "Send could not complete because the simulation scheduler lane was detached before the scheduled callback executed.",
                        ex);
                }

                (failures ??= []).Add(ExceptionDispatchInfo.Capture(ex));
                continue;
            }

            if (!ran)
            {
                throw new InvalidOperationException(
                    "Send could not complete: the scheduled callback was canceled or the simulation queue went idle before it executed.");
            }
        }

        if (sentFailure is not null)
        {
            (failures ??= []).Add(sentFailure);
        }

        if (failures is [var failure])
        {
            failure.Throw();
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException(
                "One or more callbacks failed while synchronously pumping the simulation queue.",
                failures.Select(static failure => failure.SourceException));
        }
    }

    /// <inheritdoc />
    public override SynchronizationContext CreateCopy() => new SimulationSynchronizationContext(schedulerLane);

    /// <summary>
    /// Gets the underlying scheduler lane for this synchronization context.
    /// </summary>
    public object UnderlyingScheduler => schedulerLane;

    /// <summary>
    /// Checks if this synchronization context shares the same scheduler as another context.
    /// </summary>
    /// <param name="syncCtx">The context to compare with.</param>
    /// <returns>True if they share the same underlying scheduler.</returns>
    public bool IsSameScheduler(SynchronizationContext syncCtx) => ReferenceEquals(syncCtx, this) || syncCtx is SimulationSynchronizationContext simSyncCtx && simSyncCtx.UnderlyingScheduler.Equals(UnderlyingScheduler);

    /// <summary>
    /// Installs this synchronization context on the current thread, and makes the thread execute this context's lane's
    /// work (see <see cref="Send"/>), until the returned scope is disposed. The lane runs each of its items inside this
    /// scope. If the thread is already executing the lane's work with this lane's context current, returns an empty scope
    /// (no-op); inside a completion, where the thread executes the lane's work with no context current, it installs the
    /// context.
    /// </summary>
    /// <returns>A disposable scope that restores the previous synchronization context and lane when disposed.</returns>
    public IDisposable Install()
    {
        if (IsOnOwningExecutionContext() && Current is SimulationSynchronizationContext current && IsSameScheduler(current))
        {
            return SynchronizationContextScope.Empty;
        }

        var previous = Current;
        var previousLane = t_executingLane;
        SetSynchronizationContext(this);
        t_executingLane = schedulerLane;
        return new SynchronizationContextScope(previous, previousLane);
    }

    private sealed class ScheduledSyncContextItem(SendOrPostCallback callback, object? state) : ScheduledItem
    {
        internal override string Kind => "synchronization-context";

        internal override string Description => "Posted synchronization-context callback";

        internal override void Invoke() => callback(state);
    }
}
