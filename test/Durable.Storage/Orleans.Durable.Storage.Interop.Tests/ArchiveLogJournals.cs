using System.Buffers;
using System.Globalization;
using System.Text;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Interop.Tests;

/// <summary>A command of a test's write with its body: an <c>Append</c>'s or a <c>Replace</c>'s content, or a <c>Delete</c>'s tombstone.</summary>
internal sealed record Command(RowKind Kind, byte[] Body)
{
    public static Command Append(string body) => new(RowKind.Append, Encoding.UTF8.GetBytes(body));

    public static Command Replace(string body) => new(RowKind.Replace, Encoding.UTF8.GetBytes(body));

    public static Command Delete(byte[] tombstone) => new(RowKind.Delete, tombstone);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind}({Convert.ToHexStringLower(Body)})");
}

/// <summary>
/// A journal's chain: its identity, its row key, the token its next write starts from, and the fold of its writes, the
/// interop tests' oracle: an <c>Append</c> extends the content, a <c>Replace</c> replaces it with its body, and a
/// <c>Delete</c>, a tombstoned one or an <c>Expired</c>, empties it.
/// </summary>
internal sealed class Journal(string id)
{
    public string Id { get; } = id;

    /// <summary>Gets the journal's row key, as the Journals write it, whose log the composition names <see cref="Id"/>.</summary>
    public byte[] Key { get; } = JournalRowKey.Encode(id).Memory.ToArray();

    /// <summary>Gets the token the journal's next write starts from, its last write's plan.</summary>
    public RowToken Token { get; private set; }

    /// <summary>Gets the fold of the journal's writes.</summary>
    public byte[] Fold { get; private set; } = [];

    /// <summary>Gets a value indicating whether a write of the journal landed, so its log exists.</summary>
    public bool Exists { get; private set; }

    /// <summary>Gets the number of the journal's writes, which numbers each write's segment.</summary>
    public long Writes { get; private set; }

    /// <summary>Takes the journal past a write of <paramref name="commands"/> which the log holds, whose plan is <paramref name="token"/>.</summary>
    public void Advance(RowToken token, IEnumerable<Command> commands)
    {
        Token = token;
        Fold = FoldOf(Fold, commands);
        Exists = true;
        Writes++;
    }

    public static byte[] FoldOf(byte[] before, IEnumerable<Command> commands)
    {
        var content = before;
        foreach (var command in commands)
        {
            content = command.Kind switch
            {
                RowKind.Append => [.. content, .. command.Body],
                RowKind.Replace => command.Body,
                RowKind.Delete => [],
                _ => throw new ArgumentOutOfRangeException(nameof(commands)),
            };
        }

        return content;
    }
}

/// <summary>
/// Clog's side of a test, over the services' container: its store, in the composition's names
/// (<see cref="OrleansJournalLogNames"/>), its writer, and the tokens <see cref="LogArchiveStore"/> plans; and plain Orleans
/// with read-only access, which reads what Clog wrote.
/// </summary>
internal sealed class ArchiveLogJournals : IAsyncDisposable
{
    /// <summary>Orleans' manifest of the write-ahead blob: the only metadata names it reads.</summary>
    private static readonly string[] OrleansManifest = [AzureArchiveLogMetadata.Format, AzureArchiveLogMetadata.Checkpoint, AzureArchiveLogMetadata.CheckpointOffset, AzureArchiveLogMetadata.WalGeneration];

    /// <summary>The header of a <c>Put Blob</c> which names the blob's type.</summary>
    private const string BlobTypeHeader = "x-ms-blob-type";

    /// <summary>The blob type of a write-ahead blob, which an opener's second request re-creates.</summary>
    private const string AppendBlobType = "AppendBlob";

    private readonly InteropServices _services;
    private readonly string _prefix;
    private readonly ArchiveLogWriter _writer;
    private readonly LogArchiveStore _archive;
    private int _cases;

    /// <param name="services">The test's clients.</param>
    /// <param name="layout">The store's layout.</param>
    /// <param name="format">The journal format of plain Orleans and the store; Orleans' default when null.</param>
    /// <param name="prefix">The cluster's store prefix, under which Clog writes and plain Orleans reads; none when null.</param>
    public ArchiveLogJournals(InteropServices services, ArchiveLogLayout layout, string? format = null, string? prefix = null)
    {
        _services = services;
        _prefix = prefix is null ? string.Empty : prefix + "/";
        Orleans = PlainOrleans.Over(services.ReadOnly, format, prefix);
        Store = new AzureArchiveLogStore(services.Owner, Orleans.Format, layout, _prefix);
        _archive = new LogArchiveStore(Store, new AzureBlobObjectStore(services.Owner, _prefix), OrleansJournalLogNames.Names);
        _writer = new ArchiveLogWriter(Store, new FakeTimeProvider(), new ArchiveLogWriterOptions { MaxRetries = 0 });
    }

    public AzureArchiveLogStore Store { get; }

    public PlainOrleans Orleans { get; }

    /// <summary>Gets Clog's store of the logs, which admits the keys whose logs it can name.</summary>
    public LogArchiveStore Archive => _archive;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Writes one write of <paramref name="journal"/> from its token, which the log must hold, and folds it.</summary>
    public Task WriteAsync(Journal journal, params Command[] commands) => WriteAsync(journal, 0, commands);

