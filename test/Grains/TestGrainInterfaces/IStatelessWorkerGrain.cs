// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IStatelessWorkerGrain : IGrainWithIntegerKey
    {
        Task LongCall();
        Task<Tuple<Guid, string, List<Tuple<DateTime, DateTime>>>> GetCallStats();

        Task DummyCall();
    }
}
