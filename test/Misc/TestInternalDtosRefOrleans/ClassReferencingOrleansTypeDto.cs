// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace UnitTests.DtosRefOrleans
{
    [Serializable]
    [GenerateSerializer]
    public class ClassReferencingOrleansTypeDto
    {
        static ClassReferencingOrleansTypeDto()
        {
            _ = typeof(IGrain).ToString();
        }

        [Id(0)]
        public string MyProperty { get; set; }
    }
}