// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// Stream consumer grain that just counts the events it consumes
    /// </summary>
    public interface IConsumerEventCountingGrain : IGrainWithGuidKey
    {
        Task BecomeConsumer(Guid streamId, string providerToUse);

        Task StopConsuming();

        Task<int> GetNumberConsumed();
    }
}