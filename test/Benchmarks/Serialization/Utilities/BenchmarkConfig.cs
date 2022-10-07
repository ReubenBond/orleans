using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using System;

namespace Benchmarks.Utilities
{
    internal class BenchmarkConfig : ManualConfig
    {
        public BenchmarkConfig()
        {
            ArtifactsPath = ".\\BenchmarkDotNet.Aritfacts." + DateTime.Now.ToString("u").Replace(' ', '_').Replace(':', '-');
            AddExporter(MarkdownExporter.GitHub);
            AddDiagnoser(MemoryDiagnoser.Default);
            AddDiagnoser(new DisassemblyDiagnoser(new DisassemblyDiagnoserConfig(maxDepth: 20, printSource: true)));
            Options |= ConfigOptions.KeepBenchmarkFiles;
        }
    }
}