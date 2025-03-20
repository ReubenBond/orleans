// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface IStatelessWorkerWithMayInterleaveGrain : IGrainWithIntegerKey
{
    Task GoSlow(ICallbackGrainObserver callback);
    Task GoFast(ICallbackGrainObserver callback);
}

public interface ICallbackGrainObserver : IGrainObserver
{
    Task WaitAsync();
}