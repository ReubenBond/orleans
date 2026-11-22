namespace Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Shared;

/// <summary>The contract of a namespace two components share, which the fixture table lists for the Contracts component.</summary>
public interface IFixtureContract;

/// <summary>The implementation in the shared namespace, which belongs to the Shared component.</summary>
public sealed class SharedImplementation : IFixtureContract;
