using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// Drops (archive §2.6, §8.4): a closed ledger leaves the map only once a manifest value the dropper read or applied
/// shows it complete (LSM-I3), oldest first along each key's lineage (LG-I10); a drop whose outcome is unknown is
/// submitted again, and one the committer rejects never is.
/// </summary>
public sealed class LedgerDropperTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Of two closed ledgers in one lineage, the newer one complete first waits for the older one; once both are complete,
    /// both drop, oldest first, and an open ledger never does.
    /// </summary>
    [Fact]
    public async Task CompleteLedgersDropOldestFirstAlongTheirLineage()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var first = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(first, [LiveRow.Of("a", RowKind.Append, "a0")], Token);
        await world.Ledgers.ReplaceAsync(first, Token);
        var second = world.Ledgers.OpenContaining([0]).Id;
        _ = await world.Ledgers.ProposeAsync(second, [LiveRow.Of("a", RowKind.Append, "a1")], Token);
        await world.Ledgers.ReplaceAsync(second, Token);
        var open = world.Ledgers.OpenContaining([0]).Id;
        var dropper = new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance);

        Assert.Empty(await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
        _ = await world.CompleteAsync(world.Worker(), second, Token);
        Assert.Empty(await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
        Assert.NotNull(world.Ledgers.Map.Current.Get(second));

        _ = await world.CompleteAsync(world.Worker(), first, Token);
        Assert.Equal([first, second], await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
        Assert.Equal([open], world.Ledgers.Map.Current.Ledgers.Select(static ledger => ledger.Id));
    }

    /// <summary>
    /// Two droppers racing over one complete ledger both submit its drop, which applies once; the other's finds the drop
    /// applied, which answers it as applied too, and neither meets a defect.
    /// </summary>
    [Fact]
    public async Task RacingDroppersDropALedgerOnce()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.Ledgers.ReplaceAsync(ledger, Token);
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);
        var droppers = new[]
        {
            new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance),
            new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance),
        };
        var (test, since) = (TestContext.Current.Test, DebugAssertions.RaisedCount);

        var dropped = await Task.WhenAll(droppers.Select(dropper => dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token)));

        Assert.Equal([ledger], dropped.SelectMany(static ids => ids).Distinct());
        Assert.Null(world.Ledgers.Map.Current.Get(ledger));
        Assert.Null(DebugAssertions.FirstRaisedBy(test, since));
    }

    /// <summary>
    /// A dropper whose manifest view holds no value read or applied has no evidence, and drops nothing.
    /// </summary>
    [Fact]
    public async Task ADropperWithoutAManifestValueDropsNothing()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.Ledgers.ReplaceAsync(ledger, Token);
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);

        var dropper = new LedgerDropper(world.Ledgers.Map, new ManifestView(world.Store), NullLogger<LedgerDropper>.Instance);

        Assert.Empty(await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
        Assert.NotNull(world.Ledgers.Map.Current.Get(ledger));
    }

    /// <summary>
    /// A drop whose commit throws leaves its outcome unknown: the pass still decides the other drops and refreshes the map
    /// view, then throws, and a later call submits the drop again.
    /// </summary>
    [Fact]
    public async Task AFailedDropIsSubmittedAgainAfterThePassDecidesTheOthers()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var root = world.Ledgers.OpenContaining([0]).Id;
        await world.Ledgers.SplitAsync(root, Token);
        _ = await world.CompleteAsync(world.Worker(), root, Token);
        var (lower, upper) = (world.Ledgers.OpenContaining([0x00]).Id, world.Ledgers.OpenContaining([0x80]).Id);
        await world.Ledgers.ReplaceAsync(lower, Token);
        await world.Ledgers.ReplaceAsync(upper, Token);
        var dropper = new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance);
        Assert.Equal([root], await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
        _ = await world.CompleteAsync(world.Worker(), lower, Token);
        _ = await world.CompleteAsync(world.Worker(), upper, Token);

        _ = await Assert.ThrowsAsync<IOException>(() => dropper.DropCompleteAsync(
            (change, token) => ((LedgerMapChange.Drop)change.Value).Ledger == lower
                ? Task.FromException<LedgerMapCommitOutcome>(new IOException("The drop's outcome is unknown."))
                : world.Ledgers.CommitAsync(change, token),
            Token));
        Assert.Null(world.Ledgers.Map.Current.Get(upper));
        Assert.NotNull(world.Ledgers.Map.Current.Get(lower));

        Assert.Equal([lower], await dropper.DropCompleteAsync(world.Ledgers.CommitAsync, Token));
    }

    /// <summary>
    /// A drop the committer rejects is a defect: it raises a debug assertion in a Debug build, is never submitted again,
    /// and drops nothing; a superseded or refused one, which no derived drop earns, raises one too.
    /// </summary>
    [Theory]
    [InlineData("rejected")]
    [InlineData("superseded")]
    public async Task AnUnearnedAnswerIsADefectAndARejectedDropIsNotSubmittedAgain(string answer)
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.Ledgers.ReplaceAsync(ledger, Token);
        _ = await world.CompleteAsync(world.Worker(), ledger, Token);
        var dropper = new LedgerDropper(world.Ledgers.Map, world.Manifest, NullLogger<LedgerDropper>.Instance);
        var submissions = 0;
        LedgerMapCommitOutcome Answer() => answer == "rejected"
            ? new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.DropNotClosed)
            : new LedgerMapCommitOutcome.Superseded(1);

        var since = DebugAssertions.RaisedCount;
        var test = TestContext.Current.Test;
        try
        {
            Assert.Empty(await dropper.DropCompleteAsync((_, _) => { submissions++; return Task.FromResult(Answer()); }, Token));
        }
        catch (DebugAssertionException)
        {
        }

        if (TestDiagnostics.Configuration == "Debug")
        {
            _ = Assert.Single(DebugAssertions.TakeRaisedBy(test, since));
        }

        try
        {
            _ = await dropper.DropCompleteAsync((_, _) => { submissions++; return Task.FromResult(Answer()); }, Token);
        }
        catch (DebugAssertionException)
        {
        }

        _ = DebugAssertions.TakeRaisedBy(test, since);
        Assert.Equal(answer == "rejected" ? 1 : 2, submissions);
        Assert.NotNull(world.Ledgers.Map.Current.Get(ledger));
    }
}
