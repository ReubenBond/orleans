using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using CsCheck;
using Orleans.Durable.Journaling;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

[assembly: AssemblyFixture(typeof(CloudEmulators))]
[assembly: CloudEmulatorsUse(Emulator.Azurite)]

namespace Orleans.Durable.Storage.Interop.Tests;

/// <summary>
/// Plain Orleans reads the Orleans composition's archive logs (archive-logs.md §7.3, §10; orleans.md §5.13):
/// Orleans' own Azure journaling provider, <c>AzureBlobJournalStorage</c>, configured as a plain deployment with read-only
/// access to the container Clog's <see cref="AzureArchiveLogStore"/> writes through <see cref="ArchiveLogWriter"/>, in the
/// composition's names (<see cref="OrleansJournalLogNames"/>), reads each journal as the fold of the writes its log holds,
/// byte for byte. Each case runs on the fake Azure transport on every change, on Azurite under <c>DURABLE_STORAGE_EMULATORS=1</c>,
/// and on a live account under <see cref="AzureLiveAccount.Variable"/>, each backend's result a test case of its own.
/// The cases are evidence of archive-logs.md §7.3, not of OR-I8: they write through <see cref="ArchiveLogWriter"/>,
/// bypassing <c>DurableJournalStorage</c>, whose payloads OR-I8 governs.
/// </summary>
/// <remarks>
/// The store packs blocks of at most <see cref="BlockBytes"/> bytes, so most runs split across blocks. After each write
/// and read, the fake's checks of what archive logs keep of their blobs, and of conditions it does not model, must hold.
/// </remarks>
public sealed class OrleansArchiveLogInteropTests(CloudEmulators emulators) : IDisposable
{
    /// <summary>The largest block the store packs, so a run of more than one small <c>Append</c> splits across blocks.</summary>
    private const int BlockBytes = 8;

    /// <summary>The cases each milestone run of the random-sequence property runs from its recorded seed.</summary>
    private const int MilestoneCases = 12;

    /// <summary>The cases each every-change run of the random-sequence property runs on the fake transport.</summary>
    private const int FakeCases = 100;

    /// <summary>The environment variable which replays a CsCheck case by its seed (verification.md §4.1).</summary>
    private const string SeedVariable = "DURABLE_STORAGE_TEST_SEED";

    /// <summary>A log's generation before its first write: none, so its first opener's checkpoint is generation 1's.</summary>
    private const ulong NoGeneration = 0;

    /// <summary>The generation a key's first append run creates.</summary>
    private const ulong FirstRunGeneration = 1;

    private static readonly ArchiveLogLayout SmallBlocks = new(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, BlockBytes, AzureArchiveLogStore.MaxBlocks, AzureArchiveLogStore.ReplaceRequestBlocks, AzureArchiveLogMetadata.NameRule);

    private static readonly byte[] Tombstone = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>An opener's kind, for the torn opener's cases.</summary>
    public enum OpenerKind
    {
        /// <summary>A <c>Replace</c>.</summary>
        Replace,

        /// <summary>A <c>Delete</c> with a tombstone.</summary>
        Delete,

        /// <summary>An empty <c>Delete</c>, as an <c>Expired</c>.</summary>
        Expired,
    }

    /// <summary>The state a torn opener starts from.</summary>
    public enum Predecessor
    {
        /// <summary>No log: the opener is the key's first write.</summary>
        Absent,

        /// <summary>Generation 1, which a first append run created, with no checkpoint.</summary>
        FirstRun,

        /// <summary>An opener's generation, with a checkpoint and appends after it in its write-ahead blob.</summary>
        Opened,
    }

    /// <summary>Gets the backends: the fake transport, Azurite, and the live account.</summary>
    public static TheoryData<InteropBackend> Backends => [InteropBackend.Fake, InteropBackend.Azurite, InteropBackend.Live];

    /// <summary>Gets every backend with every opener kind and predecessor.</summary>
    public static TheoryData<InteropBackend, OpenerKind, Predecessor> TornOpeners =>
        [.. Enum.GetValues<InteropBackend>().SelectMany(static backend => Enum.GetValues<OpenerKind>().SelectMany(kind => Enum.GetValues<Predecessor>().Select(predecessor => (backend, kind, predecessor))))];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>A key's first write, an append run from <c>Origin</c>, which creates generation 1 with no checkpoint.</summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AFirstWriteReadsAsItsAppends(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var journal = new Journal("interop/first");

