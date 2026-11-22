using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// A minimal single- or multi-node <see cref="SimulationCluster"/> wrapper that invokes an instrumented
/// probe method as node work (so the ambient simulation runtime and its deterministic environment are
/// active) and returns the result, unwrapping reflection exceptions so tests can assert on the real
/// shim exception types.
/// <para>
/// The host watches its simulation with <see cref="ThreadPoolEscapeGuard"/> and fails the test, when it is disposed, if
/// the simulation's logical thread queued work to the real thread pool. A test which pins such an escape opts out.
/// </para>
/// <para>
/// Each drive of the cluster runs on a thread of its own under a watchdog. A probe which blocks the simulation's logical
/// thread in a real wait never hands control back to the scheduler, so the drive never returns; the watchdog fails the
/// test with a message once the drive outlasts its bound, rather than leaving it to CI's job timeout.
/// </para>
/// </summary>
internal sealed class SimulationHost : IDisposable
{
    // Far longer than any probe takes, even in a Debug build on a loaded CI runner.
    private static readonly TimeSpan DefaultHangBound = TimeSpan.FromMinutes(2);

    private readonly SimulationCluster _cluster;
    private readonly Dictionary<string, SimulationNode> _nodes = new(StringComparer.Ordinal);
    private readonly string _defaultAddress;
    private readonly TimeSpan _hangBound;
    private readonly bool _allowThreadPoolEscapes;
    private readonly Func<StackTrace, bool>? _isKnownEscape;

    // Set once a drive outlasts the watchdog: its thread still holds the cluster, so disposing the cluster would hang too.
    private bool _hung;

    public SimulationHost(
        DateTimeOffset start,
        int seed = 1,
        TimeZoneInfo? timeZone = null,
        IReadOnlyList<string>? nodeAddresses = null,
        int? processorCount = null,
        TimeSpan? hangBound = null,
        bool allowThreadPoolEscapes = false,
        Func<StackTrace, bool>? isKnownEscape = null)
    {
        IReadOnlyList<string> addresses = nodeAddresses ?? ["node"];
        _defaultAddress = addresses[0];
        _hangBound = hangBound ?? DefaultHangBound;
        _allowThreadPoolEscapes = allowThreadPoolEscapes;
        _isKnownEscape = isKnownEscape;

        _cluster = new SimulationCluster(
            seed,
            start,
            simulationTimeZone: timeZone,
            processorCount: processorCount);

        foreach (string address in addresses)
        {
            _nodes[address] = _cluster.AddNode(address);
        }

        ThreadPoolEscapeGuard.Watch(_cluster.Scheduler);
    }

    /// <summary>Gets the scheduler which runs the cluster's controlled operations.</summary>
    public SimulationScheduler Scheduler => _cluster.Scheduler;

    /// <summary>Gets the named node's <see cref="SimulationTaskScheduler"/>, which queues work to its lane.</summary>
    public SimulationTaskScheduler NodeTaskScheduler(string address) => _nodes[address].Context.TaskScheduler;

    /// <summary>Invokes the probe method as work on the default node.</summary>
    public object? Invoke(MethodInfo method, params object?[] args) => InvokeOnNode(_defaultAddress, method, args);

    /// <summary>Invokes the probe method as work on the named node.</summary>
    public object? InvokeOnNode(string address, MethodInfo method, params object?[] args)
    {
        SimulationNode node = _nodes[address];

        object? result = null;
        Exception? error = null;
        node.Context.SchedulerLane.EnqueueAfter(
            () =>
            {
                try
                {
                    result = method.Invoke(null, args.Length == 0 ? null : args);
                }
                catch (TargetInvocationException ex)
                {
                    error = ex.InnerException ?? ex;
                }
            },
            TimeSpan.Zero);

        RunUntilIdle();

        if (error is not null)
        {
            throw error;
        }

        return result;
    }

    /// <summary>Runs uninstrumented <paramref name="work"/> as work of the default node's lane.</summary>
    public void InvokeAction(Action work)
    {
        _nodes[_defaultAddress].Context.SchedulerLane.EnqueueAfter(work, TimeSpan.Zero);
        RunUntilIdle();
    }

