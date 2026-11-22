using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using Orleans.Serialization;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The grain messages of the node registry (orleans.md §7.2, §9): every case of <see cref="NodeRegistrationOutcome"/>
/// crosses through its surrogate, whose wire encoding of every case a snapshot pins; an unknown case tag, and an occupied
/// ordinal without its ordinal or occupant or with an entry ordinal but no entry, is refused with <see cref="InvalidDataException"/>; a registration's entry
/// and archive store cross unchanged; and an archive store whose surrogate names an undefined kind or the zero ID is refused.
/// </summary>
[NoPageLeakCheck("Grain messages carry no pooled pages (OR-I18).")]
public sealed class NodeRegistryMessageTests : IDisposable
{
    private static readonly NodeRegistrationOutcome[] AllCases =
    [
        new NodeRegistrationOutcome.Registered(7),
        NodeRegistrationOutcome.Stale.Instance,
        NodeRegistrationOutcome.WrongArchiveStore.Instance,
        NodeRegistrationOutcome.WrongObjectLedgerStore.Instance,
        NodeRegistrationOutcome.Refused.Instance,
        new NodeRegistrationOutcome.OrdinalOccupied(0, Node(9), HasEntry: false, EntryOrdinal: null),
        new NodeRegistrationOutcome.OrdinalOccupied(3, Node(9), HasEntry: true, EntryOrdinal: 0),
        new NodeRegistrationOutcome.OrdinalOccupied(3, Node(9), HasEntry: true, EntryOrdinal: null),
    ];

