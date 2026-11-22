using Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.ProviderA;

namespace Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.ProviderB;

/// <summary>Planted: one provider's use of another.</summary>
public static class UsesProviderA
{
    public static Type Other() => typeof(UsesImplementation);
}
