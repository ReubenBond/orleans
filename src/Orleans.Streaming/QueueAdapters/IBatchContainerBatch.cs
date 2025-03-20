// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Streams;

/// <summary>
/// A batch of queue messages (see IBatchContainer for description of batch contents)
/// </summary>
public interface IBatchContainerBatch : IBatchContainer
{
    /// <summary>
    /// Gets the batch containers comprising this batch
    /// </summary>
    List<IBatchContainer> BatchContainers { get; }
}