using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using Microsoft.Extensions.Options;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Journaling;
using Orleans.Storage;

namespace Orleans.Durable.Storage;

/// <summary>
/// One journal's Orleans storage (orleans.md §5.5): it keeps the journal's version, from its last read or its last
/// <c>Accepted</c> write, sends it as each entry's expected version, and encodes each entry once, the Orleans journal bytes
/// unwrapped as its body, sending the same record on every resend through the silo's <see cref="JournalRouter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Orleans' rules for a journal storage hold: a write before the instance read the journal fails with
/// <see cref="InvalidOperationException"/>; a read after one validates the precondition, failing with
/// <see cref="InconsistentStateException"/> before it delivers any data when the journal's version changed; and every
/// write which does not complete fails, after which Orleans' journaled state manager fences itself and recovers through a
/// new instance, which reads first. A <c>Rejected</c> or <c>ReplaceRequired</c> write fails with
/// <see cref="InconsistentStateException"/>, leaving the journal unchanged, and an <c>Ambiguous</c> one with
/// <see cref="JournalWriteOutcomeUnknownException"/>, since it may yet apply. Before it sends, the storage refuses with
/// <see cref="ArgumentException"/>, without effect, an empty <c>Append</c> (as <see cref="ArgumentOutOfRangeException"/>,
/// which Orleans names), a body above <c>MaxBodyBytes</c>, and a journal whose key the archive store cannot name, as the
/// Journals' admission would. A write the silo's runtime stopped before any send took effect fails with
/// <see cref="ObjectDisposedException"/>, without effect.
/// </para>
/// <para>
/// A state may have two names: once a journal's rows drop, its primary may rebase it, so a version read or accepted before
/// the drop is rejected after it although the content is unchanged (journals.md §8.3). So a <c>Rejected</c> write reads the
/// journal once, and when the content is the instance's own (its length, existence, and 128-bit XXH3 hash, which the
/// instance keeps over its reads and accepted writes as their bytes pass), sends the write once more, conditional on the
/// version just read. A second <c>Rejected</c> or changed content leaves the rejection standing, and a read which fails
/// throws as a read does. A read through an instance which already wrote may fail with
/// <see cref="InconsistentStateException"/> although the content is unchanged, for the same reason; Orleans' manager
/// reads an instance only before any write, so that costs nothing.
/// </para>
/// <para>
/// <see cref="IsCompactionRequested"/> answers from the instance's latch: set by a <c>ReplaceRequired</c> answer, and by an
/// <c>Accepted</c> write or a read which asks for a replace; an accepted <c>Replace</c> or <c>Delete</c> which asks for none
/// clears it, and an accepted <c>Append</c> keeps it. It is the instance's own: Orleans fences the instance which met
/// <c>ReplaceRequired</c>, and the recovering instance's read asks for the replace, since a store which refuses an
/// <c>Append</c> from a key's token asks for a replace from it (HI-I5).
/// </para>
/// <para>
/// The metadata members keep <see cref="IJournalStorage"/>'s defaults, which throw <see cref="NotSupportedException"/>: the
/// Journals keeps no record of an empty journal, and no metadata beyond the cluster's format key and each journal's
/// version. Orleans' manager drives an instance one operation at a time, so its fields need no synchronization.
/// </para>
/// </remarks>
internal sealed class DurableJournalStorage : IJournalStorage
{
    private readonly string _key;
    private readonly JournalRowKey _rowKey;
    private readonly JournalRouter _router;
    private readonly LocalRouting _routing;
    private readonly int _maxBodyBytes;
    private readonly string _format;
    private readonly TimeProvider _timeProvider;
    private JournalVersion? _version;
    private ContentDigest _content;
    private XxHash128 _hash = new();
    private bool _replaceLatched;

    /// <summary>Initializes a new instance of the <see cref="DurableJournalStorage"/> class for the journal <paramref name="journal"/>.</summary>
    /// <exception cref="ArgumentException">The journal's key is no journal key: empty, malformed UTF-16, or too long.</exception>
    public DurableJournalStorage(JournalId journal, JournalRouter router, LocalRouting routing, int maxBodyBytes, string format, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _key = journal.Value;
        _rowKey = JournalRowKey.Encode(_key);
        _router = router;
        _routing = routing;
        _maxBodyBytes = maxBodyBytes;
        _format = format;
        _timeProvider = timeProvider;
    }

