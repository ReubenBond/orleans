// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IFaultableConsumerGrain : IGrainWithGuidKey
    {
        Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse);

        Task SetFailPeriod(TimeSpan failPeriod);

        Task StopConsuming();

        Task<int> GetNumberConsumed();

        Task<int> GetNumberFailed();

        Task<int> GetErrorCount();
    }
}
