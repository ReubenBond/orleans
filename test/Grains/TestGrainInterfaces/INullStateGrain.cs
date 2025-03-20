// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces;

[GenerateSerializer]
public class NullableState
{
    [Id(0)]
    public string Name { get; set; }
}

public interface INullStateGrain : IGrainWithIntegerKey
{
    Task SetStateAndDeactivate(NullableState state);
    Task<NullableState> GetState();
}