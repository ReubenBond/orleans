// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

public interface ISimplePersistentGrain : ISimpleGrain
{
    Task SetA(int a, bool deactivate);
    Task<Guid> GetVersion();
    Task<object> GetRequestContext();
    Task SetRequestContext(int data);
}
