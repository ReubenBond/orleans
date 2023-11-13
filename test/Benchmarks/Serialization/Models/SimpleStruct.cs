namespace Benchmarks.Models
{
    [Serializable]
    [GenerateSerializer]
    [Alias("Benchmarks.Models.SimpleStruct")]
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