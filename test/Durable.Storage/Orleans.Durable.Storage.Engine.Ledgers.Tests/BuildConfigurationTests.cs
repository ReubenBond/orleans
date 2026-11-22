using System.Reflection;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// The assemblies whose debug assertions the tests evaluate build in the tests' configuration, so the Debug leg runs
/// them in Debug (docs/spec/verification.md §12).
/// </summary>
[NoPageLeakCheck("The test reads assembly attributes; it rents no pages.")]
public sealed class BuildConfigurationTests
{
    [Fact]
    public void TheCodeWhichHoldsDebugAssertionsBuildsInTheTestsConfiguration() =>
        Assert.All(
            [typeof(ArcBufferWriter).Assembly, typeof(Ledger).Assembly, typeof(LedgerStore).Assembly],
            static assembly => Assert.Equal(TestDiagnostics.Configuration, assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration));
}
