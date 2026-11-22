using Orleans.Durable.Storage.Engine.Journals;

namespace Orleans.Durable.Storage;

/// <summary>
/// A journal entry forwarded to the primary's node (orleans.md §5.5, §7.2): the journal's key, whose row key the owner
/// encodes, and the writing silo's encoded entry record verbatim, its CRC-32C, kind, expected version, deletion time, and
/// body, in an Orleans buffer (OR-I18). The owner verifies the record against the key's row key where it enters the process
/// and proposes it unchanged (JS-I5). It is not <see cref="ImmutableAttribute"/>, so Orleans' proxy pins a copy of the
/// record as each call is made, which the message holds; the callee releases the record it receives on every path.
/// </summary>
[GenerateSerializer]
internal readonly struct JournalForwardedEntry
{
    /// <summary>Gets or initializes the journal's key.</summary>
    [Id(0)]
    public string Key { get; init; }

    /// <summary>Gets or initializes the encoded entry record.</summary>
    [Id(1)]
    public OrleansBuffer Record { get; init; }
}

/// <summary>Why the owner's node refused a forwarded call before its Journals, with no effect (orleans.md §5.5, §7.2).</summary>
internal enum JournalRefusal : byte
{
    /// <summary>
    /// An argument the owner rejected: a key which is no journal key, or a page's offset outside its snapshot; the router
    /// reports <see cref="ArgumentException"/>.
    /// </summary>
    Argument = 1,

    /// <summary>
    /// A forwarded record which fails its verification against the key's row key (JS-I5); the router reports
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    Invalid = 2,
}

/// <summary>
/// The owner's definite refusal of a forwarded call before its Journals, with no effect (orleans.md §5.5, §7.2): its kind and
/// its message, which the router reports as the kind's exception. It travels in the reply, as a replica host's refusal
/// does (OR-I15, OR-I16), so every exception the router sees from a call is a transport failure or its own finding.
/// </summary>
/// <param name="Kind">The refusal's kind.</param>
/// <param name="Message">The refusal's message.</param>
internal sealed record JournalCallRefused(JournalRefusal Kind, string Message);

