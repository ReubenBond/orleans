// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Streams;

/// <summary>
/// Constants for queue adapters.
/// </summary>
public static class QueueAdapterConstants
{
    /// <summary>
    /// The value used to indicate an unlimited number of messages can be retrieved, when returned by <see cref="IQueueFlowController.GetMaxAddCount"/>.
    /// </summary>
    public const int UNLIMITED_GET_QUEUE_MSG = -1;
}
