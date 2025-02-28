#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Runtime.GrainDirectory;
using Orleans.TestingHost;
using Tester.Directories;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.GrainDirectory;

[TestCategory("BVT"), TestCategory("Directory")]
public sealed class DistributedGrainDirectoryTests(DistributedGrainDirectoryTests.Fixture fixture, ITestOutputHelper output)
    : GrainDirectoryTests<IGrainDirectory>(output), IClassFixture<DistributedGrainDirectoryTests.Fixture>
{
    public class Fixture : DefaultClusterFixture
    {
        public override void ConfigureCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.UseTestClusterGrainDirectory = false;
            builder.ConfigureSilo((options, siloBuilder) =>
            {
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            });
        }
    }

    private readonly InProcessTestCluster _testCluster = fixture.HostedCluster;
    private InProcessSiloHandle Primary => _testCluster.Primary;

    protected override IGrainDirectory CreateGrainDirectory() =>
        Primary.SiloHost.Services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;

    protected override SiloAddress GetValidSilo(int siloNum) => _testCluster.Silos[siloNum % _testCluster.Silos.Count].SiloAddress;
}

