using System.Collections.Immutable;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;
using Orleans.Serialization;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The control plane's grain messages (orleans.md §7.2): every case of each transition, verdict, change, report, and
/// compaction message crosses through its surrogate unchanged, for fixed examples and generated ones, with a wire
/// encoding a snapshot pins; an unknown case tag, a field missing, and fields the type's constructor refuses, are refused
/// with <see cref="InvalidDataException"/>; and a drop crosses naming its ledger.
/// </summary>
[NoPageLeakCheck("Grain messages carry no pooled pages (OR-I18).")]
public sealed class ControlMessageTests : IDisposable
{
    private static readonly KeyRange Lower = KeyRange.Root.Children.Lower;
    private static readonly TransitionChoice Choice = new(Node(1), [Node(1), Node(2), Node(3)], 4, chosenAt: 5, epoch: 9);
    private static readonly TransitionChoice ObjectStoreChoice = new(Node(1), LedgerLocation.ObjectStore.Instance, 64, chosenAt: 5, epoch: 9);
    private static readonly PrimaryClaim Claim = new(Node(1), new SequencerInstanceId(Guid.Parse("00000000-0000-4000-8000-0000000000aa")));
    private static readonly Tag First = new(new LedgerId(1), Slot.Zero, 0);
    private static readonly Tag Last = new(new LedgerId(1), new Slot(4), 2);
    private static readonly SectionRef Input = new(new ObjectName("d/1/0/a"), 0, 10, Lower, First, Last, new ObjectRange(10, 5));
    private static readonly SectionRef Output = new(new ObjectName("c/1/b"), 0, 12, Lower, First, Last, new ObjectRange(12, 5));

    private static readonly Transition[] Transitions =
    [
        new Transition.Bootstrap(Choice),
        new Transition.Bootstrap(ObjectStoreChoice),
        new Transition.Replace(new LedgerId(3), Choice),
        new Transition.Replace(new LedgerId(3), Choice, PredecessorReleaseHint.Cooperative),
        new Transition.Split(new LedgerId(3), Choice),
        new Transition.Merge(new LedgerId(3), new LedgerId(4), Choice),
    ];

    private static readonly PrepareReleaseRequest ReleaseRequest = new(new LedgerId(3), Lower, TimeSpan.FromSeconds(5));

    private static readonly PrepareReleaseReply[] ReleaseReplies =
    [
        new PrepareReleaseReply.Released(new Slot(7)),
        new PrepareReleaseReply.Released(Slot.Zero),
        PrepareReleaseReply.Sealed.Instance,
        PrepareReleaseReply.TimedOut.Instance,
        PrepareReleaseReply.NotServed.Instance,
        PrepareReleaseReply.Declined.Instance,
    ];

