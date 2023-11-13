using BenchmarkGrainInterfaces.MapReduce;

namespace BenchmarkGrains.MapReduce
{
    [Serializable]
    [Orleans.GenerateSerializer]
    [Alias("BenchmarkGrains.MapReduce.MapProcessor")]
    public class MapProcessor : ITransformProcessor<string, List<string>>
    {
        private static readonly char[] _delimiters = { '.', '?', '!', ' ', ';', ':', ',' };

        public List<string> Process(string input)
        {
            return input
                 .Split(_delimiters, StringSplitOptions.RemoveEmptyEntries)
                 .ToList();
        }
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    [Alias("BenchmarkGrains.MapReduce.ReduceProcessor")]
    public class ReduceProcessor : ITransformProcessor<List<string>, Dictionary<string, int>>
    {
        public Dictionary<string, int> Process(List<string> input)
        {
            return input.GroupBy(v => v.ToLowerInvariant()).Select(v => new
            {
                key = v.Key,
                count = v.Count()
            }).ToDictionary(arg => arg.key, arg => arg.count);
        }
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    [Alias("BenchmarkGrains.MapReduce.EmptyProcessor")]
    public class EmptyProcessor : ITransformProcessor<Dictionary<string, int>, Dictionary<string, int>>
    {
        public Dictionary<string, int> Process(Dictionary<string, int> input)
        {
            return input;
        }
    }
}