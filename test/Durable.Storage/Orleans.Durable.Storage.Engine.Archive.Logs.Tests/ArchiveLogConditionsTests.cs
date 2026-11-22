using System.Collections.Immutable;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// The classifier of a failed condition (archive-logs.md §8.3): each write kind's table, written here as the spec's rows of
/// (predicate, verdict), apart from the code, and checked over every point of small domains, each run of 1 to 4 units
/// and each read absent or at each generation, length, unit count, and opener around the write's, that exactly one row
/// matches, so the table partitions its domain, and that the classifier answers that row's verdict; <c>Holds</c> by its
/// definition over generated runs, which pins <c>Holds((1, 0, 0), first run, 1) = false</c> and the count against
/// mutants; and the verdicts on the states a store reaches through the writer, a key's chain landed through each write
/// and partly through the next.
/// </summary>
public sealed class ArchiveLogConditionsTests : IDisposable
{
    private static readonly LedgerSegmentId Segment = new(new LedgerId(7), Slot.Zero);

    private static readonly ArchiveLogOpener Replace = new(ArchiveLogOpenerKind.Replace, null, 1);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>An expected verdict: a case of the outcome, or a contradiction.</summary>
    private enum Verdict
    {
        Held,
        Resume,
        Retry,
        PredecessorMissing,
        Corrupt,
    }

    public static TheoryData<ulong, ulong, ulong> Starts => new() { { 0, 0, 0 }, { 2, 4, 2 }, { 1, 0, 0 } };

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// The append run's table over every run of 1 to 4 units of 1 or 2 bytes each from <paramref name="generation"/>,
    /// <paramref name="length"/>, and <paramref name="units"/>, and every read around it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Starts))]
    public void TheAppendRunTablePartitionsItsDomainAndDecidesEachPoint(ulong generation, ulong length, ulong units)
    {
        var from = new ArchiveLogPosition(generation, 0, length, units);
        var start = from.IsOrigin ? new ArchiveLogPosition(1, 0, 0, 0) : from;
        foreach (var boundaries in Packings(start.Length, 4))
        {
            foreach (var read in Reads(start, boundaries[^1]))
            {
                var rows = AppendRows(from, start, boundaries, read).Where(static row => row.Matches).ToList();
                var point = $"the run from {from} with boundaries [{string.Join(", ", boundaries)}] and {Describe(read)}";
                Assert.True(rows.Count == 1, $"{rows.Count} rows of the append table match {point}: {string.Join("; ", rows.Select(static row => row.Name))}.");
                var (verdict, unit) = ActualAppend(from, boundaries, read);
                Assert.True(
                    (rows[0].Verdict, rows[0].Unit) == (verdict, unit),
                    $"The classifier answers {verdict}({unit}) on {point}, which the row '{rows[0].Name}' decides as {rows[0].Verdict}({rows[0].Unit}).");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Starts))]
    public void TheOpenerTablePartitionsItsDomainAndDecidesEachPoint(ulong generation, ulong length, ulong units)
    {
        var from = new ArchiveLogPosition(generation, 0, length, units);
        foreach (var read in Reads(from.IsOrigin ? new ArchiveLogPosition(1, 0, 0, 0) : from, from.Length + 4))
        {
            var rows = OpenerRows(from, read).Where(static row => row.Matches).ToList();
            var point = $"the opener from {from} and {Describe(read)}";
            Assert.True(rows.Count == 1, $"{rows.Count} rows of the opener table match {point}: {string.Join("; ", rows.Select(static row => row.Name))}.");
            var verdict = ActualOpener(from, read);
            Assert.True(rows[0].Verdict == verdict, $"The classifier answers {verdict} on {point}, which the row '{rows[0].Name}' decides as {rows[0].Verdict}.");
        }
    }

