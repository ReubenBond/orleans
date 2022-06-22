using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;

namespace Orleans.Runtime
{
    /// <summary>
    /// This class holds information regarding the request currently being processed.
    /// It is explicitly intended to be available to application code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request context is represented as a property bag.
    /// Some values are provided by default; others are derived from messages headers in the
    /// request that led to the current processing.
    /// </para>
    /// <para>
    /// Information stored in <see cref="RequestContext"/> is propagated from Orleans clients to Orleans grains automatically by the Orleans runtime.
    /// </para>
    /// </remarks>
    public static class RequestContext
    {
        /// <summary>
        /// Gets or sets a value indicating whether <c>Trace.CorrelationManager.ActivityId</c> settings should be propagated into grain calls.
        /// </summary>
        public static bool PropagateActivityId { get; set; }

        internal const string CALL_CHAIN_REQUEST_CONTEXT_HEADER = "#RC_CCH";
        internal const string E2_E_TRACING_ACTIVITY_ID_HEADER = "#RC_AI";
        internal const string PING_APPLICATION_HEADER = "Ping";

        internal static readonly AsyncLocal<ContextProperties> CallContextData = new AsyncLocal<ContextProperties>();

        /// <summary>Gets or sets an activity ID that can be used for correlation.</summary>
        public static Guid ActivityId
        {
            get { return (Guid)(Get(E2_E_TRACING_ACTIVITY_ID_HEADER) ?? Guid.Empty); }
            set
            {
                if (value == Guid.Empty)
                {
                    Remove(E2_E_TRACING_ACTIVITY_ID_HEADER);
                }
                else
                {
                    Set(E2_E_TRACING_ACTIVITY_ID_HEADER, value);
                }
            }
        }

        /// <summary>
        /// Retrieves a value from the request context.
        /// </summary>
        /// <param name="key">The key for the value to be retrieved.</param>
        /// <returns>
        /// The value currently associated with the provided key, otherwise <see langword="null"/> if no data is present for that key.
        /// </returns>
        public static object Get(string key)
        {
            var properties = CallContextData.Value;
            var values = properties.Values;

            if (values != null && values.TryGetValue(key, out var result))
            {
                return result;
            }

            return null;
        }

        /// <summary>
        /// Sets a value in the request context.
        /// </summary>
        /// <param name="key">The key for the value to be updated or added.</param>
        /// <param name="value">The value to be stored into the request context.</param>
        public static void Set(string key, object value)
        {
            var properties = CallContextData.Value;
            var values = properties.Values;

            if (values == null)
            {
                values = new Dictionary<string, object>(1);
            }
            else
            {
                // Have to copy the actual Dictionary value, mutate it and set it back.
                // This is since AsyncLocal copies link to dictionary, not create a new one.
                // So we need to make sure that modifying the value, we doesn't affect other threads.
                var hadPreviousValue = values.ContainsKey(key);
                var newValues = new Dictionary<string, object>(values.Count + (hadPreviousValue ? 0 : 1));
                foreach (var pair in values)
                {
                    newValues.Add(pair.Key, pair.Value);
                }

                values = newValues;
            }

            values[key] = value;
            CallContextData.Value = new ContextProperties
            {
                RequestObject = properties.RequestObject,
                Values = values
            };
        }

        /// <summary>
        /// Remove a value from the request context.
        /// </summary>
        /// <param name="key">The key for the value to be removed.</param>
        /// <returns><see langword="true"/> if the value was previously in the request context and has now been removed, otherwise <see langword="false"/>.</returns>
        public static bool Remove(string key)
        {
            var properties = CallContextData.Value;
            var values = properties.Values;

            if (values == null || values.Count == 0 || !values.ContainsKey(key))
            {
                return false;
            }

            if (values.Count == 1)
            {
                CallContextData.Value = new ContextProperties
                {
                    RequestObject = properties.RequestObject,
                    Values = null
                };
                return true;
            }
            else
            {
                var newValues = new Dictionary<string, object>(values);
                newValues.Remove(key);
                CallContextData.Value = new ContextProperties
                {
                    RequestObject = properties.RequestObject,
                    Values = newValues
                };
                return true;
            }
        }

        /// <summary>
        /// Clears the current request context.
        /// </summary>
        public static void Clear()
        {
            // Remove the key to prevent passing of its value from this point on
            if (!CallContextData.Value.IsDefault)
            {
                CallContextData.Value = default;
            }
        }

        /// <summary>
        /// Gets the currently executing request.
        /// </summary>
        internal static object RequestObject => CallContextData.Value.RequestObject;

        internal readonly struct ContextProperties
        {
            public object RequestObject { get; init; }

            public Dictionary<string, object> Values { get; init; }

            public bool IsDefault => RequestObject is null && Values is null;
        }
    }

