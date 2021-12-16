using Xunit;

namespace UnitTests.CompatibilityTests
{
    [CollectionDefinition("default")]
public class DefaultCollection : ICollectionFixture<TestClusterFixture>
{
}
}
