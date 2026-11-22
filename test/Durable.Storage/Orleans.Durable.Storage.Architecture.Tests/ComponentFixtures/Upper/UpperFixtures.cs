using Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Base;

namespace Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Upper;

/// <summary>A type the base references upward.</summary>
public sealed class UpperType;

/// <summary>
/// Planted: the internal overload, field, and type of a component this one reaches but which grants it nothing. The
/// public overload is no violation.
/// </summary>
public static class UsesInternals
{
    public static int Public() => FixtureOverloads.Use(1);

    public static string Overload() => FixtureOverloads.Use("internal");

    public static object Field() => FixtureOverloads.Secret;

    public static int Type() => FixtureInternal.Value;
}

/// <summary>Planted: a private protected member of another component's base type. The protected one is no violation.</summary>
public sealed class UpperDerived : FixtureBase
{
    public static int Both() => Hidden() + Shown();
}