    private static readonly TransitionReply[] Replies =
    [
        new LedgerMapCommitOutcome.Applied(9),
        new LedgerMapCommitOutcome.Superseded(10),

        // An absent register decides at epoch 0: a drop's goal holds in it, and a stale install's rule fails.
        new LedgerMapCommitOutcome.Applied(0),
        new LedgerMapCommitOutcome.Superseded(0),
        LedgerMapCommitOutcome.Refused.Instance,
        new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.OpensLeaveGap),
        TransitionReply.Declined.Instance,
    ];

    private static readonly LedgerMapChange[] MapChanges =
    [
        new LedgerMapChange.Install([new LedgerClosing(new LedgerId(3), new Slot(7))], [new LedgerOpening(Lower, [Node(1), Node(2), Node(3)], 4, Claim)], chosenAt: 5),
        new LedgerMapChange.Install([], [new LedgerOpening(KeyRange.Root, [Node(1)], 1, Claim)], chosenAt: 1),
        new LedgerMapChange.Install([], [new LedgerOpening(Lower, LedgerLocation.ObjectStore.Instance, 64, Claim)], chosenAt: 2),
        new LedgerMapChange.RaiseFloor(6),
        new LedgerMapChange.Drop(new LedgerId(3)),
    ];

    private static readonly ManifestChange[] ManifestChanges =
    [
        new ManifestChange.Drain(new LedgerId(1), Slot.Zero, new Slot(5), [Input], complete: false),
        new ManifestChange.Drain(new LedgerId(1), new Slot(5), new Slot(5), [], complete: true),
        new ManifestChange.Compaction([Input], [Output]),
    ];

    private static readonly CompactionReply[] CompactionReplies =
    [
        new CompactionReply.Compacted(new ManifestCommitOutcome.Applied(3)),
        new CompactionReply.Compacted(new ManifestCommitOutcome.Superseded(4)),
        CompactionReply.NothingToCompact.Instance,
        new CompactionReply.Corrupt(Lower, "An input breaks its format."),
        new CompactionReply.Corrupt(KeyRange.Root, "An input of the root partition breaks its format."),
    ];

    private static readonly NodeReport Report = new(
        Node(1),
        Generation: 7,
        Sequence: 19,
        [new LedgerId(3)],
        ImmutableDictionary<LedgerId, long>.Empty.Add(new LedgerId(4), 1024),
        DiskPressure: true,
        ImmutableDictionary<LedgerId, DateTimeOffset>.Empty.Add(new LedgerId(2), new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero)),
        [new LedgerId(5)],
        UndrainedKeys: 42,
        ArchiveLogRequestsPerSecond: 250,
        HandingOff: true,
        ArchiveGatePassed: true,
        Draining: true,
        ObjectStoreDemand: 512,
        ObjectStoreRequestsPerSecond: 180,
        ObjectLedgersReady: true,
        ReleasedLedgers: ImmutableDictionary<LedgerId, Slot>.Empty.Add(new LedgerId(6), new Slot(11)));

    private static readonly NodeReportReply ReportReply = new(12, [new LedgerId(2)], ArchiveLogShare: 125.5, ObjectStoreShare: 90.25);

    private static readonly NodeStanding[] Standings =
    [
        NodeStanding.NotRegistered.Instance,
        NodeStanding.Active.Instance,
        new NodeStanding.Retired(RetiredRevision: 5, Epoch: 9, Floor: 5, [new LedgerId(2), new LedgerId(7)]),
        new NodeStanding.Retired(RetiredRevision: 5, Epoch: 12, Floor: 6, []),
    ];

    private static readonly OrdinalNode AtOrdinal = new(Node(3), 4, new NodeStanding.Retired(RetiredRevision: 5, Epoch: 9, Floor: 5, [new LedgerId(2)]));

    private static readonly DrainAllStartOutcome[] DrainAllStartOutcomes =
    [
        DrainAllStartOutcome.Started.Instance,
        DrainAllStartOutcome.ObjectLedgersNotConfigured.Instance,
        DrainAllStartOutcome.ObjectLedgersNotReady.Instance,
    ];

    private static readonly DrainAllStatus[] DrainAllStatuses =
    [
        new(Active: true, Epoch: 9, Floor: 4, RegistryRevision: 7, LatestDiskfulRetiredRevision: 5, [new LedgerId(2), new LedgerId(7)], [Node(1), Node(3)]),
        new(Active: false, Epoch: 14, Floor: 9, RegistryRevision: 9, LatestDiskfulRetiredRevision: 9, [], []),
    ];

    /// <summary>Every surrogate with a field missing which its case carries, by name; each is refused.</summary>
    private static readonly Dictionary<string, Func<object>> MissingFields = new(StringComparer.Ordinal)
    {
        ["bootstrap without its choice"] = () => new TransitionConverter().ConvertFromSurrogate(new TransitionSurrogate { Case = TransitionSurrogate.BootstrapTag }),
        ["replace without its ledger"] = () => new TransitionConverter().ConvertFromSurrogate(new TransitionSurrogate { Case = TransitionSurrogate.ReplaceTag, Choice = Choice }),
        ["replace with an unknown predecessor hint"] = () => new TransitionConverter().ConvertFromSurrogate(new TransitionSurrogate { Case = TransitionSurrogate.ReplaceTag, Ledger = new LedgerId(3), Choice = Choice, PredecessorHint = 99 }),
        ["prepare release request without its ledger"] = () => new PrepareReleaseRequestConverter().ConvertFromSurrogate(new PrepareReleaseRequestSurrogate { Range = Lower, Deadline = TimeSpan.FromSeconds(5) }),
        ["prepare release request without its range"] = () => new PrepareReleaseRequestConverter().ConvertFromSurrogate(new PrepareReleaseRequestSurrogate { Ledger = new LedgerId(3), Deadline = TimeSpan.FromSeconds(5) }),
        ["prepare release request with non-positive deadline"] = () => new PrepareReleaseRequestConverter().ConvertFromSurrogate(new PrepareReleaseRequestSurrogate { Ledger = new LedgerId(3), Range = Lower, Deadline = TimeSpan.Zero }),
        ["prepare release reply without its contiguous tail"] = () => new PrepareReleaseReplyConverter().ConvertFromSurrogate(new PrepareReleaseReplySurrogate { Case = PrepareReleaseReplySurrogate.ReleasedTag }),
        ["report naming a released ledger by the default ID"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [], ReleasedLedgers = new Dictionary<LedgerId, Slot> { [default] = Slot.Zero } }),
        ["merge without its second ledger"] = () => new TransitionConverter().ConvertFromSurrogate(new TransitionSurrogate { Case = TransitionSurrogate.MergeTag, Ledger = new LedgerId(3), Choice = Choice }),
        ["choice without its replicas"] = () => new TransitionChoiceConverter().ConvertFromSurrogate(new TransitionChoiceSurrogate { Executor = Node(1), K = 4 }),
        ["applied verdict without its epoch"] = () => new LedgerMapCommitOutcomeConverter().ConvertFromSurrogate(new LedgerMapVerdictSurrogate { Case = LedgerMapVerdictSurrogate.AppliedTag }),
        ["superseded reply without its epoch"] = () => new TransitionReplyConverter().ConvertFromSurrogate(new LedgerMapVerdictSurrogate { Case = LedgerMapVerdictSurrogate.SupersededTag }),
        ["rejection without a reason it defines"] = () => new LedgerMapCommitOutcomeConverter().ConvertFromSurrogate(new LedgerMapVerdictSurrogate { Case = LedgerMapVerdictSurrogate.RejectedTag, Reason = 99 }),
        ["install without its closings"] = () => new LedgerMapChangeConverter().ConvertFromSurrogate(new LedgerMapChangeSurrogate { Case = LedgerMapChangeSurrogate.InstallTag, Opens = [] }),
        ["install without its openings"] = () => new LedgerMapChangeConverter().ConvertFromSurrogate(new LedgerMapChangeSurrogate { Case = LedgerMapChangeSurrogate.InstallTag, Closes = [] }),
        ["opening without its location"] = () => new LedgerOpeningConverter().ConvertFromSurrogate(new LedgerOpeningSurrogate { Range = Lower, K = 4, Primary = Claim }),
        ["opening without its range"] = () => new LedgerOpeningConverter().ConvertFromSurrogate(new LedgerOpeningSurrogate { Location = new LedgerLocation.Nodes([Node(1), Node(2), Node(3)]), K = 4, Primary = Claim }),
        ["drain without its sections"] = () => new ManifestChangeConverter().ConvertFromSurrogate(new ManifestChangeSurrogate { Case = ManifestChangeSurrogate.DrainTag, Ledger = new LedgerId(1), Below = new Slot(5) }),
        ["compaction without its inputs"] = () => new ManifestChangeConverter().ConvertFromSurrogate(new ManifestChangeSurrogate { Case = ManifestChangeSurrogate.CompactionTag, Outputs = [Output] }),
        ["compaction without its outputs"] = () => new ManifestChangeConverter().ConvertFromSurrogate(new ManifestChangeSurrogate { Case = ManifestChangeSurrogate.CompactionTag, Sections = [Input] }),
        ["manifest verdict without its epoch"] = () => new ManifestCommitOutcomeConverter().ConvertFromSurrogate(new ManifestCommitOutcomeSurrogate { Case = ManifestCommitOutcomeSurrogate.SupersededTag }),
        ["corrupt reply without its partition"] = () => new CompactionReplyConverter().ConvertFromSurrogate(new CompactionReplySurrogate { Case = CompactionReplySurrogate.CorruptTag, Reason = "corrupt" }),
        ["corrupt reply without its reason"] = () => new CompactionReplyConverter().ConvertFromSurrogate(new CompactionReplySurrogate { Case = CompactionReplySurrogate.CorruptTag, Partition = Lower }),
        ["compacted reply without its verdict"] = () => new CompactionReplyConverter().ConvertFromSurrogate(new CompactionReplySurrogate { Case = CompactionReplySurrogate.CompactedTag }),
        ["assignment without its partition"] = () => new CompactionAssignmentConverter().ConvertFromSurrogate(new CompactionAssignmentSurrogate { Assignee = Node(2), ManifestEpoch = 8 }),
        ["assignment without its assignee"] = () => new CompactionAssignmentConverter().ConvertFromSurrogate(new CompactionAssignmentSurrogate { Partition = Lower, ManifestEpoch = 8 }),
        ["report without its node"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Generation = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [] }),
        ["report without its generation"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [] }),
        ["report of a negative generation"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = -1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [] }),
        ["report numbered from 0"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [] }),
        ["report without its unserved ledgers"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [] }),
        ["report without its loads"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ClosedDrainProgress = [], LostReplicas = [] }),
        ["report without its drains' progress"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], LostReplicas = [] }),
        ["report without its lost replicas"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [] }),
        ["report naming a lost replica by the default ID"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [default] }),
        ["report counting negative undrained keys"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [], UndrainedKeys = -1 }),
        ["report running its drains at 0"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [], ArchiveLogRequestsPerSecond = 0 }),
        ["report running its drains at an infinite rate"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [], ArchiveLogRequestsPerSecond = double.PositiveInfinity }),
        ["report counting negative object-store demand"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [], ObjectStoreDemand = -1 }),
        ["report running its object store at 0"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [], ObjectStoreRequestsPerSecond = 0 }),
        ["report running its object store at an infinite rate"] = () => new NodeReportConverter().ConvertFromSurrogate(new NodeReportSurrogate { Node = Node(1), Generation = 1, Sequence = 1, Unserved = [], ProposedBytesPerSecond = [], ClosedDrainProgress = [], LostReplicas = [], ObjectStoreRequestsPerSecond = double.PositiveInfinity }),
        ["report reply without its ledgers to drain"] = () => new NodeReportReplyConverter().ConvertFromSurrogate(new NodeReportReplySurrogate { MapEpoch = 3 }),
        ["report reply assigning a share of 0"] = () => new NodeReportReplyConverter().ConvertFromSurrogate(new NodeReportReplySurrogate { MapEpoch = 3, DrainToo = [], ArchiveLogShare = 0 }),
        ["report reply assigning a share which is no number"] = () => new NodeReportReplyConverter().ConvertFromSurrogate(new NodeReportReplySurrogate { MapEpoch = 3, DrainToo = [], ArchiveLogShare = double.NaN }),
        ["report reply assigning an infinite share"] = () => new NodeReportReplyConverter().ConvertFromSurrogate(new NodeReportReplySurrogate { MapEpoch = 3, DrainToo = [], ArchiveLogShare = double.PositiveInfinity }),
        ["report reply assigning a negative share"] = () => new NodeReportReplyConverter().ConvertFromSurrogate(new NodeReportReplySurrogate { MapEpoch = 3, DrainToo = [], ArchiveLogShare = -1 }),
        ["report reply assigning an object-store share of 0"] = () => new NodeReportReplyConverter().ConvertFromSurrogate(new NodeReportReplySurrogate { MapEpoch = 3, DrainToo = [], ObjectStoreShare = 0 }),
        ["report reply assigning an infinite object-store share"] = () => new NodeReportReplyConverter().ConvertFromSurrogate(new NodeReportReplySurrogate { MapEpoch = 3, DrainToo = [], ObjectStoreShare = double.PositiveInfinity }),
        ["location with an unknown case tag"] = () => new LedgerLocationConverter().ConvertFromSurrogate(new LedgerLocationSurrogate { Case = 99 }),
        ["nodes location without its replicas"] = () => new LedgerLocationConverter().ConvertFromSurrogate(new LedgerLocationSurrogate { Case = LedgerLocationSurrogate.NodesTag }),
        ["nodes location with empty replicas"] = () => new LedgerLocationConverter().ConvertFromSurrogate(new LedgerLocationSurrogate { Case = LedgerLocationSurrogate.NodesTag, Replicas = [] }),
        ["nodes location with an even replica count"] = () => new LedgerLocationConverter().ConvertFromSurrogate(new LedgerLocationSurrogate { Case = LedgerLocationSurrogate.NodesTag, Replicas = [Node(1), Node(2)] }),
        ["object-store location carrying replicas"] = () => new LedgerLocationConverter().ConvertFromSurrogate(new LedgerLocationSurrogate { Case = LedgerLocationSurrogate.ObjectStoreTag, Replicas = [Node(1)] }),
        ["retired standing without its ledgers"] = () => new NodeStandingConverter().ConvertFromSurrogate(new NodeStandingSurrogate { Case = NodeStandingSurrogate.RetiredTag, RetiredRevision = 5, Epoch = 9, Floor = 5 }),
        ["retired standing without its revision"] = () => new NodeStandingConverter().ConvertFromSurrogate(new NodeStandingSurrogate { Case = NodeStandingSurrogate.RetiredTag, Epoch = 9, Floor = 5, Ledgers = [] }),
        ["node at an ordinal without its node"] = () => new OrdinalNodeConverter().ConvertFromSurrogate(new OrdinalNodeSurrogate { Ordinal = 1, Standing = NodeStanding.Active.Instance }),
        ["node at an ordinal without its standing"] = () => new OrdinalNodeConverter().ConvertFromSurrogate(new OrdinalNodeSurrogate { Node = Node(1), Ordinal = 1 }),
        ["drain-all status without its node ledgers"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { ActiveDiskfulNodes = [] }),
        ["drain-all status without its active diskful nodes"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { NodeLedgers = [] }),
        ["drain-all status with a default ledger ID"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { Epoch = 1, RegistryRevision = 1, NodeLedgers = [default], ActiveDiskfulNodes = [] }),
        ["drain-all status with a default node ID"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { RegistryRevision = 1, NodeLedgers = [], ActiveDiskfulNodes = [default] }),
        ["drain-all status with duplicate ledger IDs"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { Epoch = 1, RegistryRevision = 1, NodeLedgers = [new LedgerId(3), new LedgerId(3)], ActiveDiskfulNodes = [] }),
        ["drain-all status with duplicate active diskful node IDs"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { RegistryRevision = 1, NodeLedgers = [], ActiveDiskfulNodes = [Node(1), Node(1)] }),
        ["drain-all status with latest retirement above registry revision"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { RegistryRevision = 2, LatestDiskfulRetiredRevision = 3, NodeLedgers = [], ActiveDiskfulNodes = [] }),
        ["drain-all status active at registry revision 0"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { Active = true, RegistryRevision = 0, NodeLedgers = [], ActiveDiskfulNodes = [] }),
        ["drain-all status with active diskful nodes at registry revision 0"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { RegistryRevision = 0, NodeLedgers = [], ActiveDiskfulNodes = [Node(1)] }),
        ["drain-all status with positive floor at map epoch 0"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { Epoch = 0, Floor = 1, RegistryRevision = 1, NodeLedgers = [], ActiveDiskfulNodes = [] }),
        ["drain-all status with node ledgers at map epoch 0"] = () => new DrainAllStatusConverter().ConvertFromSurrogate(new DrainAllStatusSurrogate { Epoch = 0, RegistryRevision = 1, NodeLedgers = [new LedgerId(3)], ActiveDiskfulNodes = [] }),
        ["section without its object"] = () => new SectionRefConverter().ConvertFromSurrogate(new SectionRefSurrogate { Offset = 0, Length = 1, Partition = Lower, MinTag = First, MaxTag = Last, TailOffset = 1, TailLength = 1 }),
        ["section without its partition"] = () => new SectionRefConverter().ConvertFromSurrogate(new SectionRefSurrogate { Object = Input.Object, Offset = 0, Length = 1, MinTag = First, MaxTag = Last, TailOffset = 1, TailLength = 1 }),
        ["object name without its name"] = () => new ObjectNameConverter().ConvertFromSurrogate(default),
    };

    private static readonly Gen<NodeId> Nodes = Gen.Int[1, 40].Select(Node);

    private static readonly Gen<LedgerId> Ledgers = Gen.ULong[1, 1UL << 40].Select(static id => new LedgerId(id));

    private static readonly Gen<TransitionChoice> Choices = Gen.OneOf(
        Gen.Select(Nodes.HashSet[1, 5].Where(static replicas => replicas.Count % 2 == 1), Gen.Int[1, 64], Gen.ULong, Gen.ULong)
            .Select(static fields => new TransitionChoice(fields.Item1.First(), [.. fields.Item1], fields.Item2, fields.Item3, fields.Item4)),
        Gen.Select(Nodes, Gen.Int[1, 64], Gen.ULong, Gen.ULong)
            .Select(static fields => new TransitionChoice(fields.Item1, LedgerLocation.ObjectStore.Instance, fields.Item2, fields.Item3, fields.Item4)));

    private static readonly Gen<Transition> GeneratedTransitions = Gen.OneOf(
        Choices.Select(static choice => (Transition)new Transition.Bootstrap(choice)),
        Gen.Select(Ledgers, Choices, Gen.Enum<PredecessorReleaseHint>()).Select(static fields => (Transition)new Transition.Replace(fields.Item1, fields.Item2, fields.Item3)),
        Gen.Select(Ledgers, Choices).Select(static fields => (Transition)new Transition.Split(fields.Item1, fields.Item2)),
        Gen.Select(Ledgers, Ledgers, Choices).Where(static fields => fields.Item1 != fields.Item2).Select(static fields => (Transition)new Transition.Merge(fields.Item1, fields.Item2, fields.Item3)));

    private static readonly Gen<TransitionReply> GeneratedReplies = Gen.OneOf(
        Gen.ULong.Select(static epoch => (TransitionReply)new LedgerMapCommitOutcome.Applied(epoch)),
        Gen.ULong.Select(static epoch => (TransitionReply)new LedgerMapCommitOutcome.Superseded(epoch)),
        Gen.Const((TransitionReply)LedgerMapCommitOutcome.Refused.Instance),
        Gen.Enum<LedgerMapRejection>().Select(static reason => (TransitionReply)new LedgerMapCommitOutcome.Rejected(reason)),
        Gen.Const((TransitionReply)TransitionReply.Declined.Instance));

    private readonly ServiceProvider _services = new ServiceCollection()
        .AddSerializer(static serializer => serializer.AddAssembly(typeof(NodeRegistryEntry).Assembly))
        .BuildServiceProvider();

    private Serializer Serializer => _services.GetRequiredService<Serializer>();

    public void Dispose() => _services.Dispose();

    public static TheoryData<string> MissingFieldNames => [.. MissingFields.Keys];

    [Fact]
    public void EveryCaseCrossesUnchanged()
    {
        Assert.All(Transitions, transition => Assert.Equal(transition, RoundTrip(transition)));
        Assert.Equal(ReleaseRequest, RoundTrip(ReleaseRequest));
        Assert.All(ReleaseReplies, reply => Assert.Equal(reply, RoundTrip(reply)));
        Assert.All(Replies, reply => Assert.Equal(reply, RoundTrip(reply)));
        Assert.All(Replies.Where(static reply => reply.Value is not TransitionReply.Declined).Select(Outcome), outcome => Assert.Equal(outcome, RoundTrip(outcome)));
        Assert.All(MapChanges, change => Assert.Equal(change, RoundTrip(change)));
        Assert.All(ManifestChanges, change => Assert.Equal(change, RoundTrip(change)));
        Assert.Equal<ManifestCommitOutcome>(new ManifestCommitOutcome.Applied(3), RoundTrip<ManifestCommitOutcome>(new ManifestCommitOutcome.Applied(3)));
        Assert.Equal<ManifestCommitOutcome>(new ManifestCommitOutcome.Superseded(4), RoundTrip<ManifestCommitOutcome>(new ManifestCommitOutcome.Superseded(4)));
        Assert.All(CompactionReplies, reply => Assert.Equal(reply, RoundTrip(reply)));
        var assignment = new CompactionAssignment(Lower, Node(2), 8);
        Assert.Equal(assignment, RoundTrip(assignment));
    }

    [Fact]
    public void AReportItsReplyAndEveryStandingCrossUnchanged()
    {
        var crossed = RoundTrip(Report);
        Assert.Equal((Report.Node, Report.Generation, Report.Sequence), (crossed.Node, crossed.Generation, crossed.Sequence));
        Assert.Equal(Report.Unserved, crossed.Unserved);
        Assert.Equal(Report.ProposedBytesPerSecond, crossed.ProposedBytesPerSecond);
        Assert.Equal(Report.DiskPressure, crossed.DiskPressure);
        Assert.Equal(Report.ClosedDrainProgress, crossed.ClosedDrainProgress);
        Assert.Equal(Report.ReleasedLedgersOrEmpty, crossed.ReleasedLedgersOrEmpty);
        Assert.Equal((Report.ObjectStoreDemand, Report.ObjectStoreRequestsPerSecond, Report.ObjectLedgersReady), (crossed.ObjectStoreDemand, crossed.ObjectStoreRequestsPerSecond, crossed.ObjectLedgersReady));
        var withReady = Report with { ObjectLedgersReady = true };
        Assert.True(RoundTrip(withReady).ObjectLedgersReady);

        var answered = RoundTrip(ReportReply);
        Assert.Equal(ReportReply.MapEpoch, answered.MapEpoch);
        Assert.Equal(ReportReply.DrainToo, answered.DrainToo);
        Assert.Equal((ReportReply.ArchiveLogShare, ReportReply.ObjectStoreShare), (answered.ArchiveLogShare, answered.ObjectStoreShare));

        Assert.All(Standings, standing => Assert.Equal(standing, RoundTrip(standing)));
        Assert.Equal(AtOrdinal, RoundTrip(AtOrdinal));
        Assert.All(DrainAllStartOutcomes, outcome => Assert.Equal(outcome, RoundTrip(outcome)));
        Assert.All(DrainAllStatuses, status => Assert.Equal(status, RoundTrip(status)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public void AnUnknownDrainAllStartOutcomeTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new DrainAllStartOutcomeConverter().ConvertFromSurrogate(new DrainAllStartOutcomeSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public void AnUnknownStandingTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new NodeStandingConverter().ConvertFromSurrogate(new NodeStandingSurrogate { Case = tag, RetiredRevision = 5, Ledgers = [] }));

    /// <summary>verification §4: generated transitions and transition replies cross unchanged.</summary>
    [Fact]
    public void GeneratedTransitionsAndRepliesCrossUnchanged()
    {
        GeneratedTransitions.Sample(transition => Assert.Equal(transition, RoundTrip(transition)), iter: 500);
        GeneratedReplies.Sample(reply => Assert.Equal(reply, RoundTrip(reply)), iter: 200);
    }

    /// <summary>verification §4: generated reports, report replies, and compaction assignments cross unchanged.</summary>
    [Fact]
    public void GeneratedReportsAndAssignmentsCrossUnchanged()
    {
        Gen.Select(Nodes, Gen.Select(Gen.Int[1, int.MaxValue], Gen.Long[1, long.MaxValue]), Ledgers.HashSet[0, 4], Gen.Select(Ledgers, Gen.Long[0, long.MaxValue]).Array[0, 4], Gen.Select(Gen.Bool, Gen.Bool), Ledgers.HashSet[0, 4], Gen.Long[0, long.MaxValue], Gen.Select(Gen.Bool, Gen.Double[1e-3, 1e6], static (some, rate) => some ? rate : (double?)null))
            .Sample(
                fields =>
                {
                    var report = new NodeReport(fields.Item1, fields.Item2.Item1, fields.Item2.Item2, [.. fields.Item3], fields.Item4.DistinctBy(static load => load.Item1).ToImmutableDictionary(static load => load.Item1, static load => load.Item2), fields.Item5.Item1, [], [.. fields.Item6], fields.Item7, fields.Item8, HandingOff: fields.Item5.Item2, ArchiveGatePassed: fields.Item5.Item1 != fields.Item5.Item2, Draining: fields.Item5.Item1 == fields.Item5.Item2);
                    var crossed = RoundTrip(report);
                    Assert.Equal((report.Node, report.Generation, report.Sequence, report.DiskPressure, report.UndrainedKeys, report.ArchiveLogRequestsPerSecond, report.HandingOff, report.ArchiveGatePassed, report.Draining), (crossed.Node, crossed.Generation, crossed.Sequence, crossed.DiskPressure, crossed.UndrainedKeys, crossed.ArchiveLogRequestsPerSecond, crossed.HandingOff, crossed.ArchiveGatePassed, crossed.Draining));
                    Assert.Equal(report.Unserved, crossed.Unserved);
                    Assert.Equal(report.ProposedBytesPerSecond, crossed.ProposedBytesPerSecond);
                    Assert.Equal(report.LostReplicas, crossed.LostReplicas);
                },
                iter: 200);
        Gen.Select(Gen.ULong, Ledgers.HashSet[0, 4], Gen.Select(Gen.Bool, Gen.Double[1e-3, 1e6], static (some, share) => some ? share : (double?)null), Gen.Select(Gen.Bool, Gen.Double[1e-3, 1e6], static (some, share) => some ? share : (double?)null)).Sample(
            fields =>
            {
                var reply = new NodeReportReply(fields.Item1, [.. fields.Item2], fields.Item3, fields.Item4);
                var crossed = RoundTrip(reply);
                Assert.Equal(reply.DrainToo, crossed.DrainToo);
                Assert.Equal(reply.ArchiveLogShare, crossed.ArchiveLogShare);
                Assert.Equal(reply.ObjectStoreShare, crossed.ObjectStoreShare);
            },
            iter: 200);
        Gen.Select(Gen.OneOfConst(KeyRange.Root, Lower, Lower.Children.Upper), Nodes, Gen.ULong).Sample(
            fields =>
            {
                var assignment = new CompactionAssignment(fields.Item1, fields.Item2, fields.Item3);
                Assert.Equal(assignment, RoundTrip(assignment));
            },
            iter: 200);
    }

    /// <summary>
    /// On archive logs a drain worker submits its ledger's drop through a grain call, which carries the ledger, and a drop
    /// naming no ledger is refused (§5.6, §7.2).
    /// </summary>
    [Fact]
    public void ADropCrossesNamingItsLedger()
    {
        var drop = new LedgerMapChange.Drop(new LedgerId(3));
        Assert.Equal(drop, RoundTrip<LedgerMapChange>(drop).Value);
        _ = Assert.Throws<InvalidDataException>(() => new LedgerMapChangeConverter().ConvertFromSurrogate(new LedgerMapChangeSurrogate { Case = LedgerMapChangeSurrogate.DropTag }));
    }

    [Theory]
    [MemberData(nameof(MissingFieldNames))]
    public void AFieldMissingIsRefused(string message) =>
        _ = Assert.Throws<InvalidDataException>(() => MissingFields[message]());

    /// <summary>Each refused report fails the check its vector names, which its refusal states.</summary>
    [Theory]
    [InlineData("report without its node", "names its node")]
    [InlineData("report without its generation", "positive generation")]
    [InlineData("report of a negative generation", "positive generation")]
    [InlineData("report numbered from 0", "numbered in its process from 1")]
    [InlineData("report without its unserved ledgers", "unserved ledgers")]
    [InlineData("report without its loads", "carries its loads")]
    [InlineData("report without its drains' progress", "drains' progress")]
    [InlineData("report without its lost replicas", "lost replicas")]
    [InlineData("report naming a lost replica by the default ID", "ledgers by ID")]
    public void ARefusedReportStatesTheCheckItFailed(string vector, string refusal) =>
        Assert.Contains(refusal, Assert.Throws<InvalidDataException>(() => MissingFields[vector]()).Message, StringComparison.Ordinal);

    [Fact]
    public Task EveryCaseHasAPinnedWireEncoding() => Verify(new
    {
        Transitions = Transitions.Select(Wire),
        ReleaseRequest = Wire(ReleaseRequest),
        ReleaseReplies = ReleaseReplies.Select(Wire),
        Replies = Replies.Select(Wire),
        MapChanges = MapChanges.Select(Wire),
        ManifestChanges = ManifestChanges.Select(Wire),
        CompactionReplies = CompactionReplies.Select(Wire),
        Assignment = Wire(new CompactionAssignment(Lower, Node(2), 8)),
        Report = Wire(Report),
        ReportReply = Wire(ReportReply),
        ReportReplyWithoutShare = Wire(ReportReply with { ArchiveLogShare = null, ObjectStoreShare = null }),
        Standings = Standings.Select(Wire),
        AtOrdinal = Wire(AtOrdinal),
        DrainAllStartOutcomes = DrainAllStartOutcomes.Select(Wire),
        DrainAllStatuses = DrainAllStatuses.Select(Wire),
    }).DontScrubGuids();

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(255)]
    public void AnUnknownPrepareReleaseReplyTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new PrepareReleaseReplyConverter().ConvertFromSurrogate(new PrepareReleaseReplySurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(255)]
    public void AnUnknownTransitionTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new TransitionConverter().ConvertFromSurrogate(new TransitionSurrogate { Case = tag, Ledger = new LedgerId(3), Second = new LedgerId(4), Choice = Choice }));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(255)]
    public void AnUnknownTransitionReplyTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new TransitionReplyConverter().ConvertFromSurrogate(new LedgerMapVerdictSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void AnUnknownCommitOutcomeTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new LedgerMapCommitOutcomeConverter().ConvertFromSurrogate(new LedgerMapVerdictSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void AnUnknownMapChangeTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new LedgerMapChangeConverter().ConvertFromSurrogate(new LedgerMapChangeSurrogate { Case = tag, Closes = [], Opens = [] }));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void AnUnknownManifestChangeTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new ManifestChangeConverter().ConvertFromSurrogate(new ManifestChangeSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void AnUnknownManifestOutcomeTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new ManifestCommitOutcomeConverter().ConvertFromSurrogate(new ManifestCommitOutcomeSurrogate { Case = tag }));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public void AnUnknownCompactionReplyTagIsRefused(byte tag) =>
        _ = Assert.Throws<InvalidDataException>(() => new CompactionReplyConverter().ConvertFromSurrogate(new CompactionReplySurrogate { Case = tag, Reason = "corrupt" }));

    [Fact]
    public void FieldsTheirTypesRefuseAreRefused()
    {
        _ = Assert.Throws<InvalidDataException>(() => new SlotConverter().ConvertFromSurrogate(new SlotSurrogate { Value = -1 }));
        _ = Assert.Throws<InvalidDataException>(() => new KeyRangeConverter().ConvertFromSurrogate(new KeyRangeSurrogate { Depth = 1, Prefix = [0xff] }));
        _ = Assert.Throws<InvalidDataException>(() => new TagConverter().ConvertFromSurrogate(new TagSurrogate { Index = 0 }));
    }

    private static LedgerMapCommitOutcome Outcome(TransitionReply reply) => reply switch
    {
        LedgerMapCommitOutcome.Applied applied => applied,
        LedgerMapCommitOutcome.Superseded superseded => superseded,
        LedgerMapCommitOutcome.Refused refused => refused,
        LedgerMapCommitOutcome.Rejected rejected => rejected,
        TransitionReply.Declined => throw new ArgumentException("A declined reply relays no commit outcome.", nameof(reply)),
        null => throw new ArgumentException("conventions.md §15: a reply holds a case.", nameof(reply)),
    };

    private T RoundTrip<T>(T value) => Serializer.Deserialize<T>(Serializer.SerializeToArray(value))!;

    private string Wire<T>(T value) => Convert.ToHexStringLower(Serializer.SerializeToArray(value));
}
