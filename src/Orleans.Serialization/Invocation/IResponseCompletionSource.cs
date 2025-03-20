// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    }
}