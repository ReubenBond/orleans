// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Streams
{
    /// <summary>
    /// Functionality for creating an <see cref="IQueueCache"/> for a given queue.
    /// </summary>
    public interface IQueueAdapterCache
    {
        /// <summary>
        /// Create a cache for a given queue id
        /// </summary>
        /// <param name="queueId">The queue id.</param>
        /// <returns>The queue cache..</returns>
        IQueueCache CreateQueueCache(QueueId queueId);
    }
}
