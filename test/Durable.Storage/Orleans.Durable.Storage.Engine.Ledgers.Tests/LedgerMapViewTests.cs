using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.LedgerMapSamples;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// <see cref="LedgerMapView"/> over a scripted register (ledgers.md §2.3): it opens after its first read, its current
/// value changes only on reads and only to newer epochs, callers during a read share the next read, a refresh reads only
/// while the view is older, one caller's cancellation never cancels a shared read, and an undecodable value fails the
/// read with its rejection and leaves the current value as it was. The register's entries are rented under the test's
/// page accounting, so the leak check sees any entry a read fails to dispose.
/// </summary>
public sealed class LedgerMapViewTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task OpenReturnsOnceItsFirstReadHasReturned()
    {
        var register = new ScriptedConditionalRegister { ReadGate = new() };
        register.Put(Encoded(Map(3)));
        var open = LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken);
        Assert.False(open.IsCompleted);
        register.ReadGate.SetResult();
        var view = await open;
        Assert.Equal(3UL, view.Current.Epoch);
    }

    [Fact]
    public async Task AnAbsentRegisterReadsAsTheEmptyMap()
    {
        var view = await LedgerMapView.OpenAsync(new ScriptedConditionalRegister(), TestContext.Current.CancellationToken);
        Assert.Same(LedgerMapValue.Empty, view.Current);
        Assert.Null(view.Find([1]));
    }

    [Fact]
    public async Task CurrentChangesOnlyOnReadsAndOnlyToNewerEpochs()
    {
        var register = new ScriptedConditionalRegister();
        register.Put(Encoded(Map(2)));
        var view = await LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken);
        register.Put(Encoded(Map(5)));
        Assert.Equal(2UL, view.Current.Epoch);
        Assert.Equal(5UL, (await view.ReadAsync(TestContext.Current.CancellationToken)).Epoch);
        Assert.Equal(5UL, view.Current.Epoch);

        // A register is never older than a read of it; the view keeps the newest value it read regardless.
        register.Put(Encoded(Map(4)));
        Assert.Equal(4UL, (await view.ReadAsync(TestContext.Current.CancellationToken)).Epoch);
        Assert.Equal(5UL, view.Current.Epoch);
    }

    /// <summary>
    /// A read in flight began before the calls made during it, so those calls share the next read rather than it: two
    /// reads serve three callers.
    /// </summary>
    [Fact]
    public async Task CallsDuringAReadShareTheNextRead()
    {
        var register = new ScriptedConditionalRegister();
        var view = await LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken);
        var reads = register.Reads;
        register.ReadGate = new();
        var first = view.ReadAsync(TestContext.Current.CancellationToken);
        var second = view.ReadAsync(TestContext.Current.CancellationToken);
        var third = view.ReadAsync(TestContext.Current.CancellationToken);
        register.Put(Encoded(Map(1)));
        register.ReadGate.SetResult();
        _ = await Task.WhenAll(first, second, third);
        Assert.Equal(reads + 2, register.Reads);
        Assert.Same(await second, await third);
        Assert.Equal(1UL, view.Current.Epoch);
    }

    [Fact]
    public async Task ARefreshReadsOnlyWhileTheViewIsOlder()
    {
        var register = new ScriptedConditionalRegister();
        register.Put(Encoded(Map(1)));
        var view = await LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken);
        var reads = register.Reads;
        Assert.Equal(1UL, (await view.RefreshAsync(1, TestContext.Current.CancellationToken)).Epoch);
        Assert.Equal(reads, register.Reads);

        register.Put(Encoded(Map(2)));
        Assert.Equal(2UL, (await view.RefreshAsync(2, TestContext.Current.CancellationToken)).Epoch);
        Assert.Equal(reads + 1, register.Reads);
    }

    /// <summary>An epoch a read begun after the call does not reach names no value the register held: a caller defect.</summary>
    [Fact]
    public async Task ARefreshToAnEpochTheRegisterNeverHeldThrows()
    {
        var register = new ScriptedConditionalRegister();
        register.Put(Encoded(Map(1)));
        var view = await LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => view.RefreshAsync(2, TestContext.Current.CancellationToken));
        Assert.Equal(1UL, view.Current.Epoch);
    }

    [Fact]
    public async Task AWaitForChangeCompletesOnceAReadRaisesCurrentAndReadsNothing()
    {
        var register = new ScriptedConditionalRegister();
        register.Put(Encoded(Map(1)));
        var view = await LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken);
        Assert.Same(view.Current, await view.WaitForChangeAsync(0, TestContext.Current.CancellationToken));
        var reads = register.Reads;
        var change = view.WaitForChangeAsync(1, TestContext.Current.CancellationToken);
        Assert.False(change.IsCompleted);
        Assert.Equal(reads, register.Reads);

        register.Put(Encoded(Map(2)));
        _ = await view.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2UL, (await change).Epoch);
    }

    /// <summary>A caller which stops waiting ends only its own wait: the read it shared completes for the others.</summary>
    [Fact]
    public async Task OneCallersCancellationDoesNotCancelTheSharedRead()
    {
        var register = new ScriptedConditionalRegister();
        var view = await LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken);
        register.ReadGate = new();
        using var cancellation = new CancellationTokenSource();
        var cancelled = view.ReadAsync(cancellation.Token);
        var other = view.ReadAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        register.Put(Encoded(Map(1)));
        register.ReadGate.SetResult();
        Assert.Equal(1UL, (await other).Epoch);
        Assert.Equal(1UL, view.Current.Epoch);
    }

    /// <summary>
    /// A value which is not a ledger map is the map's corruption: the read throws the rejection naming its reason, and
    /// the view keeps the last value it decoded.
    /// </summary>
    [Fact]
    public async Task AnUndecodableValueFailsTheReadAndLeavesCurrent()
    {
        var register = new ScriptedConditionalRegister();
        register.Put(Encoded(Map(1)));
        var view = await LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken);
        var bytes = Encoded(Map(2));
        bytes[^1] ^= 1;
        register.Put(bytes);
        var rejection = await Assert.ThrowsAsync<InvalidDataException>(() => view.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(LedgerMapValue.ReasonCrcMismatch, DecodeRejection.ReasonOf(rejection));
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => view.RefreshAsync(2, TestContext.Current.CancellationToken));
        Assert.Equal(1UL, view.Current.Epoch);
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedReadFailsItsCallersAndTheNextReadSucceeds()
    {
        var register = new ScriptedConditionalRegister();
        var view = await LedgerMapView.OpenAsync(register, TestContext.Current.CancellationToken);
        register.ReadFailure = new IOException("The scripted read failed.");
        _ = await Assert.ThrowsAsync<IOException>(() => view.ReadAsync(TestContext.Current.CancellationToken));
        register.ReadFailure = null;
        register.Put(Encoded(Map(1)));
        Assert.Equal(1UL, (await view.ReadAsync(TestContext.Current.CancellationToken)).Epoch);
    }

    /// <summary>Returns a map at <paramref name="epoch"/> with no ledgers, whose floor tells the values apart.</summary>
    private static LedgerMapValue Map(ulong epoch) => new(epoch, new LedgerId(1), epoch, []);
}
