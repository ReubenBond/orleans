using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The object store contract (archive §3), run against every <see cref="IObjectStore"/> provider through examples; the
/// conformance kit judges generated sequences with <see cref="ObjectStoreSpec"/>.
/// </summary>
internal static class ObjectStoreContract
{
    private static readonly ObjectName Drain = new("d/7/0/0123456789abcdef");
    private static readonly ObjectName OtherDrain = new("d/7/3/fedcba9876543210");
    private static readonly ObjectName Compaction = new("c/0/2/00112233");
    private static readonly ObjectName Another = new("a/7/42");

    /// <summary>
    /// Gets the name of every example, which each provider's tests run.
    /// </summary>
    internal static TheoryData<string> Examples =>
    [
        nameof(CreateStoresOnlyWhenAbsent),
        nameof(ReadReturnsExactlyTheRange),
        nameof(AReadPastTheEndThrows),
        nameof(AbsentObjectsReadAsNull),
        nameof(ReplaceAppliesOnlyAtTheExpectedVersion),
        nameof(ReplaceOfNoVersionCreatesOnlyWhenAbsent),
        nameof(DeleteLeavesTheObjectAbsent),
        nameof(ListReturnsTheObjectsWithThePrefixInOrderWithTheirLengths),
        nameof(ContentIsTheCallersOnlyDuringTheCall),
        nameof(ConcurrentCreatesOfOneNameCreateOnce),
        nameof(ConcurrentReplacesOfOneVersionApplyOnce),
        nameof(InvalidArgumentsAreRefused),
        nameof(AWrongCrc32CIsRefusedAndLeavesTheObjectAsItWas),
        nameof(ANameWhoseFileCouldShortenToAnotherNamesIsDistinct),
        nameof(WindowsDeviceNamesAreObjectNames),
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Runs the example <paramref name="name"/> against <paramref name="store"/>.
    /// </summary>
    /// <param name="name">The example's name.</param>
    /// <param name="store">The store.</param>
    /// <param name="refusesOverPlainHttp">
    /// Whether a service which the store reaches over plain HTTP checks the writer's CRC-32C, so its refusal is an unknown
    /// outcome (archive §9).
    /// </param>
    internal static Task RunAsync(string name, IObjectStore store, bool refusesOverPlainHttp = false) => name switch
    {
        nameof(CreateStoresOnlyWhenAbsent) => CreateStoresOnlyWhenAbsent(store),
        nameof(ReadReturnsExactlyTheRange) => ReadReturnsExactlyTheRange(store),
        nameof(AReadPastTheEndThrows) => AReadPastTheEndThrows(store),
        nameof(AbsentObjectsReadAsNull) => AbsentObjectsReadAsNull(store),
        nameof(ReplaceAppliesOnlyAtTheExpectedVersion) => ReplaceAppliesOnlyAtTheExpectedVersion(store),
        nameof(ReplaceOfNoVersionCreatesOnlyWhenAbsent) => ReplaceOfNoVersionCreatesOnlyWhenAbsent(store),
        nameof(DeleteLeavesTheObjectAbsent) => DeleteLeavesTheObjectAbsent(store),
        nameof(ListReturnsTheObjectsWithThePrefixInOrderWithTheirLengths) => ListReturnsTheObjectsWithThePrefixInOrderWithTheirLengths(store),
        nameof(ContentIsTheCallersOnlyDuringTheCall) => ContentIsTheCallersOnlyDuringTheCall(store),
        nameof(ConcurrentCreatesOfOneNameCreateOnce) => ConcurrentCreatesOfOneNameCreateOnce(store),
        nameof(ConcurrentReplacesOfOneVersionApplyOnce) => ConcurrentReplacesOfOneVersionApplyOnce(store),
        nameof(InvalidArgumentsAreRefused) => InvalidArgumentsAreRefused(store),
        nameof(AWrongCrc32CIsRefusedAndLeavesTheObjectAsItWas) => AWrongCrc32CIsRefusedAndLeavesTheObjectAsItWas(store, refusesOverPlainHttp),
        nameof(ANameWhoseFileCouldShortenToAnotherNamesIsDistinct) => ANameWhoseFileCouldShortenToAnotherNamesIsDistinct(store),
        nameof(WindowsDeviceNamesAreObjectNames) => WindowsDeviceNamesAreObjectNames(store),
        _ => throw new ArgumentException($"'{name}' is not an object store example.", nameof(name)),
    };

    internal static async Task CreateStoresOnlyWhenAbsent(IObjectStore store)
    {
        Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await CreateAsync(store, Drain, Bytes(100, 1)));
        Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Exists.Instance, await CreateAsync(store, Drain, Bytes(50, 2)));
        Assert.Equal(Bytes(100, 1), await ReadAllAsync(store, Drain));
    }

    /// <summary>
    /// Reads of every size return exactly their bytes, those of several megabytes among them, which no buffer page
    /// holds whole.
    /// </summary>
    internal static async Task ReadReturnsExactlyTheRange(IObjectStore store)
    {
        const int MiB = 1024 * 1024;
        var content = Bytes(5 * MiB + 17, 3);
        _ = await CreateAsync(store, Drain, content);
        foreach (var (offset, length) in new[] { (0, 1), (0, content.Length), (4095, 4097), (content.Length - 1, 1), (100_000, 60_000), (MiB - 3, 4 * MiB) })
        {
            using var read = Assert.IsType<ArcBuffer>(await store.ReadAsync(Drain, offset, length, Token));
            Assert.Equal(content.AsSpan(offset, length).ToArray(), read.ToArray());
        }
    }

    /// <summary>
    /// A create or replace whose writer's CRC-32C does not match its content is refused with
    /// <see cref="ObjectContentChangedException"/>, or, by a service over plain HTTP, with an <see cref="IOException"/>,
    /// and leaves the object as it was; the content's own CRC-32C then writes it (archive §3, §9).
    /// </summary>
    internal static async Task AWrongCrc32CIsRefusedAndLeavesTheObjectAsItWas(IObjectStore store, bool refusesOverPlainHttp = false)
    {
        Task RefusedAsync(Func<Task> write) => refusesOverPlainHttp
            ? Assert.ThrowsAsync<IOException>(write)
            : Assert.ThrowsAsync<ObjectContentChangedException>(write);

        var content = Bytes(4096 + 17, 30);
        var replacement = Bytes(70, 31);
        var contentCrc32C = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, content);
        var replacementCrc32C = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, replacement);

        await RefusedAsync(async () => await CreateAsync(store, Drain, content, contentCrc32C ^ 1));
        Assert.Null(await store.ReadVersionedAsync(Drain, Token));
        Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await CreateAsync(store, Drain, content, contentCrc32C));
        Assert.Equal(content, await ReadAllAsync(store, Drain));

        await RefusedAsync(async () => await ReplaceAsync(store, ObjectName.Manifest, null, content, contentCrc32C ^ 1));
        Assert.Null(await store.ReadVersionedAsync(ObjectName.Manifest, Token));
        var applied = Assert.IsType<ObjectReplaceOutcome.Applied>((await ReplaceAsync(store, ObjectName.Manifest, null, content, contentCrc32C)).Value);
        await RefusedAsync(async () => await ReplaceAsync(store, ObjectName.Manifest, applied.Version, replacement, replacementCrc32C ^ 1));
        await AssertHoldsAsync(store, ObjectName.Manifest, content, applied.Version);
        _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await ReplaceAsync(store, ObjectName.Manifest, applied.Version, replacement, replacementCrc32C)).Value);
    }

    internal static async Task AReadPastTheEndThrows(IObjectStore store)
    {
        _ = await CreateAsync(store, Drain, Bytes(10, 4));
        _ = await Assert.ThrowsAsync<EndOfStreamException>(async () => await store.ReadAsync(Drain, 5, 6, Token));
        _ = await Assert.ThrowsAsync<EndOfStreamException>(async () => await store.ReadAsync(Drain, 10, 1, Token));
        _ = await Assert.ThrowsAsync<EndOfStreamException>(async () => await store.ReadAsync(Drain, 1_000, 1, Token));
    }

    internal static async Task AbsentObjectsReadAsNull(IObjectStore store)
    {
        Assert.Null(await store.ReadAsync(Drain, 0, 1, Token));
        Assert.Null(await store.ReadVersionedAsync(ObjectName.Manifest, Token));
        Assert.Empty(await store.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token));
    }

    internal static async Task ReplaceAppliesOnlyAtTheExpectedVersion(IObjectStore store)
    {
        var first = Assert.IsType<ObjectReplaceOutcome.Applied>((await ReplaceAsync(store, ObjectName.Manifest, null, Bytes(40, 5))).Value);
        await AssertHoldsAsync(store, ObjectName.Manifest, Bytes(40, 5), first.Version);

        var second = Assert.IsType<ObjectReplaceOutcome.Applied>((await ReplaceAsync(store, ObjectName.Manifest, first.Version, Bytes(70, 6))).Value);
        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance, await ReplaceAsync(store, ObjectName.Manifest, first.Version, Bytes(30, 7)));
        Assert.Equal<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance, await ReplaceAsync(store, ObjectName.Manifest, new ObjectVersion("never-issued"), Bytes(30, 8)));
        await AssertHoldsAsync(store, ObjectName.Manifest, Bytes(70, 6), second.Version);
    }

    internal static async Task ReplaceOfNoVersionCreatesOnlyWhenAbsent(IObjectStore store)
    {
        _ = Assert.IsType<ObjectReplaceOutcome.Applied>((await ReplaceAsync(store, ObjectName.Manifest, null, Bytes(10, 9))).Value);
        Assert.Equal<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance, await ReplaceAsync(store, ObjectName.Manifest, null, Bytes(10, 10)));
        Assert.Equal<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance, await ReplaceAsync(store, Drain, new ObjectVersion("never-issued"), Bytes(10, 11)));
        Assert.Null(await store.ReadVersionedAsync(Drain, Token));

        _ = await CreateAsync(store, Drain, Bytes(10, 12));
        Assert.Equal<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance, await ReplaceAsync(store, Drain, null, Bytes(10, 13)));
        Assert.Equal(Bytes(10, 12), await ReadAllAsync(store, Drain));
    }

    internal static async Task DeleteLeavesTheObjectAbsent(IObjectStore store)
    {
        _ = await CreateAsync(store, Drain, Bytes(10, 14));
        var replaced = Assert.IsType<ObjectReplaceOutcome.Applied>((await ReplaceAsync(store, ObjectName.Manifest, null, Bytes(10, 15))).Value);

        await store.DeleteAsync(Drain, Token);
        await store.DeleteAsync(ObjectName.Manifest, Token);
        await store.DeleteAsync(Another, Token);
        Assert.Null(await store.ReadAsync(Drain, 0, 1, Token));
        Assert.Null(await store.ReadVersionedAsync(ObjectName.Manifest, Token));
        Assert.Empty(await store.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token));
        Assert.Equal<ObjectReplaceOutcome>(ObjectReplaceOutcome.Mismatch.Instance, await ReplaceAsync(store, ObjectName.Manifest, replaced.Version, Bytes(10, 16)));
    }

    internal static async Task ListReturnsTheObjectsWithThePrefixInOrderWithTheirLengths(IObjectStore store)
    {
        var lengths = new Dictionary<ObjectName, int> { [OtherDrain] = 5, [Another] = 1, [Compaction] = 300, [Drain] = 17 };
        foreach (var (name, length) in lengths)
        {
            _ = await CreateAsync(store, name, Bytes(length, 17));
        }

        _ = await ReplaceAsync(store, ObjectName.Manifest, null, Bytes(9, 18));
        lengths[ObjectName.Manifest] = 9;
        var all = await store.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token);
        Assert.Equal([Another, Compaction, Drain, OtherDrain, ObjectName.Manifest], all.Select(static listed => listed.Name));
        Assert.All(all, listed => Assert.Equal(lengths[listed.Name], listed.Length));
        Assert.Equal([Drain, OtherDrain], await NamesAsync(store, "d/"));
        Assert.Equal([Drain, OtherDrain], await NamesAsync(store, "d/7/"));
        Assert.Equal([OtherDrain], await NamesAsync(store, "d/7/3"));
        Assert.Equal([ObjectName.Manifest], await NamesAsync(store, "man"));
        Assert.Empty(await NamesAsync(store, "d/8/"));

        // A replace changes the manifest's length, which the next listing gives.
        var version = (await ReadVersionedAsync(store, ObjectName.Manifest)).Version;
        _ = await ReplaceAsync(store, ObjectName.Manifest, version, Bytes(4_000, 19));
        Assert.Equal(new ListedObject(ObjectName.Manifest, 4_000), Assert.Single(await store.ListAsync("man", cancellationToken: Token).ToListAsync(Token)));
    }

    /// <summary>
    /// Returns the names a listing of <paramref name="prefix"/> gives, in its order.
    /// </summary>
    internal static async Task<List<ObjectName>> NamesAsync(IObjectStore store, string prefix) =>
        [.. (await store.ListAsync(prefix, cancellationToken: Token).ToListAsync(Token)).Select(static listed => listed.Name)];

    internal static async Task ContentIsTheCallersOnlyDuringTheCall(IObjectStore store)
    {
        var created = Bytes(64, 19);
        var replaced = Bytes(64, 20);
        _ = await CreateAsync(store, Drain, created);
        _ = await ReplaceAsync(store, ObjectName.Manifest, null, replaced);
        Array.Fill(created, (byte)0);
        Array.Fill(replaced, (byte)0);

        Assert.Equal(Bytes(64, 19), await ReadAllAsync(store, Drain));
        Assert.Equal(Bytes(64, 20), (await ReadVersionedAsync(store, ObjectName.Manifest)).Content);
    }

    internal static async Task ConcurrentCreatesOfOneNameCreateOnce(IObjectStore store)
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(1, 8).Select(index => Task.Run(() => CreateAsync(store, Drain, Bytes(32, (byte)index)), Token)));
        var created = Assert.Single(outcomes.Select((outcome, index) => (outcome, index)), static pair => pair.outcome.Value is ObjectCreateOutcome.Created);
        Assert.Equal(Bytes(32, (byte)(created.index + 1)), await ReadAllAsync(store, Drain));
    }

    internal static async Task ConcurrentReplacesOfOneVersionApplyOnce(IObjectStore store)
    {
        var first = Assert.IsType<ObjectReplaceOutcome.Applied>((await ReplaceAsync(store, ObjectName.Manifest, null, Bytes(32, 21))).Value);
        var outcomes = await Task.WhenAll(Enumerable.Range(1, 8).Select(index =>
            Task.Run(() => ReplaceAsync(store, ObjectName.Manifest, first.Version, Bytes(32, (byte)(30 + index))), Token)));
        var applied = Assert.Single(outcomes.Select((outcome, index) => (outcome, index)), static pair => pair.outcome.Value is ObjectReplaceOutcome.Applied);
        await AssertHoldsAsync(store, ObjectName.Manifest, Bytes(32, (byte)(31 + applied.index)), ((ObjectReplaceOutcome.Applied)applied.outcome.Value).Version);
    }

    internal static async Task InvalidArgumentsAreRefused(IObjectStore store)
    {
        using (var one = ArcBuffer.Wrap(new byte[1]))
        using (var empty = one.Slice(0, 0))
        {
            _ = await Assert.ThrowsAsync<ArgumentException>(async () => await store.ReplaceAsync(ObjectName.Manifest, null, empty, null, Token));
        }

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ReadAsync(Drain, -1, 1, Token));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ReadAsync(Drain, 0, 0, Token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await store.ListAsync("/d", cancellationToken: Token).ToListAsync(Token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await store.ListAsync("D/", cancellationToken: Token).ToListAsync(Token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await store.ListAsync("d/", "/d", Token).ToListAsync(Token));
    }

    /// <summary>
    /// <c>ledger/abc/def</c>'s file could take the NTFS short name of <c>ledger/1</c>'s, were each <c>/</c> written as the
    /// <c>~</c> of a short name: each object stays its own (archive §9).
    /// </summary>
    internal static async Task ANameWhoseFileCouldShortenToAnotherNamesIsDistinct(IObjectStore store)
    {
        ObjectName[] names = [new("ledger/abc/def"), new("ledger/1"), new("ledger/2")];
        for (var index = 0; index < names.Length; index++)
        {
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await CreateAsync(store, names[index], Bytes(16, (byte)(60 + index))));
        }

        for (var index = 0; index < names.Length; index++)
        {
            Assert.Equal(Bytes(16, (byte)(60 + index)), await ReadAllAsync(store, names[index]));
        }

        Assert.Equal(names.Order(), await NamesAsync(store, "ledger"));
    }

    /// <summary>
    /// Names which Windows reserves for devices are ordinary objects.
    /// </summary>
    internal static async Task WindowsDeviceNamesAreObjectNames(IObjectStore store)
    {
        ObjectName[] names = [new("aux"), new("com1"), new("con"), new("lpt1"), new("nul"), new("prn")];
        for (var index = 0; index < names.Length; index++)
        {
            Assert.Equal<ObjectCreateOutcome>(ObjectCreateOutcome.Created.Instance, await CreateAsync(store, names[index], Bytes(8, (byte)(70 + index))));
            var replaced = Assert.IsType<ObjectReplaceOutcome.Applied>((await ReplaceAsync(store, names[index], (await ReadVersionedAsync(store, names[index])).Version, Bytes(9, (byte)(80 + index)))).Value);
            await AssertHoldsAsync(store, names[index], Bytes(9, (byte)(80 + index)), replaced.Version);
        }

        Assert.Equal(names, await NamesAsync(store, string.Empty));
        foreach (var name in names)
        {
            await store.DeleteAsync(name, Token);
            Assert.Null(await store.ReadVersionedAsync(name, Token));
        }
    }

    /// <summary>
    /// Returns <paramref name="length"/> bytes which differ from position to position and from seed to seed.
    /// </summary>
    internal static byte[] Bytes(int length, byte seed) => [.. Enumerable.Range(0, length).Select(index => unchecked((byte)(index * 7 + seed * 13)))];

    internal static async Task<ObjectCreateOutcome> CreateAsync(IObjectStore store, ObjectName name, byte[] content, uint? crc32c = null)
    {
        using var buffer = ArcBuffer.Wrap(content);
        return await store.CreateAsync(name, buffer, crc32c, Token);
    }

    internal static async Task<ObjectReplaceOutcome> ReplaceAsync(IObjectStore store, ObjectName name, ObjectVersion? expected, byte[] content, uint? crc32c = null)
    {
        using var buffer = ArcBuffer.Wrap(content);
        return await store.ReplaceAsync(name, expected, buffer, crc32c, Token);
    }

    /// <summary>
    /// Requires <paramref name="store"/> to hold <paramref name="content"/> at <paramref name="version"/> under
    /// <paramref name="name"/>.
    /// </summary>
    internal static async Task AssertHoldsAsync(IObjectStore store, ObjectName name, byte[] content, ObjectVersion version)
    {
        var (held, heldVersion) = await ReadVersionedAsync(store, name);
        Assert.Equal(content, held);
        Assert.Equal(version, heldVersion);
    }

    internal static async Task<byte[]> ReadAllAsync(IObjectStore store, ObjectName name)
    {
        var (content, _) = await ReadVersionedAsync(store, name);
        return content;
    }

    internal static async Task<(byte[] Content, ObjectVersion Version)> ReadVersionedAsync(IObjectStore store, ObjectName name)
    {
        var read = Assert.IsType<VersionedObject>(await store.ReadVersionedAsync(name, Token));
        using var content = read.Content;
        return (content.ToArray(), read.Version);
    }
}
