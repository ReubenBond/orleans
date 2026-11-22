using System.Diagnostics;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Holds the ledgers' hand-written unions, the replica answers <see cref="ReplicaRead"/> and
/// <see cref="ReplicaTailRead"/> (ledger-replicas.md §4) and the append's <see cref="AppendOutcome"/>
/// (ledger-protocol.md §2.4), to the union laws (conventions.md §15).
/// </summary>
public sealed class LedgerUnionLawsTests : IDisposable
{
    private static readonly Gen<Slot> Slots = Gen.Long[0, 1L << 40].Select(static slot => new Slot(slot));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void AppendOutcomeKeepsTheUnionLaws() => UnionLaws.Check(new AppendOutcomeAdapter(), _leaks);

    [Fact]
    public void ReplicaReadKeepsTheUnionLaws() => UnionLaws.Check(new ReplicaReadAdapter(), _leaks);

    [Fact]
    public void ReplicaTailReadKeepsTheUnionLaws() => UnionLaws.Check(new ReplicaTailReadAdapter(), _leaks);

    [Fact]
    public void ReplicaHeartbeatReadKeepsTheUnionLaws() => UnionLaws.Check(new ReplicaHeartbeatReadAdapter(), _leaks);

    private static Gen<Func<object>> One(object value) => Gen.Const<Func<object>>(() => value);

    [Fact]
    public void ProposalOutcomeKeepsTheUnionLaws() => UnionLaws.Check(new ProposalOutcomeAdapter(), _leaks);

    [Fact]
    public void LedgerReadKeepsTheUnionLaws() => UnionLaws.Check(new LedgerReadAdapter(), _leaks);

    private sealed class LedgerReadAdapter : IUnionLawsAdapter<LedgerRead>
    {
        public IReadOnlyList<UnionCase<LedgerRead>> Cases { get; } =
        [
            new(
                nameof(LedgerRead.Data),
                Gen.Select(Slots, Gen.Byte.Array[1, 64], Gen.Int[1, 8]).Select(static (slot, bytes, count) => (Func<object>)(() => new LedgerRead.Data(slot, Buffer(bytes), count))),
                static value => new LedgerRead((LedgerRead.Data)value),
                static value => ((LedgerRead.Data)value).Dispose()),

            new(
                nameof(ReplicaWriteOutcome.Dropped),
                One(ReplicaWriteOutcome.Dropped.Instance),
                static value => new LedgerRead((ReplicaWriteOutcome.Dropped)value)),
            new(
                nameof(ReplicaWriteOutcome.Backpressure),
                One(ReplicaWriteOutcome.Backpressure.Instance),
                static value => new LedgerRead((ReplicaWriteOutcome.Backpressure)value)),
        ];

        public int Switch(LedgerRead value) => value switch
        {
            LedgerRead.Data => 0,
            ReplicaWriteOutcome.Dropped => 1,
            ReplicaWriteOutcome.Backpressure => 2,
            null => throw new UnreachableException(),
        };

        private static Orleans.Durable.Storage.Engine.Buffers.ArcBuffer Buffer(byte[] bytes)
        {
            using var writer = new Orleans.Durable.Storage.Engine.Buffers.ArcBufferWriter();
            writer.Write(bytes);
            return writer.ConsumeSlice(writer.Length);
        }
    }

    private sealed class ProposalOutcomeAdapter : IUnionLawsAdapter<ProposalOutcome>
    {
        public IReadOnlyList<UnionCase<ProposalOutcome>> Cases { get; } =
        [
            new(
                nameof(ProposalOutcome.Durable),
                Slots.Select(static slot => (Func<object>)(() => new ProposalOutcome.Durable(slot))),
                static value => new ProposalOutcome((ProposalOutcome.Durable)value)),
            new(
                nameof(ProposalOutcome.Fenced),
                Gen.Bool.Select(static _ => (Func<object>)(static () => default(ProposalOutcome.Fenced))),
                static value => new ProposalOutcome((ProposalOutcome.Fenced)value)),
            new(
                nameof(ProposalOutcome.Ambiguous),
                Gen.Bool.Select(static _ => (Func<object>)(static () => default(ProposalOutcome.Ambiguous))),
                static value => new ProposalOutcome((ProposalOutcome.Ambiguous)value)),
        ];

        public IEnumerable<(Func<ProposalOutcome> Left, Func<ProposalOutcome> Right)> EqualFieldsInDifferentCases =>
        [
            (static () => new ProposalOutcome(new ProposalOutcome.Durable(Slot.Zero)), static () => new ProposalOutcome(default(ProposalOutcome.Fenced))),
            (static () => new ProposalOutcome(default(ProposalOutcome.Fenced)), static () => new ProposalOutcome(default(ProposalOutcome.Ambiguous))),
        ];

        public int Switch(ProposalOutcome value) => value switch
        {
            ProposalOutcome.Durable => 0,
            ProposalOutcome.Fenced => 1,
            ProposalOutcome.Ambiguous => 2,
            null => throw new UnreachableException(),
        };
    }

    private sealed class AppendOutcomeAdapter : IUnionLawsAdapter<AppendOutcome>
    {
        public IReadOnlyList<UnionCase<AppendOutcome>> Cases { get; } =
        [
            new(
                nameof(AppendOutcome.Appended),
                Slots.Select(static slot => (Func<object>)(() => new AppendOutcome.Appended(slot))),
                static value => new AppendOutcome((AppendOutcome.Appended)value)),
            new(
                nameof(AppendOutcome.Sealed),
                Slots.Select(static slot => (Func<object>)(() => new AppendOutcome.Sealed(slot))),
                static value => new AppendOutcome((AppendOutcome.Sealed)value)),
            new(
                nameof(AppendOutcome.Unresolved),
                Gen.Select(Gen.Bool, Slots, static (granted, slot) => (Func<object>)(() => new AppendOutcome.Unresolved(granted ? slot : null))),
                static value => new AppendOutcome((AppendOutcome.Unresolved)value)),
        ];

