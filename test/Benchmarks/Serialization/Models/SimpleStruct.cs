// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Benchmarks.Models
{
    [Serializable]
    [GenerateSerializer]
    public struct SimpleStruct
    {
        [Id(0)]
        public int Int { get; set; }

        [Id(1)]
        public bool Bool { get; set; }

        [Id(3)]
        public object AlwaysNull { get; set; }

        [Id(4)]
        public Guid Guid { get; set; }
    }
}