    /// <summary>
    /// Starts the probe fire-and-forget as work of the cluster's own lane, as Clog starts its system under test, and runs
    /// the simulation until it is idle. The probe reports its outcome through its arguments.
    /// </summary>
    public void InvokeOnClusterLane(MethodInfo method, params object?[] args)
    {
        _cluster.SchedulerLane.Enqueue(() => _ = method.Invoke(null, args.Length == 0 ? null : args));
        RunUntilIdle();
    }

    /// <summary>
    /// Invokes the probe as node work and then enqueues <paramref name="afterWork"/> as further node
    /// work (each at the same logical instant, in order), so a probe that awaits an initially-incomplete
    /// task suspends first and is only resumed once the later work completes its antecedents. Everything
    /// runs on the single logical thread; a single <c>RunUntilIdle</c> drains the
    /// probe, the completions, and every controlled continuation deterministically.
    /// </summary>
    public object? InvokeWithWork(MethodInfo method, object?[] args, params Action[] afterWork)
    {
        SimulationNode node = _nodes[_defaultAddress];

        object? result = null;
        Exception? error = null;
        node.Context.SchedulerLane.EnqueueAfter(
            () =>
            {
                try
                {
                    result = method.Invoke(null, args.Length == 0 ? null : args);
                }
                catch (TargetInvocationException ex)
                {
                    error = ex.InnerException ?? ex;
                }
            },
            TimeSpan.Zero);

        foreach (Action work in afterWork)
        {
            Action captured = work;
            node.Context.SchedulerLane.EnqueueAfter(captured, TimeSpan.Zero);
        }

        RunUntilIdle();

        if (error is not null)
        {
            throw error;
        }

        return result;
    }

    /// <summary>
    /// Runs the probe as the root task of <see cref="SimulationCluster.RunToCompletion{T}(Func{Task{T}}, CancellationToken, int)"/>,
    /// where <see cref="TaskScheduler.Current"/> is the cluster's <see cref="SimulationTaskScheduler"/> until the
    /// probe's first await, and returns its result.
    /// </summary>
    public T RunToCompletion<T>(MethodInfo method, params object?[] args)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        T result = default!;
        Watched(() => result = _cluster.RunToCompletion(
            () =>
            {
                try
                {
                    return (Task<T>)method.Invoke(null, args.Length == 0 ? null : args)!;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    ExceptionDispatchInfo.Throw(ex.InnerException);
                    throw;
                }
            },
            cancellationToken));
        return result;
    }

    public void Dispose()
    {
        if (!_hung)
        {
            _cluster.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // An escape tracked elsewhere is recognised by the stack which queued it; any other still fails.
        List<StackTrace> escapes = [.. ThreadPoolEscapeGuard.Unwatch(_cluster.Scheduler)
            .Where(stack => _isKnownEscape is null || !_isKnownEscape(stack))];
        if (!_allowThreadPoolEscapes && escapes.Count > 0)
        {
            Assert.Fail(
                $"The simulation's logical thread queued {escapes.Count} work item(s) to the real thread pool. The first was " +
                $"queued at:{Environment.NewLine}{escapes.First()}");
        }
    }

    private void RunUntilIdle()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Watched(() => _cluster.RunUntilIdle(cancellationToken));
    }

    // Runs one drive of the cluster on a thread of its own, failing the test if it outlasts the hang bound. The thread is
    // a background thread, so a hung one does not keep the test process alive.
    private void Watched(Action drive)
    {
        ObjectDisposedException.ThrowIf(_hung, this);
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                drive();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Clockwork conformance drive",
        };
        thread.Start();
        if (!thread.Join(_hangBound))
        {
            _hung = true;
            throw new TimeoutException(
                $"The simulation did not become idle within {_hangBound}. A probe has most likely blocked the simulation's " +
                "logical thread in a real wait (an uncontrolled blocking call), so the scheduler can run nothing else. " +
                "The drive's thread is abandoned.");
        }

        failure?.Throw();
    }
}
