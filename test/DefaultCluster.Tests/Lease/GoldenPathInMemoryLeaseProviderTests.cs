using Orleans.Runtime.Development;
using TestExtensions;
using TestExtensions.Runners;
using Xunit;
using Xunit.Abstractions;

namespace DefaultCluster.Tests
{
    [TestCategory("BVT"), TestCategory("Lease")]
    public class GoldenPathInMemoryLeaseProviderTests : GoldenPathLeaseProviderTestRunner, IClassFixture<GoldenPathInMemoryLeaseProviderTests.Fixture>
    {
        public GoldenPathInMemoryLeaseProviderTests(Fixture fixture, ITestOutputHelper output)
            : base(new InMemoryLeaseProvider(fixture.GrainFactory), output)
        {
        }

        public class Fixture : BaseTestClusterFixture
        {
        }
    }
}
