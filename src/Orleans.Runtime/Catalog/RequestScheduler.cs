using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Runtime;

/// <summary>
/// Maintains the request scheduling state for an activation.
/// </summary>
/// <remarks>
/// Request scheduling is synchronized using the owning <see cref="ActivationData"/> instance so that activation state
/// transitions and request queue transitions remain atomic.
/// </remarks>
internal sealed class RequestScheduler(object synchronizationRoot, IRequestSchedulerContext context)
{
    private readonly object _synchronizationRoot = synchronizationRoot ?? throw new ArgumentNullException(nameof(synchronizationRoot));
    private readonly IRequestSchedulerContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly List<QueuedRequest> _waitingRequests = [];
    private readonly Dictionary<Message, CoarseStopwatch> _runningRequests = [];
    private Message? _blockingRequest;

    // Dispatch and activation collection use these lock-free snapshots on request hot paths.
    private int _waitingCount;
    private int _runningCount;
    private int _runningNonAlwaysInterleaveCount;
    private int _runningNonAlwaysInterleaveWritableCount;
    private CoarseStopwatch _busyDuration;

    internal int WaitingCount => Volatile.Read(ref _waitingCount);

    internal int RunningCount => Volatile.Read(ref _runningCount);

    internal int RequestCount
    {
        get
        {
            lock (_synchronizationRoot)
            {
                return _waitingRequests.Count + _runningRequests.Count;
            }
        }
    }

    internal bool IsCurrentlyExecuting => RunningCount > 0;

    internal bool IsInactive => WaitingCount == 0 && RunningCount == 0;

    internal void Enqueue(Message message)
    {
        AssertLockHeld();
        _waitingRequests.Add(new(message, CoarseStopwatch.StartNew()));
        Volatile.Write(ref _waitingCount, _waitingRequests.Count);
    }

    internal bool TryGetWaitingRequest(int index, out Message message)
    {
        AssertLockHeld();
        if ((uint)index < (uint)_waitingRequests.Count)
        {
            message = _waitingRequests[index].Message;
            return true;
        }

        message = null!;
        return false;
    }

    internal Message StartRequest(int index)
    {
        AssertLockHeld();
        var message = _waitingRequests[index].Message;
        _waitingRequests.RemoveAt(index);

        var stopwatch = CoarseStopwatch.StartNew();
        _runningRequests.Add(message, stopwatch);
        Volatile.Write(ref _runningCount, _runningRequests.Count);
        Volatile.Write(ref _waitingCount, _waitingRequests.Count);

        if (message.IsAlwaysInterleave)
        {
            return message;
        }

        ++_runningNonAlwaysInterleaveCount;
        if (!message.IsReadOnly)
        {
            ++_runningNonAlwaysInterleaveWritableCount;
        }

        if (_blockingRequest is null)
        {
            // Blocking request tracking is used for long-running request diagnostics.
            _blockingRequest = message;
            _busyDuration = stopwatch;
        }

        return message;
    }

    internal void RemoveWaitingRequest(int index)
    {
        AssertLockHeld();
        _waitingRequests.RemoveAt(index);
        Volatile.Write(ref _waitingCount, _waitingRequests.Count);
    }

    internal bool CanRun(Message incoming)
    {
        AssertLockHeld();
        if (_runningRequests.Count == 0 || incoming.IsAlwaysInterleave)
        {
            return true;
        }

        if (_runningNonAlwaysInterleaveCount == 0
            || (incoming.IsReadOnly && _runningNonAlwaysInterleaveWritableCount == 0))
        {
            return true;
        }

        var reentrancyId = incoming.GetReentrancyId();
        if (reentrancyId != Guid.Empty
            && _context.ReentrantRequestTracker?.IsReentrantSectionActive(reentrancyId) is true)
        {
            return true;
        }

        var canInterleave = _context.CanInterleave;
        bool? incomingMayInterleave = null;
        foreach (var runningRequest in _runningRequests)
        {
            var runningMessage = runningRequest.Key;
            if (runningMessage.IsAlwaysInterleave
                || (runningMessage.IsReadOnly && incoming.IsReadOnly))
            {
                continue;
            }

            if (canInterleave is not null)
            {
                incomingMayInterleave ??= canInterleave.MayInterleave(_context.GrainInstance, incoming);
                if (incomingMayInterleave.Value)
                {
                    return true;
                }

                if (canInterleave.MayInterleave(_context.GrainInstance, runningMessage))
                {
                    continue;
                }
            }

            _blockingRequest = runningMessage;
            _busyDuration = runningRequest.Value;
            return false;
        }

        return true;
    }

