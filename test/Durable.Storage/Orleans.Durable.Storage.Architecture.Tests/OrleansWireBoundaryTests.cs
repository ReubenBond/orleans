using System.Reflection;

namespace Orleans.Durable.Storage.Architecture.Tests;

/// <summary>
/// The Orleans composition's wire boundary (orleans.md §5.12, OR-I18): Orleans' own buffers appear only in the types
/// which carry them across it, and no type a grain message serializes holds one of Clog's buffers or pages, so no Orleans
/// message can refer to a Clog page.
/// </summary>
public sealed class OrleansWireBoundaryTests
{
    private const string Composition = Components.Composition;

    private static readonly string[] OrleansBuffers = ["Orleans.Serialization.Buffers.ArcBuffer", "Orleans.Serialization.Buffers.ArcBufferWriter"];

    private static readonly string[] ArcBuffers = ["Orleans.Durable.Storage.Engine.Buffers.ArcBuffer", "Orleans.Durable.Storage.Engine.Buffers.ArcBufferPage"];

    /// <summary>
    /// The types which carry a replica call's payload or DATA, or a forwarded journal entry's record or a forwarded read's
    /// pages, across the wire boundary, and those which pass a forwarded read's pages on to Orleans' journal consumer in the
    /// buffers they arrived in (orleans.md §5.5).
    /// </summary>
    private static readonly HashSet<string> WireBoundary =
    [
        // Only ForwardedReads / RoutedRead / DurableJournalStorage still carry OrleansBuffer across the wire boundary;
        // replica calls and forwarded journal writes carry zero-copy consume-on-serialize WireArcBuffer directly.
        "Orleans.Durable.Storage.WireBuffers",
        "Orleans.Durable.Storage.JournalForwardedRead",
        "Orleans.Durable.Storage.JournalForwardedReadSurrogate",
        "Orleans.Durable.Storage.JournalForwardedReadConverter",
        "Orleans.Durable.Storage.JournalForwardedPage",
        "Orleans.Durable.Storage.JournalForwardedPageSurrogate",
        "Orleans.Durable.Storage.JournalForwardedPageConverter",
        "Orleans.Durable.Storage.ForwardedReads",
        "Orleans.Durable.Storage.JournalRouter",
        "Orleans.Durable.Storage.RoutedRead",
        "Orleans.Durable.Storage.DurableJournalStorage",
    ];

    /// <summary>
    /// Orleans' <c>ArcBuffer</c> and its writer appear in the composition only in the wire boundary's types, their nested
    /// types, and the serializers Orleans generates for them.
    /// </summary>
    [Fact]
    public void OrleansBuffersAppearOnlyAtTheWireBoundary()
    {
        var composition = Repository.ProductionAssemblies().Single(static assembly => assembly.Project == Composition);
        var scanned = MetadataReferences.Scan(composition.Path, static _ => true);
        var violations = scanned
            .Where(static type => !type.Namespace.StartsWith("OrleansCodeGen", StringComparison.Ordinal))
            .Where(static type => type.AssemblyTypes.Any(static reference => OrleansBuffers.Contains(reference.FullName)))
            .Select(static type => type.FullName.Split('+')[0])
            .Where(static outer => !WireBoundary.Contains(outer))
            .Distinct(StringComparer.Ordinal);

        Assert.Contains(scanned, static type => type.FullName == "Orleans.Durable.Storage.WireBuffers" && type.AssemblyTypes.Any(static reference => OrleansBuffers.Contains(reference.FullName)));
        Assert.Empty(violations);
    }

    /// <summary>
    /// No type the composition's grain messages serialize, a <c>[GenerateSerializer]</c> type or either side of a
    /// registered converter, or a type nested in one, such as a union's cases, has a field of Clog's <c>ArcBuffer</c> or <c>ArcBufferPage</c>, directly or as a type argument.
    /// </summary>
    [Fact]
    public void NoSerializedTypeHoldsAnArcBuffer()
    {
        var types = Assembly.Load(Composition).GetTypes();
        var serialized = types
            .Where(static type => type.GetCustomAttributesData().Any(static attribute => attribute.AttributeType.FullName == "Orleans.GenerateSerializerAttribute"))
            .Concat(types
                .Where(static type => type.GetCustomAttributesData().Any(static attribute => attribute.AttributeType.FullName == "Orleans.RegisterConverterAttribute"))
                .SelectMany(static converter => converter.GetInterfaces())
                .Where(static contract => contract.IsGenericType && contract.GetGenericTypeDefinition().FullName == "Orleans.IConverter`2")
                .SelectMany(static contract => contract.GetGenericArguments()))
            .SelectMany(static type => type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Prepend(type))
            .Distinct()
            .ToArray();

        Assert.Contains(serialized, static type => type.Name == "LedgerReplicaWriteRequest");
        Assert.Empty(serialized
            .SelectMany(static type => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(static field => HoldsAnArcBuffer(field.FieldType))
                .Select(field => $"{type.FullName}.{field.Name}")));
    }

    private static bool HoldsAnArcBuffer(Type type) =>
        ArcBuffers.Contains(type.FullName)
        || type.HasElementType && HoldsAnArcBuffer(type.GetElementType()!)
        || type.IsGenericType && type.GetGenericArguments().Any(HoldsAnArcBuffer);
}