    /// <summary>The kinds of journal write, each one Orleans operation.</summary>
    private enum WriteKind
    {
        Append,
        Replace,
        Delete,
    }

    /// <summary>The instance's content at its version: its length, whether the journal exists, and its 128-bit XXH3 hash.</summary>
    private readonly record struct ContentDigest(long Length, bool Exists, UInt128 Hash);

    /// <inheritdoc/>
    public bool IsCompactionRequested => _replaceLatched;

    /// <inheritdoc/>
    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var read = await _router.ReadAsync(_key, _rowKey, cancellationToken).ConfigureAwait(false);
        using var content = read.Content;
        if (_version is { } expected && expected != read.Version)
        {
            throw new InconsistentStateException(
                string.Create(CultureInfo.InvariantCulture, $"Version conflict (ReadAsync): the journal '{_key}' changed since this storage instance last read or wrote it."),
                read.Version.ToString(),
                expected.ToString());
        }

        var metadata = read.Exists ? new JournalMetadata(_format, read.Version.ToString(), null) : JournalMetadata.Empty;
        var hash = new XxHash128();
        var digest = Digest(hash, content, read.Exists);
        consumer.Read(new JournalBufferReader(content.Reader, isCompleted: true), metadata);
        if (content.Length > 0)
        {
            throw new InvalidOperationException("The journal storage consumer did not read all supplied journal data.");
        }

