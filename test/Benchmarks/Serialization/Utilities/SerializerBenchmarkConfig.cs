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
        AddHardwareCounters(
            HardwareCounter.BranchInstructions,
            HardwareCounter.BranchMispredictions,
            HardwareCounter.CacheMisses,
            HardwareCounter.InstructionRetired,
            HardwareCounter.TotalCycles);
        AddJob(Job.ShortRun
            .WithRuntime(CoreRuntime.Core10_0)
            .WithId(".NET 10"));
        Options |= ConfigOptions.KeepBenchmarkFiles;
    }
}
