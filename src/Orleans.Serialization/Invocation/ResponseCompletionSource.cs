using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
#if ORLEANS_PROFILING
using Orleans.Serialization.Diagnostics;
#endif

#nullable disable
namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// A fulfillable promise.
    /// </summary>
    public sealed class ResponseCompletionSource : IResponseCompletionSource, IValueTaskSource<Response>, IValueTaskSource
    {
        // This source is pooled and GetResult returns it to the pool. Continuations must not run inline from SetResult/SetException,
        // or they can reset/reuse this instance before completion unwinds.
        private ManualResetValueTaskSourceCore<Response> _core = new() { RunContinuationsAsynchronously = true };
#if ORLEANS_PROFILING
        private static readonly Action<object> TracedContinuation = static state => ((ResponseCompletionSource)state).InvokeContinuation();
        private RpcCallTraceContext _traceContext;
        private Action<object> _continuation;
        private object _continuationState;
#endif

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask{Response}"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask{Response}"/>.</returns>
        public ValueTask<Response> AsValueTask() => new(this, _core.Version);

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask"/>.</returns>
        public ValueTask AsVoidValueTask() => new(this, _core.Version);

        /// <inheritdoc/>
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        /// <inheritdoc/>
        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
#if ORLEANS_PROFILING
            if (_traceContext.CorrelationId != 0)
            {
                _continuation = continuation;
                _continuationState = state;
                _core.OnCompleted(TracedContinuation, this, token, flags);
                return;
            }
#endif
            _core.OnCompleted(continuation, state, token, flags);
        }

        /// <summary>
        /// Resets this instance.
        /// </summary>
        public void Reset()
        {
#if ORLEANS_PROFILING
            _traceContext = default;
            _continuation = null;
            _continuationState = null;
#endif
            _core.Reset();
            ResponseCompletionSourcePool.Return(this);
        }

        /// <summary>
        /// Completes this instance with an exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        public void SetException(Exception exception)
        {
#if ORLEANS_PROFILING
            SignalCompletion();
#endif
            _core.SetException(exception);
        }

        /// <summary>
        /// Completes this instance with a result.
        /// </summary>
        /// <param name="result">The result.</param>
        public void SetResult(Response result)
        {
            if (result.Exception is not { } exception)
            {
#if ORLEANS_PROFILING
                SignalCompletion();
#endif
                _core.SetResult(result);
            }
            else
            {
                SetException(exception);
            }
        }

#if ORLEANS_PROFILING
        public void SetRpcTraceContext(RpcCallTraceContext context) => _traceContext = context;

        private void SignalCompletion()
        {
            if (_traceContext.CorrelationId != 0)
            {
                RpcCallEventSource.Log.WritePhase(
                    _traceContext,
                    RpcCallPhase.CompletionSignaled,
                    RpcCallResourceKind.Continuation,
                    RuntimeHelpers.GetHashCode(this),
                    queueDepth: RpcCallEventSource.PendingWorkItemCount);
            }
        }

        private void InvokeContinuation()
        {
            var continuation = _continuation;
            var state = _continuationState;
            RpcCallEventSource.Log.WritePhase(
                _traceContext,
                RpcCallPhase.ContinuationStart,
                RpcCallResourceKind.Continuation,
                RuntimeHelpers.GetHashCode(this),
                queueDepth: RpcCallEventSource.PendingWorkItemCount);
            continuation(state);
        }
