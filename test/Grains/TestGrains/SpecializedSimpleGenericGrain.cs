// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.Grains
{
    public class SpecializedSimpleGenericGrain : SimpleGenericGrain<double>
    {
        public override Task Transform()
        {
            Value = Value * 2.0;
            return Task.CompletedTask;
        }
    }
}
