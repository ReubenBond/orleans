namespace Benchmarks.Models
{
    [Serializable]
    [GenerateSerializer]
    [Alias("Benchmarks.Models.SimpleClass")]
    public class SimpleClass
    {
        [Id(0)]
        public int BaseInt { get; set; }
    }
}