    public async Task WriteAsync(Journal journal, int superseded, Command[] commands)
    {
        var (write, content) = WriteOf(journal, superseded, commands);
        var outcome = await _writer.WriteAsync(OrleansJournalLogNames.Names.Of(journal.Key), write, content, Token);
        Assert.IsType<ArchiveLogConditionOutcome.Held>(outcome.Value);
        _services.AssertNoViolations();
        journal.Advance(_archive.Plan(write), commands);
    }

    /// <summary>
    /// Writes an opener of <paramref name="journal"/> whose write-ahead blob's re-creation the gate refuses, so only its
    /// checkpoint lands: the opener torn between its two requests, as a drain which crashed between them leaves it. The
    /// write fails with the gate's refusal, after the client's retries, and the journal's chain is unchanged.
    /// </summary>
    public async Task TearAsync(Journal journal, Command[] commands)
    {
        var (write, content) = WriteOf(journal, 0, commands);
        _services.Gate.Refuses = static request => request.Method == HttpMethod.Put && request.Headers.TryGetValues(BlobTypeHeader, out var types) && types.Contains(AppendBlobType, StringComparer.Ordinal);
        try
        {
            var refused = await Assert.ThrowsAsync<AggregateException>(async () => await _writer.WriteAsync(OrleansJournalLogNames.Names.Of(journal.Key), write, content, Token));
            Assert.All(refused.InnerExceptions, static exception => Assert.IsType<GateRefusedException>(exception.InnerException));
        }
        finally
        {
            _services.Gate.Refuses = null;
        }

        _services.AssertNoViolations();
    }

    /// <summary>
    /// Asserts that plain Orleans reads <paramref name="journal"/> as its fold: as an existing journal of the store's
    /// format once a write landed, and as an absent one before; and that it reads no metadata name but its own manifest's,
    /// so Clog's reserved <c>durable_*</c> names are among the journal's caller-owned properties (pinned, archive-logs.md §7.3).
    /// </summary>
    public async Task<OrleansRead> AssertReadsAsync(Journal journal)
    {
        var read = await Orleans.ReadAsync(journal.Id);
        _services.AssertNoViolations();
        Assert.True(read.Content.AsSpan().SequenceEqual(journal.Fold), $"Plain Orleans read '{journal.Id}' as {Hex(read.Content)}, not its fold {Hex(journal.Fold)}.");
        Assert.Equal(journal.Exists, read.Metadata is not null);
        Assert.Equal(journal.Exists ? Orleans.Format : null, read.Completed.FormatKey);
        if (read.Metadata is { } metadata)
        {
            var names = (await _services.Owner.GetBlobClient(AzureArchiveLogMetadata.WalName(journal.Id, _prefix)).GetPropertiesAsync(cancellationToken: Token)).Value.Metadata.Keys;
            Assert.Equal(
                names.Where(static name => !OrleansManifest.Contains(name, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase),
                metadata.Properties.Keys.Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }

        return read;
    }

    /// <summary>Returns the number of the next case of a property, which tells its journals from earlier cases'.</summary>
    public int NextCase() => Interlocked.Increment(ref _cases);

    /// <summary>Returns the content of the checkpoint of <paramref name="journal"/>'s generation <paramref name="generation"/>.</summary>
    public async Task<byte[]> CheckpointAsync(Journal journal, ulong generation) =>
        (await _services.Owner.GetBlobClient(AzureArchiveLogMetadata.CheckpointName(journal.Id, generation, _prefix)).DownloadContentAsync(Token)).Value.Content.ToArray();

    /// <summary>Returns <paramref name="journal"/>'s write-ahead blob's <c>wal_generation</c>.</summary>
    public async Task<string> WalGenerationAsync(Journal journal) =>
        (await _services.Owner.GetBlobClient(AzureArchiveLogMetadata.WalName(journal.Id, _prefix)).GetPropertiesAsync(cancellationToken: Token)).Value.Metadata[AzureArchiveLogMetadata.WalGeneration];

    /// <summary>Returns the committed blocks of <paramref name="journal"/>'s write-ahead blob.</summary>
    public async Task<int> CommittedBlocksAsync(Journal journal) =>
        (await _services.Owner.GetAppendBlobClient(AzureArchiveLogMetadata.WalName(journal.Id, _prefix)).GetPropertiesAsync(cancellationToken: Token)).Value.BlobCommittedBlockCount;

    public ValueTask DisposeAsync() => Orleans.DisposeAsync();

    private static string Hex(byte[] content) => content.Length <= 64 ? Convert.ToHexStringLower(content) : $"{content.Length} bytes";

    private static (ArchiveWrite Write, ReadOnlySequence<byte> Content) WriteOf(Journal journal, int superseded, Command[] commands) =>
        (new ArchiveWrite(
            new LedgerSegmentId(new LedgerId(1), new Slot(journal.Writes)),
            journal.Token,
            [.. commands.Select(static command => ArchiveCommand.Of(command.Kind, command.Body))],
            superseded),
        new ReadOnlySequence<byte>([.. commands.Where(static command => command.Kind != RowKind.Delete).SelectMany(static command => command.Body)]));
}