        _version = read.Version;
        _hash = hash;
        _content = digest;
        _replaceLatched |= read.ReplaceRequested;
    }

    /// <inheritdoc/>
    public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        if (value.IsEmpty)
        {
            // Orleans' contract names this exception for an empty segment.
            throw new ArgumentOutOfRangeException(nameof(value), "An Append carries a non-empty segment.");
        }

        return WriteAsync(WriteKind.Append, value, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
        WriteAsync(WriteKind.Replace, value, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DeleteAsync(CancellationToken cancellationToken) =>
        WriteAsync(WriteKind.Delete, ReadOnlySequence<byte>.Empty, cancellationToken);

    private async ValueTask WriteAsync(WriteKind kind, ReadOnlySequence<byte> body, CancellationToken cancellationToken)
    {
        var operation = string.Create(CultureInfo.InvariantCulture, $"{kind}Async");
        var expected = _version ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{operation} requires reading the journal first."));
        if (body.Length > _maxBodyBytes)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A journal entry's body is at most MaxBodyBytes, {_maxBodyBytes} bytes, not {body.Length}."), "value");
        }

        var routing = await _routing.ReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!routing.Archive.AdmitsKey(_rowKey.Span))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The archive store cannot name the journal '{_key}'."));
        }

        using var payload = Copy(body);
        var retried = false;
        while (true)
        {
            using var record = kind switch
            {
                WriteKind.Append => JournalEntryRecord.Append(_rowKey, expected, payload),
                WriteKind.Replace => JournalEntryRecord.Replace(_rowKey, expected, payload),
                WriteKind.Delete => JournalEntryRecord.Delete(_rowKey, expected, _timeProvider.GetUtcNow()),
                _ => throw new UnreachableException(),
            };
            var outcome = await _router.WriteAsync(_key, _rowKey, record, cancellationToken).ConfigureAwait(false);
            switch (outcome.Value)
            {
                case JournalWriteOutcome.Accepted accepted:
                    _version = accepted.Version;
                    _content = Applied(kind, body);
                    _replaceLatched = accepted.ReplaceRequested || kind == WriteKind.Append && _replaceLatched;
                    return;
                case JournalWriteOutcome.Rejected when !retried && await UnchangedAsync(cancellationToken).ConfigureAwait(false) is { } current:
                    // The journal holds this instance's content under another name, as after its rows dropped (journals.md
                    // §8.3): the write is sent once more, conditional on the version just read.
                    expected = current;
                    retried = true;
                    continue;
                case JournalWriteOutcome.Rejected rejected:
                    throw Inconsistent(operation, rejected.CurrentVersion.ToString(), expected, "a state the journal held conflicts with this storage instance's version");
                case JournalWriteOutcome.ReplaceRequired:
                    _replaceLatched = true;
                    throw Inconsistent(operation, expected.ToString(), expected, "the archive store takes no Append of the journal until a Replace");
                case JournalWriteOutcome.Ambiguous:
                    throw new JournalWriteOutcomeUnknownException(_key, operation);
                case JournalMoved:
                    // The router's stopped answer: the silo's runtime stopped before any send took effect.
                    throw new ObjectDisposedException(nameof(JournalRouter), string.Create(CultureInfo.InvariantCulture, $"The silo's journal runtime stopped; {operation} of the journal '{_key}' had no effect."));
                default:
                    throw new UnreachableException();
            }
        }
    }

    /// <summary>
    /// Reads the journal after a write was rejected and returns the version the read names its state by when the state's
    /// content is the instance's, or null when it is not, and the rejection stands. A read which fails throws as any read
    /// does: the router reads again past <c>Moved</c> and unavailability, so only corruption, a reply it cannot decode, the
    /// owner's refusal, and cancellation end it.
    /// </summary>
    private async ValueTask<JournalVersion?> UnchangedAsync(CancellationToken cancellationToken)
    {
        var read = await _router.ReadAsync(_key, _rowKey, cancellationToken).ConfigureAwait(false);
        using var content = read.Content;
        if (Digest(new XxHash128(), content, read.Exists) != _content)
        {
            return null;
        }

        _replaceLatched |= read.ReplaceRequested;
        return read.Version;
    }

    /// <summary>Returns the digest of the content after an accepted write of <paramref name="kind"/> with <paramref name="body"/>.</summary>
    private ContentDigest Applied(WriteKind kind, ReadOnlySequence<byte> body)
    {
        if (kind != WriteKind.Append)
        {
            _hash.Reset();
        }

        foreach (var segment in body)
        {
            _hash.Append(segment.Span);
        }

        return kind switch
        {
            WriteKind.Append => new(_content.Length + body.Length, Exists: true, _hash.GetCurrentHashAsUInt128()),
            WriteKind.Replace => new(body.Length, Exists: true, _hash.GetCurrentHashAsUInt128()),
            WriteKind.Delete => new(0, Exists: false, _hash.GetCurrentHashAsUInt128()),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Returns the digest of a read's content, hashed in place without a copy into <paramref name="hash"/>, which then
    /// holds it, so accepted <c>Append</c>s extend it.
    /// </summary>
    private static ContentDigest Digest(XxHash128 hash, OrleansBufferWriter content, bool exists)
    {
        var slice = content.Reader.PeekSlice(content.Length);
        try
        {
            foreach (var segment in slice.AsReadOnlySequence())
            {
                hash.Append(segment.Span);
            }
        }
        finally
        {
            slice.Dispose();
        }

        return new(content.Length, exists, hash.GetCurrentHashAsUInt128());
    }

    private InconsistentStateException Inconsistent(string operation, string? storedEtag, JournalVersion expected, string reason) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Version conflict ({operation}): {reason} (journal '{_key}')."), storedEtag, expected.ToString());

    /// <summary>Copies <paramref name="body"/>, which Orleans owns, into a buffer from Clog's page pool, which the caller owns.</summary>
    private static ArcBuffer Copy(ReadOnlySequence<byte> body)
    {
        if (body.IsEmpty)
        {
            return default;
        }

        using var writer = new ArcBufferWriter(checked((int)body.Length));
        writer.Write(body);
        return writer.ConsumeSlice(writer.Length);
    }
}

/// <summary>
/// The silo's Orleans journal storage provider (orleans.md §2, §5.4, §5.5): one <see cref="DurableJournalStorage"/> per
/// <see cref="JournalId"/>, each journal keyed by its ID's value, over the silo's <see cref="JournalRouter"/>; the silo's
/// <see cref="JournalReplacement"/> tracks the activation each is created for.
/// </summary>
internal sealed class DurableJournalStorageProvider(JournalRouter router, LocalRouting routing, JournalReplacement replacement, DurableStorageOptions options, IOptions<JournaledStateManagerOptions> journaling, TimeProvider timeProvider) : IJournalStorageProvider
{
    /// <inheritdoc/>
    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var storage = new DurableJournalStorage(journalId, router, routing, options.Journals.MaxBodyBytes, journaling.Value.JournalFormatKey, timeProvider);
        replacement.Track(journalId);
        return storage;
    }
}
