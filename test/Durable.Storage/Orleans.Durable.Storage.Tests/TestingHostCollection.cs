namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The test classes which deploy <c>Orleans.TestingHost</c> clusters: they run one at a time, so their silos' timings
/// and resources do not compete with one another's.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TestingHostCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "TestingHost";
}
