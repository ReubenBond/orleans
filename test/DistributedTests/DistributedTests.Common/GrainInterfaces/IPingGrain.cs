// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace DistributedTests.GrainInterfaces;

public interface IPingGrain : IGrainWithGuidKey
{
    ValueTask Ping();
}

