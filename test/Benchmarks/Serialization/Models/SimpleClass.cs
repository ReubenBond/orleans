// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Benchmarks.Models
{
    [Serializable]
    [GenerateSerializer]
    public class SimpleClass
    {
        [Id(0)]
        public int BaseInt { get; set; }
    }
}