        await journals.WriteAsync(journal, Command.Append("first"), Command.Append("second"));

        await journals.AssertReadsAsync(journal);
        Assert.False(await services.Owner.GetBlobClient(AzureArchiveLogMetadata.CheckpointName(journal.Id, 1)).ExistsAsync(Token));
    }

    /// <summary>
    /// A journal whose identity holds upper case, a space, and the characters a URI escapes, <c>%</c>, <c>+</c>,
    /// <c>#</c>, <c>?</c>, <c>&amp;</c>, <c>=</c>, <c>;</c>, and <c>~</c>: Clog writes its log through the composition's
    /// names, and plain Orleans finds it by the identity alone, so both sides name and escape its blobs alike
    /// (orleans.md §5.13).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AJournalWhoseIdentityABlobNameEscapesReadsAsItsFold(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var journal = new Journal("Grain.Type/Key A%2F+#?&=;~");

        await journals.WriteAsync(journal, Command.Append("first"), Command.Append("second"));
        await journals.AssertReadsAsync(journal);
        await journals.WriteAsync(journal, Command.Replace("a snapshot"), Command.Append("after"));
        await journals.AssertReadsAsync(journal);

        var names = new List<string>();
        await foreach (var blob in services.Owner.GetBlobsAsync(cancellationToken: Token))
        {
            names.Add(blob.Name);
        }

        Assert.Equal([AzureArchiveLogMetadata.CheckpointName(journal.Id, FirstRunGeneration + 1), AzureArchiveLogMetadata.WalName(journal.Id)], names.Where(static name => !name.StartsWith(ArchiveLogNames.ReservedPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A journal whose identity is not ASCII, which the composition's names name, is refused before admission by Azure's
    /// name rule: an opener's write-ahead blob carries its checkpoint's name in an HTTP header, which holds only ASCII.
    /// Orleans' own provider, with write access in a container of its own, appends to such a journal, and fails, before
    /// the service sees it, the snapshot whose checkpoint's name it would record (pinned, archive-logs.md §7.3).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ANonAsciiIdentityIsRefusedAsOrleansOwnSnapshotOfItFails(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        const string Id = "Grain.Type/Key É𝄞";
        var key = JournalRowKey.Encode(Id);
        Assert.Equal(Id, OrleansJournalLogNames.Names.Of(key.Span).Value);

        Assert.False(journals.Archive.AdmitsKey(key.Span));

        var container = await services.NewContainerAsync();
        await using var orleans = PlainOrleans.Over(container);
        var storage = orleans.Provider.CreateStorage(new JournalId(Id));
        await storage.ReadAsync(new Collector(), Token);
        await storage.AppendAsync(new System.Buffers.ReadOnlySequence<byte>("appended"u8.ToArray()), Token);
        var refused = await Assert.ThrowsAsync<AggregateException>(async () => await storage.ReplaceAsync(new System.Buffers.ReadOnlySequence<byte>("snapshot"u8.ToArray()), Token));
        Assert.All(refused.InnerExceptions, static exception => Assert.IsType<HttpRequestException>(Assert.IsType<RequestFailedException>(exception).InnerException));
        Assert.Equal("appended"u8.ToArray(), (await orleans.ReadAsync(Id)).Content);
        services.AssertNoViolations();
    }

    /// <summary>
    /// An opener whose checkpoint holds its <c>Replace</c>'s body followed by the appends after it in its segment, which
    /// plain Orleans replays as the concatenation, and the appends of a later run after them (precondition 8).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnOpenerWhoseCheckpointConcatenatesAppendsReadsAsTheirFold(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var journal = new Journal("interop/opener");
        await journals.WriteAsync(journal, Command.Append("before"));

        await journals.WriteAsync(journal, Command.Replace("a snapshot"), Command.Append("one"), Command.Append("two"));
        Assert.Equal("a snapshotonetwo"u8.ToArray(), await journals.CheckpointAsync(journal, 2));
        await journals.AssertReadsAsync(journal);

        await journals.WriteAsync(journal, Command.Append("three"));
        await journals.AssertReadsAsync(journal);
    }

    /// <summary>
    /// orleans.md §5.13: a cluster's journals under its store prefix, its blobs <c>{prefix}/wal/{id}</c> and
    /// <c>{prefix}/checkpoints/{id}/{g:D20}</c>, each opener's write-ahead blob naming its checkpoint with the prefix: plain
    /// Orleans, its provider's blob names given the prefix, reads each write as the fold, and a deployment without the
    /// prefix finds no journal, nor does any blob of the cluster's lie outside its prefix.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AJournalUnderTheClustersStorePrefixReadsThroughOrleansPrefixedNames(InteropBackend backend)
    {
        const string Prefix = "tx3lajviy3";
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks, prefix: Prefix);
        var journal = new Journal("interop/prefixed");

        await journals.WriteAsync(journal, Command.Append("before"));
        await journals.AssertReadsAsync(journal);
        await journals.WriteAsync(journal, Command.Replace("a snapshot"), Command.Append("one"));
        Assert.Equal("a snapshotone"u8.ToArray(), await journals.CheckpointAsync(journal, 2));
        await journals.AssertReadsAsync(journal);

        await using var unprefixed = PlainOrleans.Over(services.ReadOnly, journals.Orleans.Format);
        Assert.Null((await unprefixed.ReadAsync(journal.Id)).Metadata);
        await foreach (var blob in services.Owner.GetBlobsAsync(cancellationToken: Token))
        {
            Assert.StartsWith(Prefix + "/", blob.Name, StringComparison.Ordinal);
        }
    }

    /// <summary>An empty <c>Replace</c>: its empty checkpoint, which plain Orleans reads, and an empty journal.</summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnEmptyReplaceReadsAsAnEmptyJournal(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var journal = new Journal("interop/empty-replace");
        await journals.WriteAsync(journal, Command.Append("before"));

        await journals.WriteAsync(journal, Command.Replace(string.Empty));

        Assert.Empty(await journals.CheckpointAsync(journal, 2));
        await journals.AssertReadsAsync(journal);
        await journals.WriteAsync(journal, Command.Append("after"));
        await journals.AssertReadsAsync(journal);
    }

    /// <summary>Runs split across blocks of at most <see cref="BlockBytes"/> bytes, on generation 1 and after an opener.</summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task RunsSplitAcrossBlocksReadAsTheirAppends(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var journal = new Journal("interop/blocks");

        await journals.WriteAsync(journal, Command.Append("abcde"), Command.Append("fgh"), Command.Append("ijklmnop"), Command.Append("q"));
        Assert.Equal(3, await journals.CommittedBlocksAsync(journal));
        await journals.AssertReadsAsync(journal);

        await journals.WriteAsync(journal, Command.Append("rst"), Command.Append("uvwxyz"));
        Assert.Equal(5, await journals.CommittedBlocksAsync(journal));
        await journals.AssertReadsAsync(journal);

        await journals.WriteAsync(journal, Command.Replace("base"), Command.Append("1234567"));
        await journals.WriteAsync(journal, Command.Append("89"), Command.Append("abcdefg"), Command.Append("h"));
        Assert.Equal(2, await journals.CommittedBlocksAsync(journal));
        await journals.AssertReadsAsync(journal);
    }

    /// <summary>
    /// A journal of several MiB, which spans the buffers of each layer: a checkpoint of a 3 MiB <c>Replace</c> and the 1.5
    /// MiB <c>Append</c> after it, which the store writes after a properties read and under <c>Expect: 100-continue</c>,
    /// and a run of two <c>Append</c>s of about 1 MiB each, which Azure's own layout packs into one block.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AJournalOfSeveralMebibytesReadsWhole(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, AzureArchiveLogStore.AzureLayout);
        var journal = new Journal("interop/large");

        await journals.WriteAsync(journal, new Command(RowKind.Replace, Pattern(3 << 20, 1)), new Command(RowKind.Append, Pattern(3 << 19, 2)));
        await journals.WriteAsync(journal, new Command(RowKind.Append, Pattern(1 << 20, 3)), new Command(RowKind.Append, Pattern((1 << 20) + 7, 4)));

        Assert.Equal(1, await journals.CommittedBlocksAsync(journal));
        await journals.AssertReadsAsync(journal);
    }

    /// <summary>
    /// A <c>Delete</c> with no appends after it in its segment leaves an empty checkpoint and an empty write-ahead blob,
    /// which plain Orleans reads as an existing, empty journal: its read completes with the store's format and its metadata read answers (pinned, an observation of
    /// Orleans, archive-logs.md §7.3).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ADeleteReadsAsAnExistingEmptyJournal(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var journal = new Journal("interop/delete");
        await journals.WriteAsync(journal, Command.Append("before"));

        await journals.WriteAsync(journal, Command.Delete(Tombstone));

        var read = await journals.AssertReadsAsync(journal);
        Assert.NotNull(read.Metadata);
        Assert.Empty(read.Content);
    }

    /// <summary>
    /// Orleans' own <c>DeleteAsync</c>, which plain Orleans runs with write access in a container of its own, deletes the
    /// write-ahead blob, so it then reads the journal as absent, where Clog's <c>Delete</c> leaves an existing, empty one
    /// (pinned, archive-logs.md §7.3).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OrleansOwnDeleteLeavesNoJournal(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        var container = await services.NewContainerAsync();
        await using var orleans = PlainOrleans.Over(container);
        var own = new JournalId("interop/orleans-own");
        var storage = orleans.Provider.CreateStorage(own);
        await storage.ReadAsync(new Collector(), Token);
        await storage.AppendAsync(new System.Buffers.ReadOnlySequence<byte>("Orleans' own"u8.ToArray()), Token);
        Assert.Equal("Orleans' own"u8.ToArray(), (await orleans.ReadAsync(own.Value)).Content);

        await storage.DeleteAsync(Token);

        var deleted = await orleans.ReadAsync(own.Value);
        Assert.Null(deleted.Metadata);
        Assert.Null(deleted.Completed.FormatKey);
        Assert.Empty(deleted.Content);
        Assert.False(await container.GetBlobClient(AzureArchiveLogMetadata.WalName(own.Value)).ExistsAsync(Token));
        services.AssertNoViolations();
    }

    /// <summary>
    /// A <c>Delete</c> followed by appends: those in its segment, which its checkpoint holds, and a later run's, which its
    /// write-ahead blob holds.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task ADeleteFollowedByAppendsReadsAsTheAppends(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var journal = new Journal("interop/delete-appends");
        await journals.WriteAsync(journal, Command.Append("before"));

        await journals.WriteAsync(journal, Command.Delete(Tombstone), Command.Append("reborn"), Command.Append("again"));
        Assert.Equal("rebornagain"u8.ToArray(), await journals.CheckpointAsync(journal, 2));
        await journals.AssertReadsAsync(journal);

        await journals.WriteAsync(journal, Command.Append("later"));
        await journals.AssertReadsAsync(journal);
    }

    /// <summary>
    /// An <c>Expired</c>, an empty <c>Delete</c> opener, which plain Orleans reads as an existing, empty journal, as it
    /// reads a <c>Delete</c> (pinned, an observation of Orleans, archive-logs.md §7.3).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AnExpiredReadsAsAnExistingEmptyJournal(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var journal = new Journal("interop/expired");
        await journals.WriteAsync(journal, Command.Append("before"));

        await journals.WriteAsync(journal, Command.Delete([]));

        var read = await journals.AssertReadsAsync(journal);
        Assert.NotNull(read.Metadata);
        Assert.Empty(read.Content);
    }

    /// <summary>
    /// An opener of each kind, from each predecessor, torn between its two requests: its checkpoint of <c>g + 1</c> landed
    /// and its write-ahead blob is still at <c>g</c>, or absent. Plain Orleans cannot see the torn state: the write-ahead
    /// blob names generation <c>g</c>'s checkpoint, if any, so it reads the log as the opener's predecessor left it, whole,
    /// and reads the new generation only once the opener's retry re-creates the write-ahead blob (pinned, archive-logs.md
    /// §7.3).
    /// </summary>
    [Theory]
    [MemberData(nameof(TornOpeners))]
    public async Task ATornOpenerReadsAsItsPredecessor(InteropBackend backend, OpenerKind kind, Predecessor predecessor)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var journal = new Journal("interop/torn");
        if (predecessor != Predecessor.Absent)
        {
            await journals.WriteAsync(journal, Command.Append("first"));
        }

        if (predecessor == Predecessor.Opened)
        {
            await journals.WriteAsync(journal, Command.Replace("snapshot"), Command.Append("tail"));
            await journals.WriteAsync(journal, Command.Append("later"));
        }

        Command[] opener = kind switch
        {
            OpenerKind.Replace => [Command.Replace("torn"), Command.Append("opener")],
            OpenerKind.Delete => [Command.Delete(Tombstone), Command.Append("opener")],
            OpenerKind.Expired => [Command.Delete([]), Command.Append("opener")],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        // The predecessor's generation: none before the first write, 1 after the first append run, and 2 after the opener
        // and the append run after it, which stays in the opener's generation.
        var generation = predecessor switch
        {
            Predecessor.Absent => NoGeneration,
            Predecessor.FirstRun => FirstRunGeneration,
            Predecessor.Opened => FirstRunGeneration + 1,
            _ => throw new ArgumentOutOfRangeException(nameof(predecessor)),
        };

        await journals.TearAsync(journal, opener);

        byte[] checkpoint = [.. opener.Where(static command => command.Kind != RowKind.Delete).SelectMany(static command => command.Body)];
        Assert.Equal(checkpoint, await journals.CheckpointAsync(journal, generation + 1));
        if (predecessor != Predecessor.Absent)
        {
            Assert.Equal(generation.ToString(CultureInfo.InvariantCulture), await journals.WalGenerationAsync(journal));
        }

        await journals.AssertReadsAsync(journal);

        await journals.WriteAsync(journal, opener);
        await journals.AssertReadsAsync(journal);
    }

    /// <summary>
    /// <c>metadata.json</c>'s write-ahead metadata, each laid down beside its checkpoint, holding the base whose MD5 it
    /// records, and an appended block: plain Orleans reads each as its base followed by the block, as Clog's reader does,
    /// in the vector's format, finding its manifest under names in any case, and reading no other name as its manifest.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task TheMetadataVectorsReadAsTheirBaseAndAppends(InteropBackend backend)
    {
        // The vectors are laid down by hand, one after another under one name, so the fake checks no chain of writes.
        await using var services = await InteropServices.CreateAsync(backend, emulators, checkInvariants: false);
        await using var orleans = PlainOrleans.Over(services.ReadOnly);
        var cases = SharedVectors.Cases("archive-logs", "metadata.json");
        var checkpointMetadata = Pairs(cases.Single(static vector => SharedVectors.Text(vector, "blob") == "checkpoint"));
        var walVectors = cases.Where(static vector => SharedVectors.Text(vector, "blob") == "wal").ToArray();
        Assert.NotEmpty(walVectors);
        foreach (var vector in walVectors)
        {
            var label = SharedVectors.Name(vector);
            var log = SharedVectors.Text(vector, "log");
            var format = SharedVectors.Text(vector, "format");
            var name = new ArchiveLogName(log, ulong.Parse(SharedVectors.Text(vector, "keyHash"), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            var metadata = Pairs(vector);
            byte[] baseContent = [];
            var checkpoint = vector.GetProperty("checkpoint").GetString();
            if (checkpoint is not null)
            {
                var baseMd5 = SharedVectors.Text(vector, "baseMd5");
                baseContent = baseMd5 == AzureArchiveLogMetadata.EmptyMd5 ? [] : Encoding.UTF8.GetBytes($"the base of generation {SharedVectors.Text(vector, "generation")}");
                Assert.Equal(baseMd5, Md5Of(baseContent));
                _ = await services.Owner.GetBlockBlobClient(checkpoint).UploadAsync(new MemoryStream(baseContent), new BlobUploadOptions { Metadata = checkpointMetadata }, Token);
            }

            var wal = services.Owner.GetAppendBlobClient(AzureArchiveLogMetadata.WalName(log));
            var tag = vector.GetProperty("tag").GetString();
            _ = await wal.CreateAsync(new AppendBlobCreateOptions { Metadata = metadata, Tags = tag is null ? null : new Dictionary<string, string> { [AzureArchiveLogMetadata.GenerationTag] = tag } }, Token);
            var appended = Encoding.UTF8.GetBytes(label);
            _ = await wal.AppendBlockAsync(new MemoryStream(appended), cancellationToken: Token);

            var read = await orleans.ReadAsync(log);
            Assert.True(read.Content.AsSpan().SequenceEqual([.. baseContent, .. appended]), $"Plain Orleans read '{label}' as {Convert.ToHexStringLower(read.Content)}.");
            Assert.Equal(format, read.Completed.FormatKey);
            Assert.Equal(
                metadata.Keys.Where(static key => !OrleansManifest.Contains(key, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase),
                read.Metadata!.Properties.Keys.Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            var clog = Assert.IsType<ArchiveLogRead.Read>((await new AzureArchiveLogStore(services.Owner, format).ReadAsync(name, Token)).Value);
            using (clog)
            {
                Assert.True(clog.Content.ToArray().AsSpan().SequenceEqual(read.Content), $"Clog read '{label}' otherwise than plain Orleans.");
            }

            _ = await wal.DeleteIfExistsAsync(cancellationToken: Token);
            if (checkpoint is not null)
            {
                _ = await services.Owner.GetBlobClient(checkpoint).DeleteIfExistsAsync(cancellationToken: Token);
            }
        }

        services.AssertNoViolations();
    }

    /// <summary>
    /// Random sequences of writes, append runs and openers of each kind with appends after them, superseding rows or not,
    /// over three journals, written through <see cref="ArchiveLogWriter"/> from the tokens <see cref="LogArchiveStore"/>
    /// plans: after every write, plain Orleans reads its journal as the fold. On the fake transport it runs on every change
    /// under CsCheck, whose failure names the seed which replays it through <c>DURABLE_STORAGE_TEST_SEED</c>; on Azurite and the live
    /// account, at milestones, it runs <see cref="MilestoneCases"/> cases drawn in turn from one seed, which the run
    /// records in its output, and a failing case reports its own seed, which replays it, as the run's first case, on its
    /// backend.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task RandomWriteSequencesReadAsTheirFold(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        var replay = Environment.GetEnvironmentVariable(SeedVariable) is { Length: > 0 } seed ? seed : null;
        if (backend == InteropBackend.Fake)
        {
            await Sequences.SampleAsync(steps => RunAsync(journals, steps), seed: replay, iter: FakeCases, threads: 1);
            return;
        }

        var pcg = replay is null ? new PCG((uint)Random.Shared.Next(), (ulong)Random.Shared.NextInt64()) : PCG.Parse(replay);
        var run = pcg.ToString();
        TestContext.Current.TestOutputHelper?.WriteLine($"The random-sequence property's recorded seed: {run} ({MilestoneCases} cases on {backend}).");
        for (var index = 0; index < MilestoneCases; index++)
        {
            var caseSeed = pcg.ToString();
            var steps = Sequences.Generate(pcg, null, out _);
            try
            {
                await RunAsync(journals, steps);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Case {index} of the run seeded {run} failed on {backend}; replay it on {backend} with {SeedVariable}={caseSeed}.", exception);
            }
        }
    }

    /// <summary>
    /// Plain Orleans with read-only access cannot run its provider's default initialisation, which creates the container
    /// if absent: the service refuses the creation with 403 even where the container exists, so a read-only deployment
    /// gives its provider a container factory which creates nothing, as these tests do (pinned, archive-logs.md §7.3).
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OrleansDefaultInitialisationFailsWithReadOnlyAccess(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var orleans = PlainOrleans.OverService(services.ReadOnlyService, services.Owner.Name);

        var refused = await Assert.ThrowsAsync<RequestFailedException>(orleans.InitializeAsync);

        Assert.Equal(403, refused.Status);
        services.AssertNoViolations();
    }

    /// <summary>
    /// Orleans' catalog lists a journal by its write-ahead blob, so it lists, indefinitely, the journals Clog deleted and
    /// expired as well as live ones, since their logs are not reclaimed (pinned, archive-logs.md §7.3). The catalog needs
    /// the provider's default initialisation, so this deployment has write access.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public async Task OrleansListsDeletedAndExpiredJournals(InteropBackend backend)
    {
        await using var services = await InteropServices.CreateAsync(backend, emulators);
        await using var journals = new ArchiveLogJournals(services, SmallBlocks);
        Journal[] listed = [new("listed/deleted"), new("listed/expired"), new("listed/live")];
        foreach (var journal in listed)
        {
            await journals.WriteAsync(journal, Command.Append("before"));
        }

        await journals.WriteAsync(listed[0], Command.Delete(Tombstone));
        await journals.WriteAsync(listed[1], Command.Delete([]));
        await journals.WriteAsync(new Journal("other/live"), Command.Append("other"));
        await using var orleans = PlainOrleans.OverService(services.Owner.GetParentBlobServiceClient(), services.Owner.Name);
        await orleans.InitializeAsync();

        var entries = new List<string>();
        await foreach (var entry in orleans.Catalog.ListAsync(new JournalCatalogListOptions { Prefix = new JournalId("listed/") }, cancellationToken: Token))
        {
            entries.Add(entry.Id.Value);
        }

        Assert.Equal(listed.Select(static journal => journal.Id), entries);
        services.AssertNoViolations();
    }

    /// <summary>The random-sequence property's sequences: steps over up to three journals.</summary>
    private static Gen<Step[]> Sequences { get; } = CreateSequences();

    /// <summary>Orleans' manifest of the write-ahead blob: the only metadata names it reads.</summary>
    private static string[] OrleansManifest { get; } = [AzureArchiveLogMetadata.Format, AzureArchiveLogMetadata.Checkpoint, AzureArchiveLogMetadata.CheckpointOffset, AzureArchiveLogMetadata.WalGeneration];

    private static Gen<Step[]> CreateSequences()
    {
        var body = Gen.Byte.Array[1, BlockBytes];
        var appends = body.Select(static bytes => new Command(RowKind.Append, bytes)).Array;
        var opener = Gen.Frequency(
            (3, Gen.Byte.Array[0, 3 * BlockBytes].Select(static bytes => new Command(RowKind.Replace, bytes))),
            (2, Gen.Const(new Command(RowKind.Delete, Tombstone))),
            (2, Gen.Const(new Command(RowKind.Delete, []))));
        var run = Gen.Select(Gen.Int[0, 2], appends[1, 5], static (journal, commands) => new Step(journal, commands, 0));
        var opened = Gen.Select(Gen.Int[0, 2], opener, appends[0, 3], Gen.Int[0, 2], static (journal, first, after, superseded) => new Step(journal, [first, .. after], superseded));
        return Gen.Frequency((3, run), (2, opened)).Array[1, 16];
    }

    private static async Task RunAsync(ArchiveLogJournals journals, Step[] steps)
    {
        var number = journals.NextCase();
        var chains = Enumerable.Range(0, 3).Select(index => new Journal(string.Create(CultureInfo.InvariantCulture, $"property/{number}/{index}"))).ToArray();
        foreach (var step in steps)
        {
            var journal = chains[step.Journal];
            await journals.WriteAsync(journal, step.Superseded, step.Commands);
            await journals.AssertReadsAsync(journal);
        }

        foreach (var journal in chains.Where(static journal => !journal.Exists))
        {
            await journals.AssertReadsAsync(journal);
        }
    }

    /// <summary>Returns <paramref name="length"/> bytes of a pattern <paramref name="seed"/> distinguishes.</summary>
    private static byte[] Pattern(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = unchecked((byte)(index * 31 + seed));
        }

        return bytes;
    }

    /// <summary>Returns the MD5 of <paramref name="content"/> in lowercase hexadecimal, as <c>durable_base_md5</c> records it.</summary>
    private static string Md5Of(byte[] content)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(content);
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    private static Dictionary<string, string> Pairs(JsonElement vector) =>
        vector.GetProperty("metadata").EnumerateArray().ToDictionary(static pair => pair[0].GetString()!, static pair => pair[1].GetString()!, StringComparer.Ordinal);

    /// <summary>One write of the random-sequence property: its journal, its commands, and the rows its opener supersedes.</summary>
    private sealed record Step(int Journal, Command[] Commands, int Superseded)
    {
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"journal {Journal}: [{string.Join(", ", Commands.Select(static command => command.ToString()))}]{(Superseded > 0 ? $" after {Superseded} superseded" : "")}");
    }
}
