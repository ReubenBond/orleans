// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface IStreamingHistoryGrain : IGrainWithStringKey
    {
        Task BecomeConsumer(StreamId streamId, string provider, string filterData = null);

        Task StopBeingConsumer();

        Task<List<int>> GetReceivedItems();
    }
}