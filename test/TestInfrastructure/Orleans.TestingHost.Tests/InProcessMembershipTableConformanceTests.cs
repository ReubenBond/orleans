using Orleans.Clustering.TestKit;
using Orleans.TestingHost.InProcess;
using TestExtensions;
using UnitTests.MembershipTests;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public sealed class InProcessMembershipTableConformanceTests : MembershipTableConformanceTestsBase
{
    protected override MembershipTableTestFixture CreateConformanceFixture()
    {
        var tables = new Dictionary<string, InProcessMembershipTable>(StringComparer.Ordinal);
        return new MembershipTableTestFixture(
            nameof(InProcessMembershipTable),
            clusterId =>
            {
                if (!tables.TryGetValue(clusterId, out var table))
                {
                    table = new InProcessMembershipTable(clusterId);
                    tables.Add(clusterId, table);
                }

                return table.CreateClient();
            });
    }
}
