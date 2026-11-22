using Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Shared;

namespace Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.ProviderA;

/// <summary>Planted: a provider's use of an implementation in a namespace it shares with a contract. The contract's use is no violation.</summary>
public static class UsesImplementation
{
    public static SharedImplementation? Implementation() => null;

    public static IFixtureContract? Contract() => null;
}
