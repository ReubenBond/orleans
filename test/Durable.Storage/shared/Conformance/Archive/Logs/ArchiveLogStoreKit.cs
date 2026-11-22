using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using CsCheck;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Archive.Logs;

namespace Orleans.Durable.Storage.Testing;

/// <summary>Runs <see cref="ArchiveLogStoreSpec"/> requests against an <see cref="IArchiveLogStore"/>, on its one log.</summary>
/// <remarks>
/// A call which throws propagates its exception: its caller decides whether the call's outcome is unknown. The executor
/// disposes every read it receives. A unit of two bytes or more crosses as two segments, so a store reads multi-segment
/// content.
/// </remarks>
internal sealed class ArchiveLogStoreExecutor(IArchiveLogStore store)
{
    /// <summary>The one log the spec's requests address.</summary>
    internal static readonly ArchiveLogName Log = new("k/spec", 0x5eed_0000_0000_0001);

    /// <summary>Runs <paramref name="request"/>.</summary>
    internal async Task<LogOutcome> RunAsync(object request, CancellationToken cancellationToken)
    {
        switch (request)
        {
            case LogAppend append:
                var generation = append.G == 0 ? 1 : append.G;
                var offset = append.G == 0 ? 0 : append.P;
                var run = new List<ReadOnlySequence<byte>>();
                foreach (var length in append.Units)
                {
                    var bytes = Enumerable.Range(offset, length).Select(at => ArchiveLogStoreSpec.UnitByte(generation, at)).ToArray();
                    run.Add(bytes.Length > 1 ? SequenceChain.Of(bytes[..1], bytes[1..]) : new ReadOnlySequence<byte>(bytes));
                    offset += length;
                }

                var from = append.G == 0 ? ArchiveLogPosition.Origin : new ArchiveLogPosition((ulong)append.G, 0, (ulong)append.P, (ulong)append.B);
                return Written(await store.AppendAsync(Log, from, run, cancellationToken));
            case LogOpen open:
                var opener = open.Opener switch
                {
                    SpecOpener.Replace => new ArchiveLogOpener(ArchiveLogOpenerKind.Replace, null, 1),
                    SpecOpener.Delete => new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, null, 1),
                    SpecOpener.DeleteWithTombstone => new ArchiveLogOpener(ArchiveLogOpenerKind.Delete, ArchiveLogStoreSpec.Tombstone, 1),
                    SpecOpener.None => throw new ArgumentException("An opener has a kind.", nameof(request)),
                    _ => throw new UnreachableException(),
                };
                var content = Enumerable.Range(0, open.BaseLength).Select(at => ArchiveLogStoreSpec.BaseByte(open.G + 1, at)).ToArray();
                var start = open.G == 0 ? ArchiveLogPosition.Origin : new ArchiveLogPosition((ulong)open.G, 0, (ulong)open.P, (ulong)open.B);
                return Written(await store.OpenAsync(Log, start, opener, new ReadOnlySequence<byte>(content), cancellationToken));
            case LogRead:
                return await store.ReadAsync(Log, cancellationToken) switch
                {
                    ArchiveLogRead.Absent => new LogOutcome(LogOutcomeKind.Absent),
                    ArchiveLogRead.Stale => new LogOutcome(LogOutcomeKind.Stale),
                    ArchiveLogRead.Read read => Read(read),
                    null => throw new InvalidOperationException("A read answered no outcome."),
                };
            case LogProperties:
                return await store.GetPropertiesAsync(Log, cancellationToken) is { } properties
                    ? new LogOutcome(LogOutcomeKind.Properties, ArchiveLogStoreSpec.PropertiesText(properties.Position.Generation, properties.Position.Length, properties.Position.Units, OpenerOf(properties.Opener)))
                    : new LogOutcome(LogOutcomeKind.Absent);
            case LogList:
                var listed = new List<ulong>();
                await foreach (var entry in store.ListGenerationsAsync(continuation: null, cancellationToken).SelectMany(static page => page.Generations))
                {
                    if (entry.Log != Log.Value || entry.KeyHash != Log.KeyHash)
                    {
                        throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"A listing named the log '{entry.Log}' with key hash {entry.KeyHash:x16}, which no request wrote."));
                    }

