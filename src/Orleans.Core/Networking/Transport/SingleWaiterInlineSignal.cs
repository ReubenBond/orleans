#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Orleans.Networking.Transport;

internal sealed class SingleWaiterInlineSignal : IValueTaskSource
{
    private static readonly Action<object?>? _signalling = _ => { };
    private Action<object?>? _continuation = null;
    private object? _state;
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
        Action<object?>? prevContinuation = _continuation;
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

    void IValueTaskSource.GetResult(short token)
    {
        Debug.Assert(token == _version);
        Debug.Assert(ReferenceEquals(_continuation, _signalling));
        var error = _exceptionResult;
        Reset();

        if (error is { })
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    public void SetException(Exception exception)
    {
        _exceptionResult = exception;
        Signal();
    }

    public void Signal()
    {
        Action<object?>? continuation = Interlocked.Exchange(ref _continuation, _signalling);
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
    public ValueTask WaitAsync() => new(this, _version);

    public void Reset()
    {
        _state = null;
        _continuation = null;
        _exceptionResult = null;

#if DEBUG
        ++_version;
#endif
    }
}

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
        Action<object?>? prevContinuation = _continuation;
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
        Action<object?>? continuation = Interlocked.Exchange(ref _continuation, _signalling);
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
