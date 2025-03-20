// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Streams;

/// <summary>
/// Identifies whether a stream provider is read-only, read-write, or write-only.
/// </summary>
public enum StreamProviderDirection
{
    /// <summary>
    /// None.
    /// </summary>
    None,

    /// <summary>
    /// This provider can receive messages but cannot send them.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// This provider can send messages but cannot receive them.
    /// </summary>
    WriteOnly,

    /// <summary>
    /// This provider can both send and receive messages.
    /// </summary>
    ReadWrite
}
