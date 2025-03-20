// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.TestingHost;

/// <summary>
/// Functionality for finding unused ports.
/// </summary>
public interface ITestClusterPortAllocator : IDisposable
{
    /// <summary>
    /// Allocates consecutive port pairs.
    /// </summary>
    /// <param name="numPorts">The number of consecutive ports to allocate.</param>
    /// <returns>Base ports for silo and gateway endpoints.</returns>
    ValueTuple<int, int> AllocateConsecutivePortPairs(int numPorts);
}