using CsCheck;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A system the fold oracle drives one ledger's processes through, over one implementation of the ledger storage
/// contract (ledger-protocol.md §2.7): each operation of <see cref="LedgerStorageSpec"/>, answered as its outcome.
/// </summary>
internal interface ILedgerConformanceSystem : IAsyncDisposable
{
    Task<LedgerOutcome> AppendAsync(LedgerAppend request);

    Task<LedgerOutcome> AcquireAsync(LedgerAcquire request);

    Task<LedgerOutcome> AppendUnansweredAsync(LedgerAppendUnanswered request);

    Task<LedgerOutcome> ReadAsync(LedgerSlotRead request);

    Task<LedgerOutcome> ReadRangeAsync(LedgerRangeRead request);

    Task<LedgerOutcome> ScanAsync(LedgerScan request);

    Task<LedgerOutcome> CheckAsync(LedgerCheck request);

    Task<LedgerOutcome> LearnAsync(LedgerLearn request);

    Task<LedgerOutcome> RecoverAsync(LedgerRecover request);

    LedgerOutcome Observe();

    Task<LedgerOutcome> DropAsync(LedgerDrop request);
}

/// <summary>
/// The operations the fold-oracle harnesses drive an <see cref="ILedgerConformanceSystem"/> with, and their generators:
/// the contract's every call (<see cref="LedgerStorageSpec"/>), weighted toward appends and reads.
/// </summary>
internal static class LedgerConformanceOperations
{
    /// <summary>Gets the values an append writes.</summary>
    internal static Gen<byte> Values { get; } = Gen.Byte[1, 8];

    /// <summary>Gets the slots a read names, past the written ones too.</summary>
    internal static Gen<long> Slots { get; } = Gen.Long[0, 6];

    /// <summary>Gets the appends whose outcome is unknown: two in three landed.</summary>
    internal static Gen<LedgerAppendUnanswered> Unanswered { get; } =
        Gen.Select(Values, Gen.Int[0, 2]).Select(static (value, landed) => new LedgerAppendUnanswered(value, Landed: landed > 0));

    /// <summary>Gets the ranged reads, some past their batch, whose lengths run from 1 to 64.</summary>
    internal static Gen<LedgerRangeRead> RangeReads { get; } =
        Gen.Select(Slots, Gen.Int[0, 40], Gen.Int[1, 40]).Select(static (slot, offset, length) => new LedgerRangeRead(slot, offset, length));

    /// <summary>Gets the seal checks: two in three the primary's.</summary>
    internal static Gen<LedgerCheck> Checks { get; } = Gen.Int[0, 2].Select(static choice => new LedgerCheck(Elsewhere: choice == 0));

    /// <summary>Gets the learns: two in three another process's.</summary>
    internal static Gen<LedgerLearn> Learns { get; } = Gen.Int[0, 2].Select(static choice => new LedgerLearn(Elsewhere: choice > 0));

    /// <summary>Gets every call, weighted as the kits weigh their operations.</summary>
    internal static Gen<ConformanceCall> Calls { get; } = Gen.Frequency(
        (8, Values.Select(static value => new ConformanceCall(LedgerStorageSpec.Append, new LedgerAppend(value)))),
        (1, Unanswered.Select(static request => new ConformanceCall(LedgerStorageSpec.AppendUnanswered, request))),
        (4, Slots.Select(static slot => new ConformanceCall(LedgerStorageSpec.Read, new LedgerSlotRead(slot)))),
        (2, RangeReads.Select(static request => new ConformanceCall(LedgerStorageSpec.ReadRange, request))),
        (1, Slots.Select(static from => new ConformanceCall(LedgerStorageSpec.Scan, new LedgerScan(from)))),
        (3, Checks.Select(static request => new ConformanceCall(LedgerStorageSpec.Check, request))),
        (2, Learns.Select(static request => new ConformanceCall(LedgerStorageSpec.Learn, request))),
        (1, Gen.Bool.Select(static elsewhere => new ConformanceCall(LedgerStorageSpec.Recover, new LedgerRecover(elsewhere)))),
        (1, Gen.Const(new ConformanceCall(LedgerStorageSpec.Observe, LedgerObserve.Instance))),
        (2, Gen.Select(Values, Gen.Enum<AcquireUse>()).Select(static (value, use) => new ConformanceCall(LedgerStorageSpec.Acquire, new LedgerAcquire(value, use)))),
        (1, Gen.Bool.Select(static every => new ConformanceCall(LedgerStorageSpec.Drop, new LedgerDrop(every)))));

    /// <summary>Adds every operation of the contract to <paramref name="kit"/>, weighted as <see cref="Calls"/> is.</summary>
    internal static ConformanceKit<LedgerState, TSystem> WithLedgerOperations<TSystem>(this ConformanceKit<LedgerState, TSystem> kit)
        where TSystem : class, ILedgerConformanceSystem =>
        kit.Operation(LedgerStorageSpec.Append, Values.Select(static value => new LedgerAppend(value)), static (system, request) => system.AppendAsync(request), weight: 8)
            .Operation(LedgerStorageSpec.AppendUnanswered, Unanswered, static (system, request) => system.AppendUnansweredAsync(request))
            .Operation(LedgerStorageSpec.Read, Slots.Select(static slot => new LedgerSlotRead(slot)), static (system, request) => system.ReadAsync(request), weight: 4)
            .Operation(LedgerStorageSpec.ReadRange, RangeReads, static (system, request) => system.ReadRangeAsync(request), weight: 2)
            .Operation(LedgerStorageSpec.Scan, Slots.Select(static from => new LedgerScan(from)), static (system, request) => system.ScanAsync(request))
            .Operation(LedgerStorageSpec.Check, Checks, static (system, request) => system.CheckAsync(request), weight: 3)
            .Operation(LedgerStorageSpec.Learn, Learns, static (system, request) => system.LearnAsync(request), weight: 2)
            .Operation(LedgerStorageSpec.Recover, Gen.Bool.Select(static elsewhere => new LedgerRecover(elsewhere)), static (system, request) => system.RecoverAsync(request))
            .Operation(LedgerStorageSpec.Observe, Gen.Const(LedgerObserve.Instance), static (system, _) => Task.FromResult(system.Observe()))
            .Operation(
                LedgerStorageSpec.Acquire,
                Gen.Select(Values, Gen.Enum<AcquireUse>()).Select(static (value, use) => new LedgerAcquire(value, use)),
                static (system, request) => system.AcquireAsync(request),
                weight: 2)
            .Operation(LedgerStorageSpec.Drop, Gen.Bool.Select(static every => new LedgerDrop(every)), static (system, request) => system.DropAsync(request));
}
