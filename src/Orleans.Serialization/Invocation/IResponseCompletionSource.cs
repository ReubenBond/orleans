namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Represents a fulfillable promise for a response to a request.
    /// </summary>
    public interface IResponseCompletionSource
    {
        /// <summary>
        /// Sets the result.
        /// </summary>
        /// <param name="value">The result value.</param>
        void Complete(Response value);

        /// <summary>
        /// Sets the result to the default value.
        /// </summary>
        void Complete(); 

#if ORLEANS_PROFILING
        /// <summary>
        /// Sets the trace context used to measure asynchronous continuation scheduling.
        /// </summary>
        void SetRpcTraceContext(Orleans.Serialization.Diagnostics.RpcCallTraceContext context);
#endif
    }
}