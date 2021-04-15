using Orleans.Serialization;
using System;

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