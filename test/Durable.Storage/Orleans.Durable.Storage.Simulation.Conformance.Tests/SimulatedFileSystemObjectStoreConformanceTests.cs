using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.FileSystem.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// The <c>IObjectStore</c> spec over <see cref="FileSystemObjectStore"/> on the recorded file system
/// (<see cref="RecordingFileSystem"/>), in memory, each case in a simulation of its own (verification.md §4.1): CsCheck's
/// sequences under seeded schedules, and Accordant's concurrent windows explored. The store only ever takes the recorded
/// file system, so no simulation reaches the real disk (verification.md §7.1). As in <c>ObjectStoreConformanceTests</c>,
/// the concurrent cases leave listings out, which a concurrent create or delete bounds by its own test.
/// </summary>
public sealed class SimulatedFileSystemObjectStoreConformanceTests
{
    /// <summary>The root of each case's recorded file system, which lives in memory.</summary>
    private static readonly string Root = Path.GetFullPath("clog-simulated-file-system-store");

    /// <summary>
    /// Every concurrent group of Accordant's generation over the store's writes, reads, and deletes, after every shorter
    /// sequence, conforms under every schedule of its window within the bound, with each of the store's two ways to
    /// publish a file: linking and renaming.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryConcurrentWindowConformsUnderEveryExploredSchedule(bool linkByRenaming)
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var spec = ObjectStoreSpec.Create(unknownOutcomes: false);
        var kit = Kit(conformance, linkByRenaming, listings: false);

        var cases = spec.GenerateConcurrentTests(new ObjectStoreState(), Inputs(spec), kit.GenerationOptions(2));
        Assert.NotEmpty(cases);
        await kit.RunAsync(cases);

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// Every concurrent group of Accordant's generation over appendable-object creates, appends, seals, tombstones, reads,
    /// property queries, and deletes, after every shorter sequence, conforms under every schedule of its window within the
    /// bound.
    /// </summary>
    [Fact]
    public async Task EveryAppendableWindowConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var spec = ObjectStoreSpec.Create(unknownOutcomes: false);
        var kit = Kit(conformance, linkByRenaming: false, listings: false);

