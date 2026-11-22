using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Tests;

/// <summary>
/// The version checker and the chain checker's rebase judgement (verification.md §3.2; JS-I10, HI-I4): a state may have
/// several names, but no version names two states, which an exact state identity tells apart even for two deletions; a
/// rebase takes the store's version base; a rebase in the chain follows only a dropped ledger's row, to the base the
/// chain replays; and once the LSM forgets a key after its <c>Expired</c>, a new lifetime from <c>Origin</c> may repeat
/// its versions and restart its chain.
/// </summary>
public sealed class VersionCheckerTests : IDisposable
{
    private static readonly byte[] Key = [1, 2, 3];
    private static readonly RowToken Base = new([9]);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>Two versions may name one state; a version which named one state may not name another.</summary>
    [Fact]
    public void AStateMayHaveTwoNamesButNoVersionNamesTwoStates()
    {
        var versions = new VersionChecker();

        Assert.Null(versions.Named(Key, new RowVersion(default, 1), "row s1:0"));
        Assert.Null(versions.Named(Key, new RowVersion(Base, 0), "row s1:0"));
        Assert.Null(versions.Named(Key, new RowVersion(Base, 0), "row s1:0"));

        Assert.Contains("names the state row s2:0, and earlier row s1:0", versions.Named(Key, new RowVersion(Base, 0), "row s2:0"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Two deletions leave states with equal content, absent and empty, which an identity by their rows tells apart, so a
    /// stale rebase naming both by one version is found.
    /// </summary>
    [Fact]
    public void TwoDeletionsAreDistinctStates()
    {
        var versions = new VersionChecker();

        Assert.Null(versions.Named(Key, new RowVersion(Base, 0), "row s1:3"));

        Assert.NotNull(versions.Named(Key, new RowVersion(Base, 0), "row s2:7"));
    }

    /// <summary>A rebase takes the store's version base, and a base names one state.</summary>
    [Fact]
    public void ARebaseTakesTheStoresVersionBase()
    {
        var versions = new VersionChecker();

        Assert.Null(versions.Rebased(Key, Base, Base, "row s1:0"));
        Assert.Contains("not the store's version base", versions.Rebased(Key, new RowToken([8]), Base, "row s1:0"), StringComparison.Ordinal);
        Assert.NotNull(versions.Rebased(Key, Base, Base, "row s2:0"));
    }

    /// <summary>
    /// A key's versions never repeat, until the LSM forgets the key and its new lifetime starts from <c>Origin</c>: then
    /// only its <c>Origin</c>-based versions may repeat.
    /// </summary>
    [Fact]
    public void OnlyOriginBasedVersionsRepeatOnceTheKeyIsForgotten()
    {
        var versions = new VersionChecker();
        Assert.Null(versions.Version(Key, new RowVersion(default, 1)));
        Assert.Null(versions.Version(Key, new RowVersion(Base, 1)));
        Assert.Contains("repeats", versions.Version(Key, new RowVersion(default, 1)), StringComparison.Ordinal);

        versions.Forget(Key);

        Assert.Null(versions.Version(Key, new RowVersion(default, 1)));
        Assert.Contains("repeats", versions.Version(Key, new RowVersion(Base, 1)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A defining row at the first count of another base follows a rebase only once the previous row's ledger has dropped,
    /// and only to the base the chain replays, which the memory store's version base is.
    /// </summary>
    [Fact]
    public void ARebaseInTheChainFollowsADroppedLedgerToTheReplayedBase()
    {
        using var store = new InMemoryArchiveStore();
        var first = Row(1, RowKind.Append, default, new RowVersion(default, 1));
        var replayed = store.Plan(TokenChainChecker.WriteOf([first]));
        var rebased = Row(2, RowKind.Append, replayed, new RowVersion(replayed, 1));
        var elsewhere = Row(2, RowKind.Append, replayed, new RowVersion(Base, 1));

        Assert.Null(TokenChainChecker.Check(store, default, [first, rebased], dropped: new HashSet<LedgerId> { new(1) }));
        Assert.Contains("does not follow", TokenChainChecker.Check(store, default, [first, rebased]), StringComparison.Ordinal);
        Assert.Contains("does not follow", TokenChainChecker.Check(store, default, [first, rebased], dropped: new HashSet<LedgerId>()), StringComparison.Ordinal);
        Assert.Contains("does not follow", TokenChainChecker.Check(store, default, [first, elsewhere], dropped: new HashSet<LedgerId> { new(1) }), StringComparison.Ordinal);
    }

    /// <summary>
    /// Once the LSM forgets a key after its <c>Expired</c>, the key's next write starts a new lifetime from <c>Origin</c>:
    /// its token and its version restart, which the chain checker allows only when told the key was forgotten.
    /// </summary>
    [Fact]
    public void AForgottenKeysNewLifetimeRestartsItsChain()
    {
        using var store = new InMemoryArchiveStore();
        var first = Row(1, RowKind.Append, default, new RowVersion(default, 1));
        var expired = Row(2, RowKind.Delete, store.Plan(TokenChainChecker.WriteOf([first])), new RowVersion(default, 2));
        var restarted = Row(3, RowKind.Append, default, new RowVersion(default, 1));

        Assert.Null(TokenChainChecker.Check(store, default, [first, expired, restarted], forgotten: true));
        Assert.Contains("HI-I4", TokenChainChecker.Check(store, default, [first, expired, restarted]), StringComparison.Ordinal);
    }

    /// <summary>A row alone in its ledger's segment from slot 0, with a one-byte body, or an <c>Expired</c>'s empty <c>Delete</c>.</summary>
    private static ChainRow Row(ulong ledger, RowKind kind, RowToken token, RowVersion version) =>
        new(new Tag(new LedgerId(ledger), Slot.Zero, 0), new LedgerSegmentId(new LedgerId(ledger), Slot.Zero), kind, kind == RowKind.Delete ? 0 : 1, ReadOnlyMemory<byte>.Empty, token, version, Held: false);
}
