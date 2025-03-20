// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.ClientObservers
{
    /// <summary>
    /// Base type for special client-wide observers.
    /// </summary>
    internal abstract class ClientObserver
    {
        /// <summary>
        /// Gets the observer id.
        /// </summary>
        /// <param name="clientId">The client id.</param>
        /// <returns>The observer id.</returns>
        internal abstract ObserverGrainId GetObserverGrainId(ClientGrainId clientId);
    }
}
