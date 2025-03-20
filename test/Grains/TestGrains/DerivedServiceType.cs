// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class DerivedServiceType : ServiceType, IDerivedServiceType
    {
        public Task<string> DerivedServiceTypeMethod1()
        {
            return Task.FromResult("DerivedServiceTypeMethod1");
        }

        public Task<string> H1Method()
        {
            return Task.FromResult("H1");
        }

        public Task<string> H2Method()
        {
            return Task.FromResult("H2");
        }

        public Task<string> H3Method()
        {
            return Task.FromResult("H3");
        }
    }
}
