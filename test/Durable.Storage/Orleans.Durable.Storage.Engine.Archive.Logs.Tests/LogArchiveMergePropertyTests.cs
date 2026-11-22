using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// The merge property over archive logs (archive.md §10), whose prefix holds every write which landed, by its position,
/// so it holds any first segments; each key's write of a segment lands through <see cref="ArchiveLogWriter"/>. Its cut
/// points crash a drain after any request, leaving a key's first run, opener, or run landed through some units, a
/// generation 1 created with none, or an opener's base alone, which <c>Holds</c> decides at the prefix's unit boundary.
/// </summary>
[NoPageLeakCheck("The base class holds the leak check, and runs every case in its scope.")]
public sealed class LogArchiveMergePropertyTests : ArchiveMergeProperty
{
    protected override long Iterations => 60;

    private ArchiveLogConditions.HoldsMutant _mutant;

    [Fact]
    public Task TheMergeFoldsToTheDecidedRowsAtEveryCutPoint() => SampleCutPointsAsync();

    /// <summary>
    /// A <c>Holds</c> which judges a run's first commands by the whole run counts a run landed through some units as held
    /// whole, so the merge would drop its unlanded rows: the merge's own chain check refuses the result as corrupt, or the
    /// fold's comparison catches it, at the cut points.
    /// </summary>
    [Fact]
    public async Task AHoldsWhichIgnoresTheCountFailsAtTheCutPoints()
    {
        _mutant = ArchiveLogConditions.HoldsMutant.IgnoresCount;

        var failure = await Assert.ThrowsAsync<CsCheck.CsCheckException>(() => SampleCutPointsAsync(iterations: 1000));

        var report = failure.ToString();
        Assert.True(report.Contains(FoldFailure, StringComparison.Ordinal) || report.Contains(nameof(ArchiveCorruptException), StringComparison.Ordinal), report);
    }

    protected override Task<IMergeStore> CreateAsync() => Task.FromResult<IMergeStore>(new LogMergeStore(_mutant));

    private sealed class LogMergeStore : IMergeStore
    {
        private readonly MemoryArchiveLogStore _logs;
        private readonly ArchiveLogWriter _writer;
        private int? _budget;

        public LogMergeStore(ArchiveLogConditions.HoldsMutant mutant)
        {
            _logs = new(
                new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 3, 64, 48, MemoryArchiveLogStore.NameRule),
                _ => _budget is not { } budget ? ArchiveLogFault.None : (_budget = budget - 1) >= 0 ? ArchiveLogFault.None : ArchiveLogFault.FailBefore);
            Store = new LogArchiveStore(_logs, new InMemoryObjectStore(), ArchiveLogNames.Hexadecimal) { HoldsMutant = mutant };
            _writer = new ArchiveLogWriter(_logs, new FakeTimeProvider(), new ArchiveLogWriterOptions { MaxRetries = 0 });
        }

        public IArchiveStore Store { get; }

        public bool HoldsWholeLedgers => false;

        public async Task DrainAsync(LedgerId ledger, IReadOnlyList<KeyedRow[]> segments, Slot end)
        {
            foreach (var segment in segments)
            {
                var verdicts = await ArchiveLogDrains.DrainAsync(_writer, ArchiveLogNames.Hexadecimal, segment, CancellationToken.None);
                Assert.All(verdicts.Values, static verdict => Assert.IsType<ArchiveLogConditionOutcome.Held>(verdict.Value));
            }
        }

        public async Task DrainInPartAsync(LedgerId ledger, KeyedRow[] segment, int requests)
        {
            _budget = requests;
            try
            {
                _ = await ArchiveLogDrains.DrainAsync(_writer, ArchiveLogNames.Hexadecimal, segment, CancellationToken.None);
            }
            catch (IOException)
            {
                // The drain crashed at its cut point: the key whose request failed holds its write in part, and later keys none of theirs.
            }
            finally
            {
                _budget = null;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