    /// <summary>
    /// <c>Holds</c> by its definition over generated runs: a log at the run's unit boundary <c>(g, q_u, b₀ + u)</c> holds
    /// its first <c>i</c> commands exactly when <c>start + Σℓ₁..ᵢ ≤ q_u</c>, a later generation holds them all, and an
    /// earlier one none; the mutant which ignores the count fails it.
    /// </summary>
    [Fact]
    public void HoldsIsItsDefinitionAtEveryUnitBoundaryOfGeneratedRunsUnlikeTheMutant()
    {
        var killed = false;
        Gen.Select(Gen.Int[1, 6], Gen.Int[1, 5].Array[1, 8], Gen.Int[0, 2], Gen.Int[0, 5]).Sample(@case =>
        {
            var (blockBytes, lengths, generation, length) = @case;
            var layout = new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, Math.Max(blockBytes, lengths.Max()), 100, 50, MemoryArchiveLogStore.NameRule);
            var from = generation == 0 ? ArchiveLogPosition.Origin : new ArchiveLogPosition((ulong)generation, 0, (ulong)length, (ulong)(length + 1) / 2);
            var start = ArchiveLogConditions.RunStart(from);
            var write = Write(from, [.. lengths.Select(ArchiveCommand.Append)]);
            var boundaries = ArchiveLogUnitPacker.Boundaries(layout, start.Length, write.Commands.AsSpan());
            for (var unit = 0; unit < boundaries.Length; unit++)
            {
                var at = start with { Length = boundaries[unit], Units = start.Units + (ulong)unit };
                var reach = start.Length;
                for (var count = 0; count <= lengths.Length; count++)
                {
                    reach += count == 0 ? 0 : (ulong)lengths[count - 1];
                    Assert.Equal(reach <= boundaries[unit], ArchiveLogConditions.Holds(at, write, count));
                    Assert.True(ArchiveLogConditions.Holds(at with { Generation = start.Generation + 1, Length = 0, Units = 0 }, write, count));
                    killed |= ArchiveLogConditions.Holds(at, write, count, ArchiveLogConditions.HoldsMutant.IgnoresCount) != reach <= boundaries[unit];
                }

                if (start.Generation > 1)
                {
                    Assert.False(ArchiveLogConditions.Holds(new ArchiveLogPosition(start.Generation - 1, 0, boundaries[^1], 9), write, 1));
                }
            }
        });
        Assert.True(killed, "Every generated run's boundaries agree with the mutant which ignores the count.");
    }

    /// <summary>
    /// A key's first append run, from <c>Origin</c>, is a run on generation 1 from length 0: a log at <c>(1, 0, 0)</c>,
    /// which its create alone made, holds none of it; a mutant which treats it as an opener holds it there.
    /// </summary>
    [Fact]
    public void AFirstRunIsNotHeldByAnEmptyFirstGenerationUnlikeTheMutant()
    {
        var first = Write(ArchiveLogPosition.Origin, ArchiveCommand.Append(3));
        var empty = new ArchiveLogPosition(1, 0, 0, 0);

        Assert.False(ArchiveLogConditions.Holds(empty, first, 1));
        Assert.True(ArchiveLogConditions.Holds(new ArchiveLogPosition(1, 0, 3, 1), first, 1));
        Assert.True(ArchiveLogConditions.Holds(empty, first, 1, ArchiveLogConditions.HoldsMutant.FirstRunAsOpener), "The mutant holds a first run at (1, 0, 0), which the vector refuses.");
        Assert.Equal(new ArchiveLogAppendConditionOutcome.Resume(0), ArchiveLogConditions.ClassifyAppend(ArchiveLogPosition.Origin, [0, 3], new ArchiveLogProperties(empty, null)));
    }

    /// <summary>
    /// On the states a store reaches through the writer, a key's chain of writes landed through each one and partly
    /// through the next, every write before the cut is held, the cut one resumes where it stopped, and every later one finds
    /// its predecessor missing; <c>Holds</c> holds exactly the writes before the cut.
    /// </summary>
    [Fact]
    public async Task OnReachableStatesEveryWriteIsClassifiedByWhereTheChainStopped()
    {
        var layout = new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 2, 100, 50, MemoryArchiveLogStore.NameRule);
        var chain = Chain(layout);
        for (var cut = 0; cut <= chain.Count; cut++)
        {
            var partials = cut == chain.Count ? 1 : chain[cut].IsOpener ? 2 : UnitsOf(layout, chain[cut]);
            for (var partial = 0; partial < partials; partial++)
            {
                await _leaks.InScopeAsync(() => CheckCutAsync(layout, chain, cut, partial));
            }
        }
    }

    private static async Task CheckCutAsync(ArchiveLogLayout layout, List<ArchiveWrite> chain, int cut, int partial)
    {
        var faults = new Queue<ArchiveLogFault>();
        var store = new MemoryArchiveLogStore(layout, _ => faults.TryDequeue(out var fault) ? fault : ArchiveLogFault.None);
        var name = new ArchiveLogName("k/reachable", 3);
        var writer = new ArchiveLogWriter(store, TimeProvider.System, new ArchiveLogWriterOptions { MaxRetries = 0 });
        foreach (var write in chain.Take(cut))
        {
            Assert.IsType<ArchiveLogConditionOutcome.Held>((await writer.WriteAsync(name, write, Content(write), CancellationToken.None)).Value);
        }

        if (cut < chain.Count && partial > 0)
        {
            // The cut write lands in part: an opener's base without its generation, or an append run's first units.
            var write = chain[cut];
            var landed = write.IsOpener ? 1 : partial + (write.Token.IsEmpty ? 1 : 0);
            for (var request = 0; request < landed; request++)
            {
                faults.Enqueue(ArchiveLogFault.None);
            }

            faults.Enqueue(ArchiveLogFault.FailBefore);
            await Assert.ThrowsAsync<IOException>(async () => await writer.WriteAsync(name, write, Content(write), CancellationToken.None));
        }

        var read = await store.GetPropertiesAsync(name, CancellationToken.None);
        for (var index = 0; index < chain.Count; index++)
        {
            var write = chain[index];
            var from = ArchiveLogTokens.PositionOf(write.Token);
            var (verdict, unit) = write.IsOpener
                ? (ActualOpener(from, read), 0)
                : ActualAppend(from, ArchiveLogUnitPacker.Boundaries(layout, ArchiveLogConditions.RunStart(from).Length, write.Commands.AsSpan()), read);
            // The cut write's own start, which a log behind the write reaches when its predecessor lands, has an opener
            // retried and a run resumed at unit 0; a first run's, before its create, an absent log no failed condition leaves.
            var expected = index < cut ? (Verdict.Held, 0)
                : index > cut ? (Verdict.PredecessorMissing, 0)
                : write.IsOpener ? (Verdict.Retry, 0)
                : from.IsOrigin && partial == 0 ? (Verdict.Corrupt, 0)
                : (Verdict.Resume, partial);
            Assert.True(expected == (verdict, unit), $"With the chain landed through write {cut} and {partial} of its parts, write {index} classifies as {verdict}({unit}), not {expected}.");
            Assert.Equal(index < cut, ArchiveLogConditions.Holds(read?.Position ?? ArchiveLogPosition.Origin, write, write.Commands.Length));
        }
    }

    /// <summary>A key's chain: a first run of three units, an opener with an append, a run of two units, and a tombstoned Delete.</summary>
    private static List<ArchiveWrite> Chain(ArchiveLogLayout layout)
    {
        ImmutableArray<ArchiveCommand>[] writes =
        [
            [ArchiveCommand.Append(1), ArchiveCommand.Append(2), ArchiveCommand.Append(1)],
            [ArchiveCommand.Replace(3), ArchiveCommand.Append(1)],
            [ArchiveCommand.Append(2), ArchiveCommand.Append(2)],
            [ArchiveCommand.Delete(new byte[8])],
        ];
        var chain = new List<ArchiveWrite>();
        RowToken token = default;
        foreach (var commands in writes)
        {
            var write = new ArchiveWrite(Segment, token, commands);
            chain.Add(write);
            var plan = new ArchiveLogPlan(layout, ArchiveLogTokens.PositionOf(token));
            foreach (var command in commands)
            {
                plan.Add(command);
            }

            token = plan.End();
        }

        return chain;
    }

    private static int UnitsOf(ArchiveLogLayout layout, ArchiveWrite write) =>
        ArchiveLogUnitPacker.Boundaries(layout, 0, write.Commands.AsSpan()).Length - 1;

    private static System.Buffers.ReadOnlySequence<byte> Content(ArchiveWrite write) =>
        new(new byte[write.Commands.Sum(static command => command.ContentLength)]);

    private static ArchiveWrite Write(ArchiveLogPosition from, params ArchiveCommand[] commands) =>
        new(Segment, ArchiveLogTokens.Encode(from), [.. commands]);

    /// <summary>Every run of 1 to <paramref name="maxUnits"/> units of 1 or 2 bytes each, from <paramref name="start"/>, as its boundaries.</summary>
    private static IEnumerable<ulong[]> Packings(ulong start, int maxUnits)
    {
        for (var units = 1; units <= maxUnits; units++)
        {
            for (var sizes = 0; sizes < 1 << units; sizes++)
            {
                var boundaries = new ulong[units + 1];
                boundaries[0] = start;
                for (var unit = 0; unit < units; unit++)
                {
                    boundaries[unit + 1] = boundaries[unit] + 1 + (ulong)((sizes >> unit) & 1);
                }

                yield return boundaries;
            }
        }
    }

    /// <summary>
    /// Every read of the domain: an absent log, and each generation, length up to past <paramref name="end"/>, unit count,
    /// and opener, none or a <c>Replace</c>, around <paramref name="start"/>.
    /// </summary>
    private static IEnumerable<ArchiveLogProperties?> Reads(ArchiveLogPosition start, ulong end)
    {
        yield return null;
        for (var generation = start.Generation == 1 ? 1ul : start.Generation - 1; generation <= start.Generation + 1; generation++)
        {
            for (var length = 0ul; length <= end + 2; length++)
            {
                for (var units = 0ul; units <= start.Units + end - start.Length + 2; units++)
                {
                    var position = new ArchiveLogPosition(generation, 0, length, units);
                    yield return new ArchiveLogProperties(position, null);
                    yield return new ArchiveLogProperties(position, Replace);
                }
            }
        }
    }

    /// <summary>
    /// The append run's table, archive-logs.md §8.3, as its rows: the run from <paramref name="from"/> starts at
    /// <paramref name="start"/> <c>(g, q₀, b₀)</c> and packs <c>m</c> units ending at <c>q_m</c>.
    /// </summary>
    private static IEnumerable<Row> AppendRows(ArchiveLogPosition from, ArchiveLogPosition start, ulong[] boundaries, ArchiveLogProperties? read)
    {
        var absent = read is null;
        var (gL, pL, bL) = read is { Position: var p } ? (p.Generation, p.Length, p.Units) : (0ul, 0ul, 0ul);
        var (g, b0, m, end) = (start.Generation, start.Units, (ulong)(boundaries.Length - 1), boundaries[^1]);
        var firstRunOnOpened = !absent && from.IsOrigin && gL == 1 && read!.Opener is not null;
        var onRun = !absent && !firstRunOnOpened && gL == g;
        yield return new("absent after a first run", absent && from.IsOrigin, Verdict.Corrupt);
        yield return new("absent", absent && !from.IsOrigin, Verdict.PredecessorMissing);
        yield return new("a first run on an opener's generation 1", firstRunOnOpened, Verdict.Corrupt);
        yield return new("a later generation", !absent && !firstRunOnOpened && gL > g, Verdict.Held);
        yield return new("an earlier generation", !absent && !firstRunOnOpened && gL < g, Verdict.PredecessorMissing);
        yield return new("short of the start", onRun && pL < boundaries[0], Verdict.PredecessorMissing);
        yield return new("at the end, its units packed", onRun && pL == end && bL == b0 + m, Verdict.Held);
        yield return new("at the end, other units", onRun && pL == end && bL != b0 + m, Verdict.Corrupt);
        yield return new("past the end, appends after it", onRun && pL > end && bL > b0 + m, Verdict.Held);
        yield return new("past the end, no unit after it", onRun && pL > end && bL <= b0 + m, Verdict.Corrupt);
        for (var unit = 0; unit < boundaries.Length - 1; unit++)
        {
            var at = onRun && pL == boundaries[unit];
            yield return new($"at unit {unit}'s start", at && bL == b0 + (ulong)unit, Verdict.Resume, unit);
            yield return new($"at unit {unit}'s start, other units", at && bL != b0 + (ulong)unit, Verdict.Corrupt);
        }

        yield return new("inside a unit", onRun && pL > boundaries[0] && pL < end && Array.IndexOf(boundaries, pL) < 0, Verdict.Corrupt);
    }

    /// <summary>The opener's table, archive-logs.md §8.3, as its rows: the opener from <paramref name="from"/> opens <c>g + 1</c>.</summary>
    private static IEnumerable<Row> OpenerRows(ArchiveLogPosition from, ArchiveLogProperties? read)
    {
        var absent = read is null;
        var log = read?.Position ?? default;
        yield return new("absent after an opener from Origin", absent && from.Generation == 0, Verdict.Corrupt);
        yield return new("absent", absent && from.Generation > 0, Verdict.PredecessorMissing);
        yield return new("the opened generation or later", !absent && log.Generation > from.Generation, Verdict.Held);
        yield return new("an earlier generation", !absent && log.Generation < from.Generation, Verdict.PredecessorMissing);
        yield return new("short of the start", !absent && log.Generation == from.Generation && log.Length < from.Length, Verdict.PredecessorMissing);
        yield return new("at the start", !absent && log == from, Verdict.Retry);
        yield return new("past the start, or at it with other units", !absent && log.Generation == from.Generation && log.Length >= from.Length && log != from, Verdict.Corrupt);
    }

    private static (Verdict Verdict, int Unit) ActualAppend(ArchiveLogPosition from, ulong[] boundaries, ArchiveLogProperties? read)
    {
        try
        {
            return ArchiveLogConditions.ClassifyAppend(from, boundaries, read).Value switch
            {
                ArchiveLogAppendConditionOutcome.Held => (Verdict.Held, 0),
                ArchiveLogAppendConditionOutcome.Resume resume => (Verdict.Resume, resume.Unit),
                ArchiveLogAppendConditionOutcome.PredecessorMissing => (Verdict.PredecessorMissing, 0),
                _ => throw new InvalidOperationException("The classifier answered nothing."),
            };
        }
        catch (ArchiveCorruptException)
        {
            return (Verdict.Corrupt, 0);
        }
    }

    private static Verdict ActualOpener(ArchiveLogPosition from, ArchiveLogProperties? read)
    {
        try
        {
            return ArchiveLogConditions.ClassifyOpener(from, read).Value switch
            {
                ArchiveLogOpenerConditionOutcome.Held => Verdict.Held,
                ArchiveLogOpenerConditionOutcome.Retry => Verdict.Retry,
                ArchiveLogOpenerConditionOutcome.PredecessorMissing => Verdict.PredecessorMissing,
                _ => throw new InvalidOperationException("The classifier answered nothing."),
            };
        }
        catch (ArchiveCorruptException)
        {
            return Verdict.Corrupt;
        }
    }

    private static string Describe(ArchiveLogProperties? read) => read is null ? "an absent log" : $"a log at {read.Position} opened by {read.Opener?.ToString() ?? "none"}";

    /// <summary>A row of a table: its name, its predicate on a point, and its verdict.</summary>
    private sealed record Row(string Name, bool Matches, Verdict Verdict, int Unit = 0);
}