    private readonly ServiceProvider _services = new ServiceCollection()
        .AddSerializer(static serializer => serializer.AddAssembly(typeof(NodeRegistryEntry).Assembly))
        .BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Fact]
    public Task EveryOutcomeCaseHasAPinnedWireEncoding()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var converter = new NodeRegistrationOutcomeConverter();
        return Verify(AllCases.Select(outcome => new
        {
            Case = outcome.Value!.GetType().Name,
            Surrogate = converter.ConvertToSurrogate(outcome),
            Wire = Convert.ToHexStringLower(serializer.SerializeToArray(outcome)),
        }));
    }

    [Fact]
    public void EveryOutcomeCaseCrossesUnchanged()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        foreach (var outcome in AllCases)
        {
            Assert.Equal(outcome, serializer.Deserialize<NodeRegistrationOutcome>(serializer.SerializeToArray(outcome)));
        }
    }

    /// <summary>
    /// An archive store's identity crosses as its surrogate, whose wire encoding a snapshot pins: each kind, an ID whose
    /// halves differ, so a swap of the halves shows, and the default identity as zeros.
    /// </summary>
    [Fact]
    public Task AnArchiveStoreHasAPinnedWireEncoding()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var converter = new ArchiveStoreIdentityConverter();
        var id = new ArchiveStoreId(new UInt128(0x0102030405060708, 0x090a0b0c0d0e0f10));
        var prefix = new ArchiveStorePrefix("abcdefghij");
        ArchiveStoreIdentity[] identities = [new(ArchiveStoreKind.Lsm, prefix, id), new(ArchiveStoreKind.Logs, prefix, id), new(ArchiveStoreKind.Memory, prefix, id), default];
        return Verify(identities.Select(identity => new
        {
            Identity = identity == default ? "default" : $"{identity.Kind} {identity.Prefix} {identity.Id}",
            Surrogate = converter.ConvertToSurrogate(identity),
            Wire = Convert.ToHexStringLower(serializer.SerializeToArray(identity)),
        }));
    }

    /// <summary>
    /// An object-ledger store ID crosses as its surrogate, whose wire encoding a snapshot pins: an ID whose halves differ,
    /// so a swap of the halves shows, and the default ID as zeros.
    /// </summary>
    [Fact]
    public Task AnObjectLedgerStoreIdHasAPinnedWireEncoding()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var converter = new ObjectLedgerStoreIdConverter();
        var id = new ObjectLedgerStoreId(new UInt128(0x0102030405060708, 0x090a0b0c0d0e0f10));
        ObjectLedgerStoreId[] ids = [id, default];
        return Verify(ids.Select(item => new
        {
            Id = item == default ? "default" : item.ToString(),
            Surrogate = converter.ConvertToSurrogate(item),
            Wire = Convert.ToHexStringLower(serializer.SerializeToArray(item)),
        })).DontScrubGuids();
    }

    [Fact]
    public void AnObjectLedgerStoreIdCrossesUnchanged()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var id = new ObjectLedgerStoreId(new UInt128(0x0102030405060708, 0x090a0b0c0d0e0f10));
        Assert.Equal(id, serializer.Deserialize<ObjectLedgerStoreId>(serializer.SerializeToArray(id)));
        Assert.Equal(default, serializer.Deserialize<ObjectLedgerStoreId>(serializer.SerializeToArray(default(ObjectLedgerStoreId))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(255)]
    public void AnUnknownCaseTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new NodeRegistrationOutcomeConverter().ConvertFromSurrogate(new NodeRegistrationOutcomeSurrogate { Case = tag }));

    [Theory]
    [InlineData(-1, true, false, null)]
    [InlineData(0, false, false, null)]
    [InlineData(0, true, false, 0)]
    [InlineData(0, true, true, -1)]
    public void AMalformedOccupiedOrdinalIsRefused(int ordinal, bool occupant, bool hasEntry, int? entryOrdinal) =>
        _ = Assert.Throws<InvalidDataException>(() => new NodeRegistrationOutcomeConverter().ConvertFromSurrogate(new NodeRegistrationOutcomeSurrogate
        {
            Case = NodeRegistrationOutcomeSurrogate.OrdinalOccupiedTag,
            Ordinal = ordinal,
            Occupant = occupant ? Node(9) : default,
            HasEntry = hasEntry,
            EntryOrdinal = entryOrdinal,
        }));

    [Fact]
    public void ARegistrationsEntryAndArchiveStoreCrossUnchanged()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        NodeRegistryEntry[] entries =
        [
            new(Node(1), Silo(1, 3), ordinal: 2, failureDomain: "zone-a"),
            Entry(2, 1),
            new(Node(3), SiloAddress.New(System.Net.IPEndPoint.Parse("[2001:db8::3]:11111"), 4), ordinal: 0),
            new(Node(4), Silo(4, 1), failureDomain: "zone-b", diskless: true),
        ];

        foreach (var entry in entries)
        {
            Assert.Equal(entry, serializer.Deserialize<NodeRegistryEntry>(serializer.SerializeToArray(entry)));
        }

        Assert.Equal(StoreB, serializer.Deserialize<ArchiveStoreIdentity>(serializer.SerializeToArray(StoreB)));
        Assert.Equal(new ArchiveStoreIdentity(ArchiveStoreKind.Memory, new ArchiveStorePrefix("t"), new ArchiveStoreId(UInt128.MaxValue)), serializer.Deserialize<ArchiveStoreIdentity>(serializer.SerializeToArray(new ArchiveStoreIdentity(ArchiveStoreKind.Memory, new ArchiveStorePrefix("t"), new ArchiveStoreId(UInt128.MaxValue)))));
        Assert.Equal(default, serializer.Deserialize<ArchiveStoreIdentity>(serializer.SerializeToArray(default(ArchiveStoreIdentity))));
    }

    /// <summary>
    /// An archive store's surrogate which names no store but is not the default identity's, an undefined kind, a prefix
    /// which is none, or the zero ID, is refused with <see cref="InvalidDataException"/> around the identity's refusal; the
    /// default identity crosses for the committer to refuse, and only the all-zero surrogate is the default.
    /// </summary>
    [Theory]
    [InlineData(4, 0UL, 1UL, "t", "kind")]
    [InlineData(-1, 0UL, 1UL, "t", "kind")]
    [InlineData(0, 0UL, 1UL, "t", "kind")]
    [InlineData(0, 1UL, 0UL, "t", "kind")]
    [InlineData(1, 0UL, 1UL, null, "prefix")]
    [InlineData(1, 0UL, 1UL, "Upper", "prefix")]
    [InlineData(1, 0UL, 1UL, "seventeen-chars-x", "prefix")]
    [InlineData(1, 0UL, 0UL, "t", "value")]
    public void AMalformedArchiveStoreIsRefused(int kind, ulong upper, ulong lower, string? prefix, string refused)
    {
        var failure = Assert.Throws<InvalidDataException>(() => new ArchiveStoreIdentityConverter().ConvertFromSurrogate(new ArchiveStoreIdentitySurrogate { Kind = kind, UpperId = upper, LowerId = lower, Prefix = prefix }));

        Assert.Equal(refused, Assert.IsAssignableFrom<ArgumentException>(failure.InnerException).ParamName);
    }

    [Theory]
    [InlineData(1, 1UL, 0UL)]
    [InlineData(3, 0UL, 1UL)]
    public void AnArchiveStoreWithOneHalfOfItsIdIsAccepted(int kind, ulong upper, ulong lower) =>
        Assert.Equal(
            new ArchiveStoreIdentity((ArchiveStoreKind)kind, new ArchiveStorePrefix("t"), new ArchiveStoreId(new UInt128(upper, lower))),
            new ArchiveStoreIdentityConverter().ConvertFromSurrogate(new ArchiveStoreIdentitySurrogate { Kind = kind, UpperId = upper, LowerId = lower, Prefix = "t" }));
}
