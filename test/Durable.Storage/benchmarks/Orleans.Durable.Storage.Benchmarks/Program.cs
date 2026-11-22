using BenchmarkDotNet.Running;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Runs the BenchmarkDotNet microbenchmarks the arguments select.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--wal-perf":
                WalWriteWorkload.ProfileAsync(args).GetAwaiter().GetResult();
                return 0;
            case "--layer-bench":
                return LayerDecompositionBenchmarks.RunAsync(args[1..]).GetAwaiter().GetResult();
            case "--orleans-bench":
                return OrleansMultiProcessBenchmarks.RunCoordinatorAsync(args[1..]).GetAwaiter().GetResult();
            case "--orleans-silo":
                return OrleansMultiProcessBenchmarks.RunSiloProcessAsync(args[1..]).GetAwaiter().GetResult();
            default:
                _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
                return 0;
        }
    }
}
