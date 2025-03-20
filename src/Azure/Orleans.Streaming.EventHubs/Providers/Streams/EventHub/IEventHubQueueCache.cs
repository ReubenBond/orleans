// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Streams;
using Azure.Messaging.EventHubs;

namespace Orleans.Streaming.EventHubs;

/// <summary>
/// Interface for a stream message cache that stores EventHub EventData
/// </summary>
public interface IEventHubQueueCache : IQueueFlowController, IDisposable
{
    /// <summary>
    /// Add a list of EventHub EventData to the cache.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="dequeueTimeUtc"></param>
    /// <returns></returns>
    List<StreamPosition> Add(List<EventData> message, DateTime dequeueTimeUtc);

    /// <summary>
    /// Get a cursor into the cache to read events from a stream.
    /// </summary>
    /// <param name="streamId"></param>
    /// <param name="sequenceToken"></param>
    /// <returns></returns>
    object GetCursor(StreamId streamId, StreamSequenceToken sequenceToken);
    /// <summary>
    /// Try to get the next message in the cache for the provided cursor.
    /// </summary>
    /// <param name="cursorObj"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    bool TryGetNextMessage(object cursorObj, out IBatchContainer message);

    /// <summary>
    /// Add cache pressure monitor to the cache's back pressure algorithm
    /// </summary>
    /// <param name="monitor"></param>
    void AddCachePressureMonitor(ICachePressureMonitor monitor);

    /// <summary>
    /// Send purge signal to the cache, the cache will perform a time based purge on its cached messages
    /// </summary>
    void SignalPurge();
}
