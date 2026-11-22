using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// <para>
/// Records work queued to the real thread pool by a simulation's logical thread. The BCL raises
/// <c>FrameworkEventSource</c>'s <c>ThreadPoolEnqueueWork</c> event on the thread which queues the work, so a listener
/// sees it on that thread and can ask each watched scheduler whether it is that scheduler's logical thread. Work queued
/// there runs off the simulation: a concurrency escape, such as a framework continuation which the BCL declined to run
/// inline.
/// </para>
/// <para>
/// The guard only observes: it neither delays nor reorders work, so a simulation runs exactly as it would without it.
/// <see cref="SimulationHost"/> watches its cluster's scheduler and fails a test whose simulation queued such work.
/// </para>
/// <para>
/// The thread pool reads whether the enqueue event is enabled only when a worker next dispatches work, so an enqueue
/// before then raises no event. The guard is primed when it is created: it queues no-op work until it observes its own
/// enqueue, and only then watches a scheduler.
/// </para>
/// </summary>
internal sealed class ThreadPoolEscapeGuard : EventListener
{
    private const string FrameworkEventSourceName = "System.Diagnostics.Eventing.FrameworkEventSource";
    private const string EnqueueEventName = "ThreadPoolEnqueueWork";

    // FrameworkEventSource.Keywords.ThreadPool | ThreadTransfer: the BCL raises the enqueue event only when both are on.
    private const EventKeywords ThreadPoolKeywords = (EventKeywords)0x12;

    // Far longer than a thread-pool worker takes to dispatch a no-op work item, even on a loaded CI runner.
    private static readonly TimeSpan PrimingBound = TimeSpan.FromSeconds(30);

    private static readonly Lazy<ThreadPoolEscapeGuard> Instance = new(Create);

    private readonly ConcurrentDictionary<SimulationScheduler, ConcurrentQueue<StackTrace>> _watched = new();
    private readonly ManualResetEventSlim _primed = new();
    private volatile int _primingThread;

    /// <summary>Starts recording the work which <paramref name="scheduler"/>'s logical thread queues to the thread pool.</summary>
    /// <param name="scheduler">The scheduler to watch.</param>
    public static void Watch(SimulationScheduler scheduler) =>
        Instance.Value._watched.TryAdd(scheduler, new ConcurrentQueue<StackTrace>());

    /// <summary>Stops watching <paramref name="scheduler"/> and returns the stacks which queued work while it was watched.</summary>
    /// <param name="scheduler">The watched scheduler.</param>
    /// <returns>The stack of each enqueue.</returns>
    public static IReadOnlyCollection<StackTrace> Unwatch(SimulationScheduler scheduler) =>
        Instance.Value._watched.TryRemove(scheduler, out ConcurrentQueue<StackTrace>? escapes) ? escapes : [];

    private static ThreadPoolEscapeGuard Create()
    {
        var guard = new ThreadPoolEscapeGuard();
        guard.Prime();
        return guard;
    }

    private void Prime()
    {
        _primingThread = Environment.CurrentManagedThreadId;
        var elapsed = Stopwatch.StartNew();
        while (!_primed.IsSet)
        {
            if (elapsed.Elapsed > PrimingBound)
            {
                throw new InvalidOperationException(
                    $"The thread-pool escape guard observed no enqueue event within {PrimingBound}, so it cannot detect escapes.");
            }

            ThreadPool.UnsafeQueueUserWorkItem(static _ => { }, null);
            _primed.Wait(TimeSpan.FromMilliseconds(10));
        }

        _primingThread = 0;
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == FrameworkEventSourceName)
        {
            EnableEvents(eventSource, EventLevel.Verbose, ThreadPoolKeywords);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName != EnqueueEventName)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId == _primingThread)
        {
            _primed.Set();
            return;
        }

        foreach ((SimulationScheduler scheduler, ConcurrentQueue<StackTrace> escapes) in _watched)
        {
            if (scheduler.IsSimulationThread)
            {
                escapes.Enqueue(new StackTrace(fNeedFileInfo: true));
            }
        }
    }
}
