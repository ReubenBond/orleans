using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace Benchmarks.Serialization.Utilities;

internal sealed class SerializerBenchmarkConfig : ManualConfig
{
    public SerializerBenchmarkConfig()
    {
        ArtifactsPath = @".\Artifacts\Benchmarks\Serializer";
        AddExporter(MarkdownExporter.GitHub);
        AddExporter(JsonExporter.Full);
        AddDiagnoser(MemoryDiagnoser.Default);
        AddDiagnoser(new DisassemblyDiagnoser(new DisassemblyDiagnoserConfig(
            maxDepth: 3,
            printSource: true,
            exportGithubMarkdown: true,
            exportCombinedDisassemblyReport: true)));
        if (string.Equals(Environment.GetEnvironmentVariable("ORLEANS_BENCHMARK_HARDWARE_COUNTERS"), "1", StringComparison.Ordinal))
        {
            AddHardwareCounters(
                HardwareCounter.BranchInstructions,
                HardwareCounter.BranchMispredictions,
                HardwareCounter.CacheMisses,
                HardwareCounter.InstructionRetired,
                HardwareCounter.TotalCycles);
        }
#if NET10_0_OR_GREATER
        AddJob(Job.ShortRun
            .WithRuntime(CoreRuntime.Core10_0)
            .WithId(".NET 10"));
#else
        AddJob(Job.ShortRun
            .WithRuntime(CoreRuntime.Core80)
            .WithId(".NET 8"));
#endif
        Options |= ConfigOptions.KeepBenchmarkFiles;
    }
}
