// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Concurrency;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains;

[StatelessWorker(MaxLocalWorkers)]
public class StatelessWorkerExceptionGrain : Grain, IStatelessWorkerExceptionGrain
{
    public const int MaxLocalWorkers = 1;

    public StatelessWorkerExceptionGrain()
    {
        throw new Exception("oops");
    }

    public Task Ping()
    {
        return Task.CompletedTask;
    }
}
