#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Orleans.Connections.Transport.Utilities;

internal sealed class SingleWaiterInlineSignal : IValueTaskSource
{
    // Signaled indicates that the event has been signaled and not yet reset.
    private const uint SignaledFlag = 1;

    // Waiting indicates that a waiter is present and waiting for the event to be signaled.
    private const uint WaitingFlag = 1 << 1;

    // ResetMask is used to clear both status flags.
    private const uint ResetMask = ~SignaledFlag & ~WaitingFlag;

    private ManualResetValueTaskSourceCore<bool> _waitSource;
    private volatile uint _status;

    public bool RunContinuationsAsynchronously
    {
        get => _waitSource.RunContinuationsAsynchronously;
        set => _waitSource.RunContinuationsAsynchronously = value;
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _waitSource.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _waitSource.OnCompleted(continuation, state, token, flags);

    void IValueTaskSource.GetResult(short token)
    {
        // Reset the wait source.
        _waitSource.GetResult(token);
        _waitSource.Reset();

        // Reset the status.
        ResetStatus();
    }

    /// <summary>
    /// Signal the waiter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Signal()
    {
        // Set the signaled flag.
        var status = Interlocked.Or(ref _status, SignaledFlag);

        // If there was a waiter and the signaled flag was unset, wake the waiter now.
        if ((status & SignaledFlag) != SignaledFlag && (status & WaitingFlag) == WaitingFlag)
        {
            // Note that in this assert we are checking the volatile _status field.
            // This is a sanity check to ensure that the signalling conditions are true:
            // that "Signaled" and "Waiting" flags are both set.
            Debug.Assert((_status & (SignaledFlag | WaitingFlag)) == (SignaledFlag | WaitingFlag));
            _waitSource.SetResult(true);
        }
    }

    /// <summary>
    /// Wait for the event to be signaled.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WaitAsync()
    {
        // Indicate that there is a waiter.
        var status = Interlocked.Or(ref _status, WaitingFlag);

        // If there was already a waiter, that is an error since this class is designed for use with a single waiter.
        if ((status & WaitingFlag) == WaitingFlag)
        {
            ThrowConcurrentWaitersNotSupported();
        }

        // If the event was already signaled, immediately wake the waiter.
        if ((status & SignaledFlag) == SignaledFlag)
        {
            // Reset just the status because the _waitSource has not been set.
            // We know that _waitSource has not been set because _waitSource is only set when
            // Signal() observes that the "Waiting" flag had been set but not the "Signaled" flag.
            ResetStatus();
            return default;
        }

        return new(this, _waitSource.Version);
    }

//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
    /// <summary>
    /// Signal the waiter with an exception.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SignalException(Exception exception)
    {
        // Set the signaled flag.
        var status = Interlocked.Or(ref _status, SignaledFlag);

        // If there was a waiter and the signaled flag was unset, wake the waiter now.
        if ((status & SignaledFlag) != SignaledFlag && (status & WaitingFlag) == WaitingFlag)
        {
            // Note that in this assert we are checking the volatile _status field.
            // This is a sanity check to ensure that the signalling conditions are true:
            // that "Signaled" and "Waiting" flags are both set.
            Debug.Assert((_status & (SignaledFlag | WaitingFlag)) == (SignaledFlag | WaitingFlag));
            _waitSource.SetException(exception);
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
        }
    }

    /// <summary>
    /// Called when a waiter handles the event signal.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetStatus()
    {
        // The event is being handled, so clear the "Signaled" flag now.
        // The waiter is no longer waiting, so clear the "Waiting" flag, too.
        var status = Interlocked.And(ref _status, ResetMask);

        // If both the "Waiting" and "Signaled" flags were not already set, something has gone catastrophically wrong.
        Debug.Assert((status & (WaitingFlag | SignaledFlag)) == (WaitingFlag | SignaledFlag));
    }

    private static void ThrowConcurrentWaitersNotSupported() => throw new InvalidOperationException("Concurrent waiters are not supported");
}

//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO BAD BAD BAD BAD
//  TODO
//  TODO
//  TODO
//  TODO
//  TODO
//  TODO
//  TODO
//  TODO
//  TODO
internal sealed class UnsafeInlineSignal<T> : IValueTaskSource<T>
{
    private static readonly Action<object?>? _signalling = _ => { };
    private Action<object?>? _continuation = null;
    private object? _state;
    private T? _result;
    private Exception? _exceptionResult;
#if DEBUG
    private short _version;
#else
    private const short _version = 0;
#endif

    public ValueTaskSourceStatus GetStatus(short token)
    {
        Debug.Assert(token == _version);
        var continuation = _continuation;
        var res = ReferenceEquals(continuation, _signalling) ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;
        return res;
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        Debug.Assert(token == _version);
        var prevContinuation = _continuation;
        if (prevContinuation is null)
        {
            _state = state;
            prevContinuation = Interlocked.CompareExchange(ref _continuation, continuation, null);
        }

        if (prevContinuation is not null)
        {
            Debug.Assert(ReferenceEquals(prevContinuation, _signalling));
            continuation(state);
        }
    }

    T IValueTaskSource<T>.GetResult(short token)
    {
        Debug.Assert(token == _version);
        Debug.Assert(ReferenceEquals(_continuation, _signalling));
        if (_exceptionResult is Exception error)
        {
            Reset();
            ExceptionDispatchInfo.Throw(error);
            return default;
        }
        else
        {
            var result = _result!;
            Reset();
            return result;
        }
    }

    public void SetException(Exception exception)
    {
        _exceptionResult = exception;
        Signal();
    }

    public void SetResult(T result)
    {
        _result = result;
        Signal();
    }

    private void Signal()
    {
        var continuation = Interlocked.Exchange(ref _continuation, _signalling);
        if (continuation is null)
        {
            // There is was no waiter.
            // If one comes, it will see _signalling and fire.
            return;
        }

        if (ReferenceEquals(continuation, _signalling))
        {
            // The signal is already firing.
            return;
        }

        // Execute the continuation inline.
        Debug.Assert(!ReferenceEquals(continuation, _signalling));
        continuation(_state);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<T> WaitAsync() => new(this, _version);

    public void Reset()
    {
        _state = null;
        _continuation = null;
        _result = default;
        _exceptionResult = null;

#if DEBUG
        ++_version;
#endif
    }
}