        public IEnumerable<(Func<AppendOutcome> Left, Func<AppendOutcome> Right)> EqualFieldsInDifferentCases =>
        [
            (static () => new AppendOutcome(new AppendOutcome.Appended(new Slot(3))), static () => new AppendOutcome(new AppendOutcome.Sealed(new Slot(3)))),
            (static () => new AppendOutcome(new AppendOutcome.Sealed(new Slot(3))), static () => new AppendOutcome(new AppendOutcome.Unresolved(new Slot(3)))),
            (static () => new AppendOutcome(new AppendOutcome.Appended(Slot.Zero)), static () => new AppendOutcome(new AppendOutcome.Unresolved(Slot.Zero))),
        ];

        public int Switch(AppendOutcome value) => value switch
        {
            AppendOutcome.Appended => 0,
            AppendOutcome.Sealed => 1,
            AppendOutcome.Unresolved => 2,
            null => throw new UnreachableException(),
        };
    }

    private sealed class ReplicaReadAdapter : IUnionLawsAdapter<ReplicaRead>
    {
        public IReadOnlyList<UnionCase<ReplicaRead>> Cases { get; } =
        [
            new(
                nameof(ArcBuffer),
                Gen.Byte.Array[1, 9_000].Select(static bytes => (Func<object>)(() => LedgerReplicaTestData.Buffer(bytes))),
                static value => new ReplicaRead((ArcBuffer)value),
                static value => ((ArcBuffer)value).Dispose()),
            new(nameof(ReplicaRead.Missing), One(ReplicaRead.Missing.Instance), static value => new ReplicaRead((ReplicaRead.Missing)value)),
            new(nameof(ReplicaWriteOutcome.Dropped), One(ReplicaWriteOutcome.Dropped.Instance), static value => new ReplicaRead((ReplicaWriteOutcome.Dropped)value)),
            new(nameof(ReplicaWriteOutcome.Backpressure), One(ReplicaWriteOutcome.Backpressure.Instance), static value => new ReplicaRead((ReplicaWriteOutcome.Backpressure)value)),
            new(
                nameof(ReplicaRead.OutsideBatch),
                Gen.Int[1, int.MaxValue].Select(static length => (Func<object>)(() => new ReplicaRead.OutsideBatch(length))),
                static value => new ReplicaRead((ReplicaRead.OutsideBatch)value)),
        ];

        public int Switch(ReplicaRead value) => value switch
        {
            ArcBuffer => 0,
            ReplicaRead.Missing => 1,
            ReplicaWriteOutcome.Dropped => 2,
            ReplicaWriteOutcome.Backpressure => 3,
            ReplicaRead.OutsideBatch => 4,
            null => throw new UnreachableException(),
        };
    }

    private sealed class ReplicaTailReadAdapter : IUnionLawsAdapter<ReplicaTailRead>
    {
        public IReadOnlyList<UnionCase<ReplicaTailRead>> Cases { get; } =
        [
            new(
                nameof(LedgerReplicaTail),
                Gen.Select(Slots, Gen.Bool, static (tail, isSealed) => (Func<object>)(() => new LedgerReplicaTail(tail, isSealed))),
                static value => new ReplicaTailRead((LedgerReplicaTail)value)),
            new(nameof(ReplicaWriteOutcome.Dropped), One(ReplicaWriteOutcome.Dropped.Instance), static value => new ReplicaTailRead((ReplicaWriteOutcome.Dropped)value)),
            new(nameof(ReplicaWriteOutcome.Backpressure), One(ReplicaWriteOutcome.Backpressure.Instance), static value => new ReplicaTailRead((ReplicaWriteOutcome.Backpressure)value)),
        ];

        public int Switch(ReplicaTailRead value) => value switch
        {
            LedgerReplicaTail => 0,
            ReplicaWriteOutcome.Dropped => 1,
            ReplicaWriteOutcome.Backpressure => 2,
            null => throw new UnreachableException(),
        };
    }

    private sealed class ReplicaHeartbeatReadAdapter : IUnionLawsAdapter<ReplicaHeartbeatRead>
    {
        private static readonly Gen<ReplicaHeartbeatStatus> Statuses = Gen.Byte[0, 3].Select(static value => (ReplicaHeartbeatStatus)value);

        public IReadOnlyList<UnionCase<ReplicaHeartbeatRead>> Cases { get; } =
        [
            new(
                nameof(ReplicaHeartbeatReply),
                Gen.Select(Gen.Long[0, TimeSpan.FromMinutes(1).Ticks], Statuses.Array[0, 8], static (ticks, statuses) => (Func<object>)(() => new ReplicaHeartbeatReply(TimeSpan.FromTicks(ticks), statuses))),
                static value => new ReplicaHeartbeatRead((ReplicaHeartbeatReply)value)),
            new(nameof(ReplicaWriteOutcome.Backpressure), One(ReplicaWriteOutcome.Backpressure.Instance), static value => new ReplicaHeartbeatRead((ReplicaWriteOutcome.Backpressure)value)),
        ];

        public int Switch(ReplicaHeartbeatRead value) => value switch
        {
            ReplicaHeartbeatReply => 0,
            ReplicaWriteOutcome.Backpressure => 1,
            null => throw new UnreachableException(),
        };
    }
}
