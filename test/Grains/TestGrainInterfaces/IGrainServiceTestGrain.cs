// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface IGrainServiceTestGrain : IGrainWithIntegerKey
{

    Task<string> GetHelloWorldUsingCustomService();
    Task<bool> CallHasStarted();
    Task<bool> CallHasStartedInBackground();
    Task<bool> CallHasInit();
    Task<string> GetServiceConfigProperty();
    Task<string> EchoViaExtension(string what);
}

public interface IEchoExtension : IGrainExtension
{
    Task<string> Echo(string what);
}
