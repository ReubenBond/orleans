// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Concurrency;
using Orleans.Serialization.Invocation;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

[StatelessWorker(1)] // '1' to force interleaving, otherwise it just creates a new worker
[MayInterleave(nameof(MayInterleaveMethod))]
public class StatelessWorkerWithMayInterleaveGrain : Grain, IStatelessWorkerWithMayInterleaveGrain
{
    public static bool MayInterleaveMethod(IInvokable req) => req.GetMethodName() == nameof(GoFast);

    public async Task GoSlow(ICallbackGrainObserver callback)
    {
        await callback.WaitAsync();
    }

    public async Task GoFast(ICallbackGrainObserver callback) 
    {
        await callback.WaitAsync();
    }
}
