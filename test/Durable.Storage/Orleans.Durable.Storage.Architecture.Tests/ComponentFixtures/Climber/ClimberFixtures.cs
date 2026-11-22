using Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Composition;

namespace Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Climber;

/// <summary>A layer whose component depends on the composition, which the table check reports.</summary>
public static class ClimberType
{
    public static Type Composition() => typeof(UsesGrantedInternals);
}