/// <summary>
/// The answer of <see cref="IJournalStoreGrain.WriteAsync"/> (orleans.md §5.5, §7.2, conventions.md §15): the node's
/// <see cref="LocalJournalWriteOutcome"/> cases, the Journals' outcomes with <see cref="LocalJournalWriteOutcome.Throttled"/> and
/// <see cref="LocalJournalWriteOutcome.Unavailable"/>;
/// the owner's <see cref="JournalCallRefused"/> of a key which is no journal key or a record which fails JS-I5; and
/// <see cref="NotThisNode"/>, which a misplaced node grain answers before it touches the node's state.
/// </summary>
internal readonly union JournalForwardedWriteOutcome(
    JournalWriteOutcome.Accepted,
    JournalWriteOutcome.Rejected,
    JournalWriteOutcome.ReplaceRequired,
    JournalMoved,
    JournalWriteOutcome.Fenced,
    JournalWriteOutcome.Ambiguous,
    LocalJournalWriteOutcome.Throttled,
    LocalJournalWriteOutcome.Unavailable,
    JournalCallRefused,
    NotThisNode) : IEquatable<JournalForwardedWriteOutcome>
{
    /// <inheritdoc/>
    public bool Equals(JournalForwardedWriteOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JournalForwardedWriteOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}

/// <summary>
/// The answer of <see cref="IJournalStoreGrain.ReadAsync"/> (orleans.md §5.5, §7.2, conventions.md §15): the journal's
/// <see cref="Found"/> read with its first page, <see cref="JournalMoved"/>, <see cref="Busy"/> when the owner could not
/// take the read within its deadline, the owner's <see cref="JournalCallRefused"/> of a key which is no journal key, or
/// <see cref="NotThisNode"/>.
/// </summary>
internal readonly union JournalForwardedRead(JournalForwardedRead.Found, JournalMoved, JournalForwardedRead.Busy, JournalCallRefused, NotThisNode) : IEquatable<JournalForwardedRead>
{
    /// <summary>The most bytes one page of a forwarded read carries (orleans.md §5.5).</summary>
    public const int PageBytes = 1 << 20;

    /// <inheritdoc/>
    public bool Equals(JournalForwardedRead other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JournalForwardedRead other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The journal's read on its owner (journals.md §2.3): the version of the content it returns, whether the journal
    /// exists, its content's length, the replace request the read gives, its first page of at most
    /// <see cref="PageBytes"/>, and, when more pages follow, the snapshot the owner pinned for them, from which
    /// <see cref="IJournalStoreGrain.ReadPageAsync"/> answers each in turn. The first page is in an Orleans buffer, with one
    /// releaser on each side, as a replica read's DATA (OR-I18). Two are equal when their fields and bytes are, which tests
    /// compare.
    /// </summary>
    /// <param name="Version">The version of the content the read returns.</param>
    /// <param name="Exists">Whether the journal exists.</param>
    /// <param name="ContentLength">The length of the journal's content.</param>
    /// <param name="ReplaceRequested">Whether the read asks the journal's writer for a <c>Replace</c> (journals.md §2.4).</param>
    /// <param name="FirstPage">The content's first page, all of it when it holds at most one page.</param>
    /// <param name="Snapshot">The snapshot the owner pinned for the later pages, when the content holds more than one page.</param>
    public sealed record Found(JournalVersion Version, bool Exists, long ContentLength, bool ReplaceRequested, OrleansBuffer FirstPage, Guid? Snapshot)
    {
        /// <inheritdoc/>
        public bool Equals(Found? other) =>
            other is not null
            && (Version, Exists, ContentLength, ReplaceRequested, Snapshot) == (other.Version, other.Exists, other.ContentLength, other.ReplaceRequested, other.Snapshot)
            && FirstPage.ToArray().AsSpan().SequenceEqual(other.FirstPage.ToArray());

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Version, ContentLength, Snapshot);

        /// <inheritdoc/>
        public override string ToString() =>
            $"Found {{ Version = {Version}, Exists = {Exists}, ContentLength = {ContentLength}, ReplaceRequested = {ReplaceRequested}, FirstPage = {FirstPage.Length} bytes, Snapshot = {Snapshot} }}";
    }

    /// <summary>
    /// The owner could not take the read within its deadline, waiting for a forwarded-read slot or for its snapshot's
    /// reservation, or the read's history arrived past it (orleans.md §5.5): the read had no effect and pinned nothing, and
    /// the reading silo reads again after backoff.
    /// </summary>
    public sealed record Busy
    {
        private Busy()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static Busy Instance { get; } = new();
    }
}

/// <summary>
/// The answer of <see cref="IJournalStoreGrain.ReadPageAsync"/> (orleans.md §5.5, §7.2, conventions.md §15): the
/// snapshot's <see cref="Page"/> at the offset asked for, <see cref="Expired"/> once the owner released the snapshot, the
/// owner's <see cref="JournalCallRefused"/> of an offset outside the snapshot, of kind <see cref="JournalRefusal.Argument"/>,
/// or <see cref="NotThisNode"/>.
/// </summary>
internal readonly union JournalForwardedPage(JournalForwardedPage.Page, JournalForwardedPage.Expired, JournalCallRefused, NotThisNode) : IEquatable<JournalForwardedPage>
{
    /// <inheritdoc/>
    public bool Equals(JournalForwardedPage other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JournalForwardedPage other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// A page of the snapshot's content, at least one byte and at most <see cref="JournalForwardedRead.PageBytes"/>, in an
    /// Orleans buffer with one releaser on each side (OR-I18); the last page released the snapshot. Only the reading silo
    /// knows the offset it asked for and the content's length, so it refuses a page whose length is not
    /// <c>min(PageBytes, ContentLength − offset)</c> with <see cref="InvalidDataException"/>. Two are equal when their bytes are.
    /// </summary>
    /// <param name="Bytes">The page's bytes.</param>
    public sealed record Page(OrleansBuffer Bytes)
    {
        /// <inheritdoc/>
        public bool Equals(Page? other) => other is not null && Bytes.ToArray().AsSpan().SequenceEqual(other.Bytes.ToArray());

        /// <inheritdoc/>
        public override int GetHashCode() => Bytes.Length;

        /// <inheritdoc/>
        public override string ToString() => $"Page[{Bytes.Length} bytes]";
    }

    /// <summary>
    /// The owner released the snapshot, at its deadline, at the node's stop, or with its last page; the reading silo reads the
    /// journal again from the start. A last page resent after its reply was lost finds it so, and the read starts over, which
    /// costs only liveness.
    /// </summary>
    public sealed record Expired
    {
        private Expired()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static Expired Instance { get; } = new();
    }
}
