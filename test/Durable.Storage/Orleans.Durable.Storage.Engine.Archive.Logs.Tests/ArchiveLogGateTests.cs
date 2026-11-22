using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// <see cref="ArchiveLogGate"/> (orleans.md §5.13, OR-I8, OR-I20): the gate passes only when the store's record is the
/// store the node registry names, kind and ID, with the store's own fields, and its caller parameters are the process's;
/// each refusal names what differs, and a refusal of the kind makes no request.
/// </summary>
public sealed class ArchiveLogGateTests : IDisposable
{
    private static readonly ImmutableDictionary<string, string> Parameters = ImmutableDictionary<string, string>.Empty.Add("format", "json");

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly InMemoryObjectStore _objects = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task TheGatePassesWithTheRecordsIdentityAndParametersAndBindsBothStores()
    {
        var logs = new MemoryArchiveLogStore();
        var store = new LogArchiveStore(logs, _objects, ArchiveLogNames.Hexadecimal);
        var record = await store.StoreRecordAsync(Parameters, Token);

        var gate = await ArchiveLogGate.PassAsync(store, Identity(record.Id), Parameters, Token);

        Assert.Equal(record.Id, gate.Record.Id);
        Assert.Same(logs, gate.Logs);
        Assert.Same(_objects, gate.Objects);
        Assert.Same(ArchiveLogNames.Hexadecimal, gate.Names);
    }

    public static TheoryData<string> Refusals => ["kind", "id", "parameter"];

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task TheGateRefusesWhatDiffers(string refusal)
    {
        var store = new LogArchiveStore(new MemoryArchiveLogStore(), _objects, ArchiveLogNames.Hexadecimal);
        var record = await store.StoreRecordAsync(Parameters, Token);
        var (identity, parameters) = refusal switch
        {
            "kind" => (new ArchiveStoreIdentity(ArchiveStoreKind.Lsm, new ArchiveStorePrefix("t"), record.Id), Parameters),
            "id" => (Identity(ArchiveStoreId.NewRandom()), Parameters),
            _ => (Identity(record.Id), Parameters.SetItem("format", "binary")),
        };

        var mismatch = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await ArchiveLogGate.PassAsync(store, identity, parameters, Token));

        var field = refusal switch
        {
            "kind" => ArchiveStoreRecord.KindField,
            "id" => ArchiveStoreRecord.IdField,
            _ => "format",
        };
        Assert.Equal(field, mismatch.Field);
        Assert.Equal(refusal == "parameter", mismatch.IsParameter);
    }

    /// <summary>
    /// archive-logs.md §7.5, D5: the gate binds the log-naming scheme the record pins: a store whose names follow another
    /// scheme is refused at the record's <c>name-scheme</c> field, and a drain takes its names only from the gate, since
    /// <see cref="ArchiveLogOptions"/> names none.
    /// </summary>
    [Fact]
    public async Task TheGateRefusesAnotherNamingScheme()
    {
        var logs = new MemoryArchiveLogStore();
        var record = await new LogArchiveStore(logs, _objects, ArchiveLogNames.Hexadecimal).StoreRecordAsync(Parameters, Token);
        var other = new LogArchiveStore(logs, _objects, new ArchiveLogNames("other-1", static key => "o/" + Convert.ToHexStringLower(key)));

        var mismatch = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await ArchiveLogGate.PassAsync(other, Identity(record.Id), Parameters, Token));

        Assert.Equal("name-scheme", mismatch.Field);
        Assert.DoesNotContain(typeof(ArchiveLogOptions).GetProperties(), static property => property.PropertyType == typeof(ArchiveLogNames));
    }

    /// <summary>
    /// orleans.md §5.13, D12: a joining silo, whose registry records an identity, pointed at an empty container is refused
    /// at the record's ID, which it only reads, and creates nothing there.
    /// </summary>
    [Fact]
    public async Task AJoiningSiloPointedAtAnEmptyContainerCreatesNothing()
    {
        var store = new LogArchiveStore(new MemoryArchiveLogStore(), _objects, ArchiveLogNames.Hexadecimal);

        var mismatch = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await ArchiveLogGate.PassAsync(store, Identity(ArchiveStoreId.NewRandom()), Parameters, Token));

        Assert.Equal((ArchiveStoreRecord.IdField, "nothing"), (mismatch.Field, mismatch.StoredValue));
        await foreach (var listed in _objects.ListAsync(string.Empty, cancellationToken: Token))
        {
            Assert.Fail($"The refused gate created '{listed.Name}'.");
        }
    }

    /// <summary>
    /// orleans.md §5.13, D12: a joining silo pointed at another store's container is refused at the record's ID, and
    /// leaves that store's record as it was.
    /// </summary>
    [Fact]
    public async Task AJoiningSiloPointedAtAnotherStoresContainerCreatesNothing()
    {
        var store = new LogArchiveStore(new MemoryArchiveLogStore(), _objects, ArchiveLogNames.Hexadecimal);
        var theirs = await store.StoreRecordAsync(Parameters, Token);
        var before = (await _objects.ReadVersionedAsync(new ObjectName(ArchiveLogNames.StoreRecord), Token))!.Value;
        before.Content.Dispose();

        var mismatch = await Assert.ThrowsAsync<ArchiveStoreMismatchException>(async () => await ArchiveLogGate.PassAsync(store, Identity(ArchiveStoreId.NewRandom()), Parameters, Token));

        Assert.Equal((ArchiveStoreRecord.IdField, theirs.Id.ToString()), (mismatch.Field, mismatch.StoredValue));
        var after = (await _objects.ReadVersionedAsync(new ObjectName(ArchiveLogNames.StoreRecord), Token))!.Value;
        after.Content.Dispose();
        Assert.Equal(before.Version, after.Version);
    }

    private static ArchiveStoreIdentity Identity(ArchiveStoreId id) => new(ArchiveStoreKind.Logs, new ArchiveStorePrefix("t"), id);
}
