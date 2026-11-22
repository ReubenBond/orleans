using Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Upper;

namespace Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Base;

/// <summary>A base type with a member a derived type of another component may call, and one it may not.</summary>
public class FixtureBase
{
    private protected static int Hidden() => 1;

    protected static int Shown() => 2;
}

/// <summary>A public overload beside an internal one, and an internal field.</summary>
public static class FixtureOverloads
{
    internal static readonly object Secret = new();

    public static int Use(int value) => value;

    internal static string Use(string value) => value;
}

/// <summary>An internal type, which only the composition, under the component's grant, may use.</summary>
internal static class FixtureInternal
{
    internal static int Value => 4;
}

/// <summary>Planted: an upward reference, to a component which this one does not reach.</summary>
public static class UsesUpper
{
    public static UpperType? Upper() => null;
}