#endif

        /// <summary>
        /// Completes this instance with a result.
        /// </summary>
        /// <param name="value">The result value.</param>
        public void Complete(Response value) => SetResult(value);

        /// <summary>
        /// Completes this instance with the default result.
        /// </summary>
        public void Complete() => SetResult(Response.Completed);

        /// <inheritdoc />
        public Response GetResult(short token)
        {
            bool isValid = token == _core.Version;
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                if (isValid)
                {
                    Reset();
                }
            }
        }

        /// <inheritdoc />
        void IValueTaskSource.GetResult(short token)
        {
            bool isValid = token == _core.Version;
            try
            {
                _ = _core.GetResult(token);
            }
            finally
            {
                if (isValid)
                {
                    Reset();
                }
            }
        }
    }

    /// <summary>
    /// A fulfillable promise.
    /// </summary>
    /// <typeparam name="TResult">The underlying result type.</typeparam>
    public sealed class ResponseCompletionSource<TResult> : IResponseCompletionSource, IValueTaskSource<TResult>, IValueTaskSource
    {
        // This source is pooled and GetResult returns it to the pool. Continuations must not run inline from SetResult/SetException,
        // or they can reset/reuse this instance before completion unwinds.
        private ManualResetValueTaskSourceCore<TResult> _core = new() { RunContinuationsAsynchronously = true };
#if ORLEANS_PROFILING
        private static readonly Action<object> TracedContinuation = static state => ((ResponseCompletionSource<TResult>)state).InvokeContinuation();
        private RpcCallTraceContext _traceContext;
        private Action<object> _continuation;
        private object _continuationState;
#endif

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask{Response}"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask{Response}"/>.</returns>
        public ValueTask<TResult> AsValueTask() => new(this, _core.Version);

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask"/>.</returns>
        public ValueTask AsVoidValueTask() => new(this, _core.Version);

        /// <inheritdoc/>
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        /// <inheritdoc/>
        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
#if ORLEANS_PROFILING
            if (_traceContext.CorrelationId != 0)
            {
                _continuation = continuation;
                _continuationState = state;
                _core.OnCompleted(TracedContinuation, this, token, flags);
                return;
            }
#endif
            _core.OnCompleted(continuation, state, token, flags);
        }

        /// <summary>
        /// Resets this instance.
        /// </summary>
        public void Reset()
        {
#if ORLEANS_PROFILING
            _traceContext = default;
            _continuation = null;
            _continuationState = null;
#endif
            _core.Reset();
            ResponseCompletionSourcePool.Return(this);
        }

        /// <summary>
        /// Completes this instance with an exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        public void SetException(Exception exception)
        {
#if ORLEANS_PROFILING
            SignalCompletion();
#endif
            _core.SetException(exception);
        }

        /// <summary>
        /// Completes this instance with a result.
        /// </summary>
        /// <param name="result">The result.</param>
        public void SetResult(TResult result)
        {
#if ORLEANS_PROFILING
            SignalCompletion();
#endif
            _core.SetResult(result);
        }

#if ORLEANS_PROFILING
        public void SetRpcTraceContext(RpcCallTraceContext context) => _traceContext = context;

        private void SignalCompletion()
        {
            if (_traceContext.CorrelationId != 0)
            {
                RpcCallEventSource.Log.WritePhase(
                    _traceContext,
                    RpcCallPhase.CompletionSignaled,
                    RpcCallResourceKind.Continuation,
                    RuntimeHelpers.GetHashCode(this),
                    queueDepth: RpcCallEventSource.PendingWorkItemCount);
            }
        }

        private void InvokeContinuation()
        {
            var continuation = _continuation;
            var state = _continuationState;
            RpcCallEventSource.Log.WritePhase(
                _traceContext,
                RpcCallPhase.ContinuationStart,
                RpcCallResourceKind.Continuation,
                RuntimeHelpers.GetHashCode(this),
                queueDepth: RpcCallEventSource.PendingWorkItemCount);
            continuation(state);
        }
#endif

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Complete(Response value)
        {
            // Check exception first since it's a simple null check
            if (value.Exception is { } exception)
            {
                SetException(exception);
                return;
            }

            // Check for typed response (common for void returns)
            if (value is Response<TResult> typed)
            {
                SetResult(typed.TypedResult);
                return;
            }

            // Handle untyped successful response
            var result = value.Result;
            if (result is null)
            {
                SetResult(default);
            }
            else if (result is TResult typedResult)
            {
                SetResult(typedResult);
            }
            else
            {
                SetInvalidCastException(result);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SetInvalidCastException(object result)
        {
            var exception = new InvalidCastException($"Cannot cast object of type {result.GetType()} to {typeof(TResult)}");
#if NET5_0_OR_GREATER
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.SetCurrentStackTrace(exception);
            SetException(exception);
#else
            try
            {
                throw exception;
            }
            catch (Exception ex)
            {
                SetException(ex);
            }
#endif
        }

        /// <summary>
        /// Completes this instance with a result.
        /// </summary>
        /// <param name="value">The result value.</param>
        public void Complete(Response<TResult> value)
        {
            if (value.Exception is { } exception)
            {
                SetException(exception);
            }
            else
            {
                SetResult(value.TypedResult);
            }
        }

        /// <inheritdoc/>
        public void Complete() => SetResult(default);

        /// <inheritdoc/>
        public TResult GetResult(short token)
        {
            bool isValid = token == _core.Version;
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                if (isValid)
                {
                    Reset();
                }
            }
        }

        /// <inheritdoc/>
        void IValueTaskSource.GetResult(short token)
        {
            bool isValid = token == _core.Version;
            try
            {
                _ = _core.GetResult(token);
            }
            finally
            {
                if (isValid)
                {
                    Reset();
                }
            }
        }
    }
}
