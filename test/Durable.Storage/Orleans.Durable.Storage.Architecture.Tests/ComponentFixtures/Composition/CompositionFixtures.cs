using Microsoft.CodeAnalysis;
using Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Base;

namespace Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Composition;

/// <summary>An internal type under its owner's grant, and the composition's own technology: no violation.</summary>
public static class UsesGrantedInternals
{
    public static int Value() => FixtureInternal.Value;

    public static SyntaxTree? Tree() => null;
}