                    listed.Add(entry.Generation);
                }

                return new LogOutcome(LogOutcomeKind.Listed, string.Join(",", listed.Order()));
            case LogDeleteBase delete:
                await store.DeleteGenerationAsync(Log.Value, (ulong)delete.G, cancellationToken);
                return new LogOutcome(LogOutcomeKind.Deleted);
            default:
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{request} is not an archive-log store request."), nameof(request));
        }
    }

    private static LogOutcome Written(ArchiveLogWriteOutcome outcome) => outcome switch
    {
        ArchiveLogWriteOutcome.Applied => new LogOutcome(LogOutcomeKind.Applied),
        ArchiveLogWriteOutcome.ConditionFailed => new LogOutcome(LogOutcomeKind.ConditionFailed),
        null => throw new InvalidOperationException("A write answered no outcome."),
    };

    private static LogOutcome Read(ArchiveLogRead.Read read)
    {
        using (read)
        {
            return new LogOutcome(LogOutcomeKind.Read, ArchiveLogStoreSpec.ReadText(read.Position.Generation, read.Position.Length, read.Position.Units, read.HasBase, read.Tombstone, read.Content.ToArray()));
        }
    }

    private static SpecOpener OpenerOf(ArchiveLogOpener? opener) => opener switch
    {
        null => SpecOpener.None,
        { Kind: ArchiveLogOpenerKind.Replace } => SpecOpener.Replace,
        { Tombstone: null } => SpecOpener.Delete,
        { Tombstone: not null } => SpecOpener.DeleteWithTombstone,
    };
}

/// <summary>Registers <see cref="ArchiveLogStoreSpec"/>'s operations with a conformance kit.</summary>
internal static class ArchiveLogStoreKit
{
    /// <summary>
    /// The inputs Accordant's exhaustive generation draws from: a key's first run, runs and openers at the positions
    /// those reach, and a run from a position the log never reaches.
    /// </summary>
    internal static IReadOnlyList<object> Inputs { get; } =
    [
        new LogAppend(0, 0, 0, [1, 1]),
        new LogAppend(1, 2, 2, [1]),
        new LogAppend(1, 1, 1, [1]),
        new LogAppend(2, 0, 0, [2]),
        new LogOpen(0, 0, 0, SpecOpener.Replace, 1),
        new LogOpen(1, 2, 2, SpecOpener.DeleteWithTombstone, 0),
        new LogOpen(1, 0, 0, SpecOpener.Delete, 2),
        LogRead.Instance,
        LogProperties.Instance,
        new LogDeleteBase(1),
    ];

    /// <summary>
    /// The inputs of the straggler set, which exhaustive generation draws to depth 5: a key's first run, the opener after
    /// it, the run after that, and each again later, as a straggler or a retry re-issues it, with reads between.
    /// </summary>
    internal static IReadOnlyList<object> StragglerInputs { get; } =
    [
        new LogAppend(0, 0, 0, [1, 1]),
        new LogOpen(1, 2, 2, SpecOpener.Replace, 1),
        new LogAppend(2, 0, 0, [2]),
        LogProperties.Instance,
    ];

    /// <summary>Returns <paramref name="requests"/>, <see cref="Inputs"/> by default, as the input set of <paramref name="spec"/>'s operations.</summary>
    internal static InputSet InputSet(Microsoft.Accordant.Spec<ArchiveLogStoreState> spec, IReadOnlyList<object>? requests = null)
    {
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, LogOutcome>(operation).With(request, request.ToString()));
        foreach (var input in requests ?? Inputs)
        {
            switch (input)
            {
                case LogAppend append:
                    Add(ArchiveLogStoreSpec.AppendOperation, append);
                    break;
                case LogOpen open:
                    Add(ArchiveLogStoreSpec.OpenOperation, open);
                    break;
                case LogRead read:
                    Add(ArchiveLogStoreSpec.ReadOperation, read);
                    break;
                case LogProperties properties:
                    Add(ArchiveLogStoreSpec.PropertiesOperation, properties);
                    break;
                case LogDeleteBase delete:
                    Add(ArchiveLogStoreSpec.DeleteBaseOperation, delete);
                    break;
                default:
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{input} is not an input of the spec."));
            }
        }

