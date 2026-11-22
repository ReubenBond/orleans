using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Tests;

/// <summary>Registrations, archive stores, and committers for the node registry's tests.</summary>
internal static class NodeRegistrySamples
{
    /// <summary>The LSM, ID …a.</summary>
    public static readonly ArchiveStoreIdentity StoreA = Store(ArchiveStoreKind.Lsm, 0xa);

    /// <summary>Archive logs, ID …b: another kind and another ID than <see cref="StoreA"/>.</summary>
    public static readonly ArchiveStoreIdentity StoreB = Store(ArchiveStoreKind.Logs, 0xb);

    /// <summary>The LSM, ID …b: <see cref="StoreA"/>'s kind with another ID.</summary>
    public static readonly ArchiveStoreIdentity StoreC = Store(ArchiveStoreKind.Lsm, 0xb);

    /// <summary>Archive logs, ID …a: <see cref="StoreA"/>'s ID with another kind.</summary>
    public static readonly ArchiveStoreIdentity StoreD = Store(ArchiveStoreKind.Logs, 0xa);

    public static NodeId Node(int node) => new(Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"01234567-89ab-4def-8000-{node:x12}"), CultureInfo.InvariantCulture));

    public static SiloAddress Silo(int host, int generation) => SiloAddress.New(new IPEndPoint(IPAddress.Parse(string.Create(CultureInfo.InvariantCulture, $"10.0.0.{host}")), 11111), generation);

    /// <summary>Returns node <paramref name="node"/>'s entry at <paramref name="generation"/>, on host <paramref name="host"/>, by default its own.</summary>
    public static NodeRegistryEntry Entry(int node, int generation, int? ordinal = null, int? host = null) =>
        new(Node(node), Silo(host ?? node, generation), ordinal);

    /// <summary>Returns a committer over <paramref name="register"/> with no value limit to speak of.</summary>
    public static NodeRegistryCommitter Committer(Orleans.Durable.Storage.Engine.MetaStore.IConditionalRegister register, int maxValueLength = 1 << 20) =>
        new(register, maxValueLength, NullLogger.Instance);

    /// <summary>
    /// Registers <paramref name="entries"/> in order through one committer over <paramref name="register"/>, each naming for
    /// replacement the node an earlier one registered at its ordinal, and asserts each is registered.
    /// </summary>
    public static async Task RegisterInOrderAsync(Orleans.Durable.Storage.Engine.MetaStore.IConditionalRegister register, params NodeRegistryEntry[] entries)
    {
        await using var committer = Committer(register);
        var holders = new Dictionary<int, NodeId>();
        foreach (var entry in entries)
        {
            NodeId? nodeToReplace = entry.Ordinal is { } ordinal && holders.TryGetValue(ordinal, out var holder) && holder != entry.Node ? holder : null;
            _ = Assert.IsType<NodeRegistrationOutcome.Registered>((await committer.RegisterAsync(entry, StoreA, nodeToReplace, TestContext.Current.CancellationToken)).Value);
            if (entry.Ordinal is { } held)
            {
                holders[held] = entry.Node;
            }
        }
    }

    /// <summary>Reads the register's current value.</summary>
    public static async Task<NodeRegistryValue> ReadAsync(ScriptedConditionalRegister register) =>
        (await NodeRegistryCodec.ReadAsync(register, TestContext.Current.CancellationToken)).Value;

    private static ArchiveStoreIdentity Store(ArchiveStoreKind kind, int id) =>
        new(kind, new ArchiveStorePrefix("abcdefghij"), new ArchiveStoreId(UInt128.Parse(string.Create(CultureInfo.InvariantCulture, $"fedcba9876544321a000{id:x12}"), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)));
}
