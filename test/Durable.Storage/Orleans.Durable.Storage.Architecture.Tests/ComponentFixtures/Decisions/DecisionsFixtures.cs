using Microsoft.CodeAnalysis;

namespace Orleans.Durable.Storage.Architecture.Tests.ComponentFixtures.Decisions;

/// <summary>Planted: a technology which neither this component nor any it reaches takes.</summary>
public static class UsesRoslyn
{
    public static SyntaxTree? Tree() => null;
}