    /*
    * Before executing a method, get a request context from the pool and assign:
       * RuntimeContext
       * RequestContext
       * Root synchronization context
    * When accessing RequestContext
      * First check if SynchronizationContext.Current is an OrleansSyncrhonizationContext
        * If so, use it to get the RequestContext
        * Else, use the RequestContext from the async local (needed to support non-Grain callers efficiently)
    * When accessing RuntimeContext
      * Always check if SynchronizationContext.Current is an OrleansSyncrhonizationContext
        * If so, use it to get the RuntimeContext
        * Else, return null
      * Setting runtime context directly is now invalid 
    * After method execution, reset the OrleansSynchronizationContext and return it to the pool
    * PROBLEM: RequestContext no longer has copy-on-write semantics, so changes made in nested calls will be visible to the caller.
      * One solution is to always copy the RequestContext into a new OrleansSynchronizationContext whenever RequestContext is updated.
      * This is likely no worse than the current solution, which performs a copy-on-write of the dictionary anyway.
      * We can possibly detect the nesting based on calls to the SynchronizationContext methods, and only perform a copy-on-write if a
        SynchronizationContext call has occurred betweeen the last update.
    */

    internal sealed class OrleansSynchronizationContext : SynchronizationContext, IDisposable
    {
        private static readonly OrleansSynchronizationContextPool Pool = new();

        public static OrleansSynchronizationContext Get(SynchronizationContext rootSynchronizationContext, IGrainContext grainContext)
        {
            var context = Pool.Get();
            context.RootSynchronizationContext = rootSynchronizationContext;
            context.RuntimeContext = grainContext;
            return context;
        }

        public static OrleansSynchronizationContext Fork(OrleansSynchronizationContext original)
        {
            var context = Pool.Get();
            context.RootSynchronizationContext = original.RootSynchronizationContext;
            context.RuntimeContext = original.RuntimeContext;
            context.RequestContextProperties = original.RequestContextProperties;
            return context;
        }

        public static void Return(OrleansSynchronizationContext original)
        {
            Pool.Return(original);
        }

        public RequestContext.ContextProperties RequestContextProperties { get; set; }
        public IGrainContext RuntimeContext { get; set; }
        public SynchronizationContext RootSynchronizationContext { get; set; }

        public override void Send(SendOrPostCallback d, object state)
        {
            if (RootSynchronizationContext is null)
            {
                ThrowMissingSynchronizationContext();
            }

            RootSynchronizationContext.Send(d, state);
        }

        public override void Post(SendOrPostCallback d, object state)
        {
            if (RootSynchronizationContext is null)
            {
                ThrowMissingSynchronizationContext();
            }

            RootSynchronizationContext.Post(d, state);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowMissingSynchronizationContext() => throw new InvalidOperationException("Missing synchronization context.");

        private void Reset()
        {
            RequestContextProperties = default;
            RuntimeContext = default;
            RootSynchronizationContext = default;
        }

        public void Dispose() => Pool.Return(this);

        internal readonly struct PoolPolicy : IPooledObjectPolicy<OrleansSynchronizationContext>
        {
            public OrleansSynchronizationContext Create() => new();

            public bool Return(OrleansSynchronizationContext obj)
            {
                obj.Reset();
                return true;
            }
        }

        private sealed class OrleansSynchronizationContextPool : ConcurrentObjectPool<OrleansSynchronizationContext, PoolPolicy>
        {
            public OrleansSynchronizationContextPool() : base(default)
            {
            }
        }
    }

    internal sealed class ConcurrentObjectPool<T> : ConcurrentObjectPool<T, DefaultConcurrentObjectPoolPolicy<T>> where T : class, new()
    {
        public ConcurrentObjectPool() : base(new())
        {
        }
    }

    internal class ConcurrentObjectPool<T, TPoolPolicy> : ObjectPool<T> where T : class where TPoolPolicy : IPooledObjectPolicy<T>
    {
        private readonly ThreadLocal<Stack<T>> _objects = new(() => new());

        private readonly TPoolPolicy _policy;

        public ConcurrentObjectPool(TPoolPolicy policy) => _policy = policy;

        public int MaxPoolSize { get; set; } = int.MaxValue;

        public override T Get()
        {
            var stack = _objects.Value;
            if (stack.TryPop(out var result))
            {
                return result;
            }

            return _policy.Create();
        }

        public override void Return(T obj)
        {
            if (_policy.Return(obj))
            {
                var stack = _objects.Value;
                if (stack.Count < MaxPoolSize)
                {
                    stack.Push(obj);
                }
            }
        }
    }

    internal readonly struct DefaultConcurrentObjectPoolPolicy<T> : IPooledObjectPolicy<T> where T : class, new()
    {
        public T Create() => new();

        public bool Return(T obj) => true;
    }
}
