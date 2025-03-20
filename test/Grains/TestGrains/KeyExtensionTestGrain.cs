// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

internal class KeyExtensionTestGrain : Grain, IKeyExtensionTestGrain
{
    private readonly Guid uniqueId = Guid.NewGuid();

    public Task<IKeyExtensionTestGrain> GetGrainReference()
    {
        return Task.FromResult(this.AsReference<IKeyExtensionTestGrain>());
    }

    public Task<string> GetActivationId()
    {
        return Task.FromResult(uniqueId.ToString());
    }
}