        return inputs;
    }

    /// <summary>
    /// Registers the spec's operations, drawing positions over three generations and short runs; <paramref name="run"/>
    /// executes each request. Each generation's units have one length, <see cref="ArchiveLogStoreSpec.UnitLength"/>, so a
    /// position's units follow from its length, as a token's in a key's chain do.
    /// </summary>
    internal static ConformanceKit<ArchiveLogStoreState, TSystem> ArchiveLogStoreOperations<TSystem>(
        this ConformanceKit<ArchiveLogStoreState, TSystem> kit,
        Func<TSystem, object, Task<LogOutcome>> run,
        bool listings = true)
        where TSystem : IAsyncDisposable
    {
        var positions = Gen.Frequency(
            (1, Gen.Const((0, 0, 0))),
            (4, Gen.Int[1, 3].SelectMany(static generation => Gen.Int[0, 4].Select(units => (generation, units * ArchiveLogStoreSpec.UnitLength(generation), units)))));
        var counts = Gen.Int[1, 3];
        Task<LogOutcome> Run<TRequest>(TSystem system, TRequest request)
            where TRequest : notnull =>
            run(system, request);

        return kit
            .Operation(
                ArchiveLogStoreSpec.AppendOperation,
                Gen.Select(positions, counts, static (from, count) => new LogAppend(from.Item1, from.Item2, from.Item3, [.. Enumerable.Repeat(ArchiveLogStoreSpec.UnitLength(Math.Max(from.Item1, 1)), count)])),
                Run,
                weight: 5)
            .Operation(
                ArchiveLogStoreSpec.OpenOperation,
                Gen.Select(positions, Gen.Int[1, 3], Gen.Int[0, 2], static (from, opener, length) => new LogOpen(from.Item1, from.Item2, from.Item3, (SpecOpener)opener, length)),
                Run,
                weight: 2)
            .Operation(ArchiveLogStoreSpec.ReadOperation, Gen.Const(LogRead.Instance), Run, weight: 2)
            .Operation(ArchiveLogStoreSpec.PropertiesOperation, Gen.Const(LogProperties.Instance), Run)
            .Operation(ArchiveLogStoreSpec.ListOperation, Gen.Const(LogList.Instance), Run, weight: listings ? 1 : 0)
            .Operation(ArchiveLogStoreSpec.DeleteBaseOperation, Gen.Int[1, 2].Select(static generation => new LogDeleteBase(generation)), Run);
    }

    /// <summary>
    /// Returns <paramref name="cases"/> with stragglers: after some round, a copy of an earlier write of the same case,
    /// as a racing drain, a retry, or a straggler re-issues it, weighted toward the latest writes.
    /// </summary>
    internal static Gen<ConformanceCase> WithReplays(this Gen<ConformanceCase> cases) =>
        Gen.Select(cases, Gen.Int[0, 3], Gen.Int[0, 63].Array[3], Gen.Int[0, 1023].Array[3], static (@case, count, picks, places) =>
        {
            var rounds = @case.Rounds.ToList();
            for (var replay = 0; replay < count; replay++)
            {
                var writes = rounds.SelectMany(static round => round.Calls).Where(static call => call.Request is LogAppend or LogOpen).ToList();
                if (writes.Count == 0)
                {
                    break;
                }

                // Replay(k): the k-th latest write, the latest half the time, then each older one half as often.
                var pick = picks[replay];
                var k = pick < 32 ? 0 : pick < 48 ? 1 : pick < 56 ? 2 : pick % writes.Count;
                var at = rounds.Count - places[replay] % (rounds.Count + 1);
                rounds.Insert(at, new ConformanceRound([writes[writes.Count - 1 - Math.Min(k, writes.Count - 1)]]));
            }

            return @case with { Rounds = rounds };
        });
}
