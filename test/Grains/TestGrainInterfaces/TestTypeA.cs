// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    [Orleans.GenerateSerializer]
    public class TestTypeA
    {
        [Orleans.Id(0)]
        public ICollection<TestTypeA> Collection { get; set; }
    }
}
