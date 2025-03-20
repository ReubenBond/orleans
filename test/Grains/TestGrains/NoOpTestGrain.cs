// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class NoOpTestGrain : Grain, INoOpTestGrain
    {
    }
}
