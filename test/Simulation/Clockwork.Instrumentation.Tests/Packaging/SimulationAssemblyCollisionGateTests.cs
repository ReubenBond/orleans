using System.IO.Compression;
using Orleans.Testing.Simulation.Instrumentation.Tests.Infrastructure;

namespace Orleans.Testing.Simulation.Instrumentation.Tests.Packaging;

public sealed class SimulationAssemblyCollisionGateTests
{
    [Theory]
    [InlineData("Clockwork.dll")]
    [InlineData("Orleans.Testing.Simulation.dll")]
    [InlineData("Orleans.Testing.Simulation.Testing.dll")]
    public void ABoundaryContainingOnlyOneAssemblyFamilyIsAccepted(string assembly) =>
        SimulationAssemblyCollisionGate.AssertNoCollision("probe", [assembly]);

    [Fact]
    public void LegacyAndFinalSimulationAssembliesInOneBoundaryAreRejected()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            SimulationAssemblyCollisionGate.AssertNoCollision(
                "probe",
                [
                    @"packages\clockwork.simulation\lib\net11.0\Clockwork.dll",
                    "Orleans.Testing.Simulation.Testing, Version=1.0.0.0",
                ]));

        Assert.Contains("legacy Clockwork.dll", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Orleans.Testing.Simulation.Testing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluatedCompileAndRuntimeAssetsRejectLegacyAndFinalAssemblies()
    {
        string directory = TestArtifacts.CreateUnique("orleans-collision-assets");
        string assetsPath = Path.Combine(directory, "project.assets.json");
        File.WriteAllText(
            assetsPath,
            """
            {
              "targets": {
                "net11.0": {
                  "Legacy/1.0.0": {
                    "compile": {
                      "lib/net11.0/Clockwork.dll": {}
                    }
                  },
                  "Final/1.0.0": {
                    "runtime": {
                      "lib/net11.0/Orleans.Testing.Simulation.dll": {}
                    }
                  }
                }
              }
            }
            """);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SimulationAssemblyCollisionGate.AssertNoCollisionInAssetsFile(assetsPath));

        Assert.Contains("evaluated assets", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Clockwork", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Orleans.Testing.Simulation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackedRuntimePayloadRejectsLegacyAndFinalAssemblies()
    {
        string directory = TestArtifacts.CreateUnique("orleans-collision-package");
        string packagePath = Path.Combine(directory, "collision.nupkg");
        using (ZipArchive package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            package.CreateEntry("lib/net11.0/Clockwork.dll");
            package.CreateEntry("lib/net11.0/Orleans.Testing.Simulation.Testing.dll");
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => SimulationAssemblyCollisionGate.AssertNoCollisionInPackage(packagePath));

        Assert.Contains("package payload", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Clockwork", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Orleans.Testing.Simulation.Testing", exception.Message, StringComparison.Ordinal);
    }
}
