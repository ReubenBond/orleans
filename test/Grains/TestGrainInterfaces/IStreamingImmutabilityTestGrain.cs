// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    public interface IStreamingImmutabilityTestGrain : IGrainWithGuidKey
    {
        Task SubscribeToStream(Guid guid, string providerName);
        Task UnsubscribeFromStream();
        Task SendTestObject(string providerName);
        Task SetTestObjectStringProperty(string value);
        Task<string> GetTestObjectStringProperty();
        Task<string> GetSiloIdentifier();

    }
}