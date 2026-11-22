using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>The history reader over the LSM, whose drain uploads a closed ledger's rows as one section and completes it.</summary>
public sealed class LsmArchiveReaderTests : ArchiveReaderContract
{
    private readonly InMemoryObjectStore _objects = new();
    private readonly ManifestView _manifest;
    private readonly ManifestCommitter _committer;
    private readonly LsmArchiveStore _store;

    public LsmArchiveReaderTests()
    {
        _manifest = new ManifestView(_objects);
        _committer = new ManifestCommitter(_objects, _manifest, TimeProvider.System, TimeSpan.Zero);
        _store = new LsmArchiveStore(_objects, new ManifestView(_objects), new TailCache(_objects, new ArchiveOptions()), new ArchiveOptions(), TimeProvider.System);
    }

    protected override IArchiveStore Store => _store;

    protected override IConditionalRegister NewRegister() => new InMemoryConditionalRegister();

    protected override async Task LandAsync(LedgerId ledger, Slot end, IReadOnlyList<TaggedRow> rows, CancellationToken cancellationToken)
    {
        TaggedRow[] sorted = [.. rows.OrderBy(static row => row.Row.Key.ToArray(), Comparer<byte[]>.Create(static (left, right) => left.AsSpan().SequenceCompareTo(right))).ThenBy(static row => row.Tag)];
        var name = RunObjectNames.Drain(ledger, Slot.Zero, RunObjectNames.NewId());
        using var encoded = RunObjectFormat.Encode([new RunSectionInput(KeyRange.Root, sorted)], new RunObjectCondition.Drain(ledger, Slot.Zero), new ArchiveOptions().SectionSettings);
        _ = await _objects.CreateAsync(name, encoded.Content, null, cancellationToken);
        var outcome = await _committer.CommitAsync(new ManifestChange.Drain(ledger, Slot.Zero, end, [.. encoded.SectionRefs(name)], complete: true), cancellationToken);
        _ = Assert.IsType<ManifestCommitOutcome.Applied>(outcome.Value);
    }

    protected override ValueTask DisposeStoreAsync() => _committer.DisposeAsync();
}