    internal bool CompleteRequest(Message message)
    {
        AssertLockHeld();
        var removed = _runningRequests.Remove(message);
        if (removed)
        {
            Volatile.Write(ref _runningCount, _runningRequests.Count);
        }

        if (removed && !message.IsAlwaysInterleave)
        {
            --_runningNonAlwaysInterleaveCount;
            if (!message.IsReadOnly)
            {
                --_runningNonAlwaysInterleaveWritableCount;
            }

            Debug.Assert(_runningNonAlwaysInterleaveCount >= 0);
            Debug.Assert(_runningNonAlwaysInterleaveWritableCount >= 0);
        }

        if (_blockingRequest is null || message.Equals(_blockingRequest))
        {
            _blockingRequest = null;
            _busyDuration = default;
        }

        return removed;
    }

    internal List<Message> DequeueAllWaitingRequests()
    {
        AssertLockHeld();
        var result = new List<Message>(_waitingRequests.Count);
        foreach (var request in _waitingRequests)
        {
            // Reroutable messages leave the activation; local operations complete with their current activation.
            if (!request.Message.IsLocalOnly)
            {
                result.Add(request.Message);
            }
        }

        _waitingRequests.Clear();
        Volatile.Write(ref _waitingCount, 0);
        return result;
    }

    internal bool TryFindRequest(
        GrainId senderGrainId,
        CorrelationId messageId,
        out Message? message,
        out bool wasWaiting)
    {
        AssertLockHeld();
        foreach (var candidate in _runningRequests.Keys)
        {
            if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
            {
                message = candidate;
                wasWaiting = false;
                return true;
            }
        }

        for (var i = 0; i < _waitingRequests.Count; i++)
        {
            var candidate = _waitingRequests[i].Message;
            if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
            {
                message = candidate;
                wasWaiting = true;
                _waitingRequests.RemoveAt(i);
                Volatile.Write(ref _waitingCount, _waitingRequests.Count);
                return true;
            }
        }

        message = null;
        wasWaiting = false;
        return false;
    }

    internal Message? GetBlockingRequest(out CoarseStopwatch busyDuration)
    {
        lock (_synchronizationRoot)
        {
            busyDuration = _busyDuration;
            return _blockingRequest;
        }
    }

    internal bool TryGetRunningDuration(Message message, out CoarseStopwatch duration)
    {
        AssertLockHeld();
        return _runningRequests.TryGetValue(message, out duration);
    }

    internal Dictionary<Message, CoarseStopwatch>.Enumerator GetRunningRequestsEnumerator()
    {
        AssertLockHeld();
        return _runningRequests.GetEnumerator();
    }

    internal List<QueuedRequest>.Enumerator GetWaitingRequestsEnumerator()
    {
        AssertLockHeld();
        return _waitingRequests.GetEnumerator();
    }

    [Conditional("DEBUG")]
    private void AssertLockHeld() => Debug.Assert(Monitor.IsEntered(_synchronizationRoot));

    internal readonly record struct QueuedRequest(Message Message, CoarseStopwatch QueuedTime);
}

internal interface IRequestSchedulerContext
{
    object? GrainInstance { get; }
    GrainCanInterleave? CanInterleave { get; }
    ReentrantRequestTracker? ReentrantRequestTracker { get; }
}

internal sealed class ReentrantRequestTracker : Dictionary<Guid, int>
{
    public void EnterReentrantSection(Guid reentrancyId)
    {
        Debug.Assert(reentrancyId != Guid.Empty);
        ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(this, reentrancyId, out _);
        ++count;
    }

    public void LeaveReentrantSection(Guid reentrancyId)
    {
        Debug.Assert(reentrancyId != Guid.Empty);
        ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(this, reentrancyId);
        if (Unsafe.IsNullRef(ref count))
        {
            return;
        }

        if (--count <= 0)
        {
            Remove(reentrancyId);
        }
    }

    public bool IsReentrantSectionActive(Guid reentrancyId)
    {
        Debug.Assert(reentrancyId != Guid.Empty);
        return TryGetValue(reentrancyId, out var count) && count > 0;
    }
}