        var cases = spec.GenerateConcurrentTests(new ObjectStoreState(), AppendableInputs(spec), kit.GenerationOptions(2));
        Assert.NotEmpty(cases);
        await kit.RunAsync(cases);

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// Skipping <c>store.lock</c> in <see cref="FileSystemObjectStore.ReplaceAsync"/>
    /// (<see cref="FileSystemObjectStore.SkipsReplaceLockMutant"/>) lets two concurrent replaces of one version both check
    /// the version before either renames and both answer <see cref="ObjectReplaceOutcome.Applied"/>. The default schedule
    /// runs each call to completion and passes; schedule exploration preempts between the version check and the rename and
    /// flags the race.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchAnUnheldReplaceLock()
    {
        ConformanceCase witness = new(
        [
            new([new(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, null, 1))]),
            new([new(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, 1, 2)), new(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, 1, 3))]),
        ]);

        var explored = new SimulatedConformance(ScheduleBound.Gate);
        var failure = await Assert.ThrowsAsync<ConformanceScheduleException>(
            () => Kit(explored, linkByRenaming: false, listings: false, skipsReplaceLockMutant: true).RunAsync(witness));
        Assert.Contains("The spec does not allow round 2", failure.Message, StringComparison.Ordinal);
        Assert.Contains(WindowScheduleExplorer.ScheduleVariable + "=", failure.Message, StringComparison.Ordinal);
        var found = Assert.Single(explored.Explorations);
        Assert.True(found.Schedules > 1, $"The default schedule failed: {found}.");
        Assert.Contains(found.Failure!.Prefix, static choice => choice != 0);
        TestDiagnostics.WriteLine(FormattableString.Invariant(
            $"The unheld-replace-lock mutant: caught at {found}, by schedule {WindowScheduleExplorer.Format(found.Failure.Prefix)}."));
    }

    /// <summary>
    /// CsCheck's sequences, listings among them, and its concurrent sequences conform, each case under its seeded
    /// schedule.
    /// </summary>
    [Fact]
    public async Task SequencesConformUnderSeededSchedules()
    {
        var conformance = new SimulatedConformance();
        var kit = Kit(conformance, linkByRenaming: false, listings: true);
        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 1);

        var concurrent = Kit(conformance, linkByRenaming: true, listings: false);
        await concurrent.SampleAsync(concurrent.ConcurrentSequences(10), iterations: 40, threads: 1);

        TestDiagnostics.WriteLine(conformance.Summary());
    }

    /// <summary>
    /// Returns the input set of the plain-object explored windows: creates of one created-once name and replaces of the
    /// manifest, each pair racing on its condition, versioned reads, and deletes.
    /// </summary>
    private static InputSet Inputs(Spec<ObjectStoreState> spec)
    {
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, ObjectOutcome>(operation).With(request, request.ToString()));

        Add(ObjectStoreSpec.CreateOperation, new ObjectCreate(1, 1));
        Add(ObjectStoreSpec.CreateOperation, new ObjectCreate(1, 2));
        Add(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, null, 3));
        Add(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, null, 4));
        Add(ObjectStoreSpec.ReplaceOperation, new ObjectReplace(0, 3, 5));
        Add(ObjectStoreSpec.ReadVersionedOperation, new ObjectReadVersioned(0));
        Add(ObjectStoreSpec.DeleteOperation, new ObjectDelete(0));
        return inputs;
    }

    /// <summary>
    /// Returns the input set of the appendable-object explored windows: creation, racing appends at the same length, seal,
    /// tombstone, reads, properties, and deletion of one appendable name.
    /// </summary>
    private static InputSet AppendableInputs(Spec<ObjectStoreState> spec)
    {
        var inputs = new InputSet();
        void Add<TRequest>(string operation, TRequest request)
            where TRequest : notnull =>
            inputs.Add(spec.GetOperation<TRequest, ObjectOutcome>(operation).With(request, request.ToString()));

        Add(ObjectStoreSpec.CreateAppendableOperation, new ObjectCreateAppendable(4));
        Add(ObjectStoreSpec.AppendOperation, new ObjectAppend(4, 0, 1));
        Add(ObjectStoreSpec.AppendOperation, new ObjectAppend(4, 0, 2));
        Add(ObjectStoreSpec.SealOperation, new ObjectSeal(4));
        Add(ObjectStoreSpec.TombstoneOperation, new ObjectTombstone(4));
        Add(ObjectStoreSpec.ReadOperation, new ObjectRead(4, 0, ObjectStoreSpec.ChunkLength));
        Add(ObjectStoreSpec.PropertiesOperation, new ObjectGetProperties(4));
        Add(ObjectStoreSpec.DeleteOperation, new ObjectDelete(4));
        return inputs;
    }

    private static ConformanceKit<ObjectStoreState, Target> Kit(
        SimulatedConformance conformance,
        bool linkByRenaming,
        bool listings,
        bool skipsReplaceLockMutant = false) =>
        new ConformanceKit<ObjectStoreState, Target>(
                ObjectStoreSpec.Create(unknownOutcomes: false),
                static () => new ObjectStoreState(),
                _ => Task.FromResult(new Target(new FileSystemObjectStore(Root, SimulationEnvironment.Clock, new RecordingFileSystem(Root) { LinkByRenaming = linkByRenaming, YieldsAtIo = true })
                {
                    SkipsReplaceLockMutant = skipsReplaceLockMutant,
                })),
                ObjectStoreSpec.IsCallerAllowed,
                CancellationToken.None,
                conflicts: ObjectStoreSpec.Conflicts,
                scheduler: conformance)
            .ObjectStoreOperations(static (target, request) => target.RunAsync(request), listings, appends: true);

    /// <summary>
    /// One case's store, over a recorded file system of its own, and the executor of its calls.
    /// </summary>
    private sealed class Target(FileSystemObjectStore store) : IAsyncDisposable
    {
        private readonly ObjectStoreExecutor _executor = new(store, store);

        public Task<ObjectOutcome> RunAsync(object request) => _executor.RunAsync(request, CancellationToken.None);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
