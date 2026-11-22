using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The kind of a <see cref="JournalInput"/>: a Journals write or read (journals.md §2).
/// </summary>
internal enum JournalOperationKind
{
    /// <summary>An <c>Append</c> of a payload at an expected version.</summary>
    Append,

    /// <summary>A <c>Replace</c> with a snapshot at an expected version.</summary>
    Replace,

    /// <summary>A <c>Delete</c> at an expected version.</summary>
    Delete,

    /// <summary>The owner's <c>Expired</c> of a journal deleted at its expected version (§8.6).</summary>
    Expire,

    /// <summary><c>ReadAsync(key)</c> with its content.</summary>
    Read,
}

/// <summary>
/// The outcome of a journal operation: a write's (journals.md §2.2), or whether a read was answered.
/// </summary>
internal enum JournalOutcome
{
    /// <summary>A write accepted at its expected version, or a read answered with the journal's state.</summary>
    Accepted,

    /// <summary>The entry applies to no state the owner's applied state and in-flight entries give; no effect.</summary>
    Rejected,

    /// <summary>An <c>Append</c> the archive store's limits refuse, decided from the owner's applied state; no effect.</summary>
    ReplaceRequired,

    /// <summary>Not served by the answering process; no effect.</summary>
    Moved,

    /// <summary>The owner is under resource pressure; no effect.</summary>
    Throttled,

    /// <summary>The entry is not in the ledger and never will be; no effect.</summary>
    Fenced,

    /// <summary>A write which may have been accepted, or a read without an answer.</summary>
    Ambiguous,
}

/// <summary>
/// An operation on one journal.
/// </summary>
/// <param name="Key">The journal's key; the journal model partitions histories by it.</param>
/// <param name="Kind">The operation.</param>
/// <param name="Expected">A write's expected version, in its text form (journals.md §4.3); <see langword="null"/> for a read.</param>
/// <param name="Payload">An append's payload or a replace's snapshot.</param>
internal readonly record struct JournalInput(string Key, JournalOperationKind Kind, string? Expected, ReadOnlyMemory<byte> Payload)
{
    /// <summary>Returns an <c>Append</c> of <paramref name="payload"/> at <paramref name="expected"/>.</summary>
    public static JournalInput Append(string key, string expected, ReadOnlyMemory<byte> payload) => Write(key, JournalOperationKind.Append, expected, payload);

    /// <summary>Returns a <c>Replace</c> with <paramref name="snapshot"/> at <paramref name="expected"/>.</summary>
    public static JournalInput Replace(string key, string expected, ReadOnlyMemory<byte> snapshot) => Write(key, JournalOperationKind.Replace, expected, snapshot);

    /// <summary>Returns a <c>Delete</c> at <paramref name="expected"/>.</summary>
    public static JournalInput Delete(string key, string expected) => Write(key, JournalOperationKind.Delete, expected, default);

    /// <summary>Returns the owner's <c>Expired</c> of the journal deleted at <paramref name="expected"/>.</summary>
    public static JournalInput Expire(string key, string expected) => Write(key, JournalOperationKind.Expire, expected, default);

    /// <summary>Returns <c>ReadAsync(key)</c>.</summary>
    public static JournalInput Read(string key) => new(key, JournalOperationKind.Read, null, default);

    private static JournalInput Write(string key, JournalOperationKind kind, string expected, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expected);
        return new JournalInput(key, kind, expected, payload);
    }
}

/// <summary>
/// The response to a <see cref="JournalInput"/>.
/// </summary>
/// <param name="Outcome">The write's outcome, or whether the read was answered.</param>
/// <param name="Version">
/// An accepted write's version after its entry, a rejected write's reported <c>CurrentVersion</c>, or an answered read's
/// version, in its text form; otherwise <see langword="null"/>.
/// </param>
/// <param name="Exists">Whether an answered read found the journal.</param>
/// <param name="Content">An answered read's content: its opener's snapshot and every later append's payload, in order.</param>
/// <param name="Deposed">
/// Whether a <c>Rejected</c> or <c>ReplaceRequired</c> came from a deposed owner, whose ledger the map no longer listed open
/// when it answered: its applied state may be stale (journals.md §2.2). A test-only flag the simulation takes from the map.
/// </param>
internal readonly record struct JournalOutput(JournalOutcome Outcome, string? Version, bool Exists, ReadOnlyMemory<byte> Content, bool Deposed = false)
{
    /// <summary>Gets an <c>Append</c> the archive store's limits refuse.</summary>
    public static JournalOutput ReplaceRequired { get; } = new(JournalOutcome.ReplaceRequired, null, Exists: false, default);

    /// <summary>Gets a <c>Moved</c> write or read.</summary>
    public static JournalOutput Moved { get; } = new(JournalOutcome.Moved, null, Exists: false, default);

    /// <summary>Gets a throttled write.</summary>
    public static JournalOutput Throttled { get; } = new(JournalOutcome.Throttled, null, Exists: false, default);

    /// <summary>Gets a fenced write.</summary>
    public static JournalOutput Fenced { get; } = new(JournalOutcome.Fenced, null, Exists: false, default);

    /// <summary>
    /// Gets an ambiguous write, or a read without an answer. Record an ambiguous write with
    /// <see cref="HistoryOperation.Unbounded"/> as its response time: its entry may be decided after the response.
    /// </summary>
    public static JournalOutput Ambiguous { get; } = new(JournalOutcome.Ambiguous, null, Exists: false, default);

    /// <summary>Returns a write accepted with the journal's version after it.</summary>
    public static JournalOutput Accepted(string version) => new(JournalOutcome.Accepted, version, Exists: false, default);

    /// <summary>Returns a rejected write, which reported <paramref name="currentVersion"/>, from a deposed owner when <paramref name="deposed"/>.</summary>
    public static JournalOutput Rejected(string currentVersion, bool deposed = false) => new(JournalOutcome.Rejected, currentVersion, Exists: false, default, deposed);

    /// <summary>Returns an <c>Append</c> the archive store's limits refuse, from a deposed owner when <paramref name="deposed"/>.</summary>
    public static JournalOutput ReplaceRequiredBy(bool deposed) => new(JournalOutcome.ReplaceRequired, null, Exists: false, default, deposed);

    /// <summary>Returns an answered read: whether the journal exists, its version, and its content.</summary>
    public static JournalOutput Read(bool exists, string version, ReadOnlyMemory<byte> content) => new(JournalOutcome.Accepted, version, exists, content);
}

/// <summary>
/// The state of one journal in <see cref="JournalModel"/>: the fold of its accepted entries (journals.md §2.1), its
/// names, and every version any of its states has had as a name.
/// </summary>
internal sealed class JournalModelState : IEquatable<JournalModelState>
{
    private readonly PayloadNode? _payloads;

    private JournalModelState(ImmutableHashSet<string> names, string? version, bool deleted, PayloadNode? payloads, ImmutableHashSet<string> held, bool expired = false)
    {
        Expired = expired;
        Names = names;
        Version = version;
        Deleted = deleted;
        _payloads = payloads;
        Held = held;
    }

    /// <summary>Gets an absent journal: named <see cref="JournalModel.Origin"/>, which it has held.</summary>
    public static JournalModelState Absent { get; } = new(
        ImmutableHashSet.Create(StringComparer.Ordinal, JournalModel.Origin),
        JournalModel.Origin,
        deleted: false,
        payloads: null,
        ImmutableHashSet.Create(StringComparer.Ordinal, JournalModel.Origin));

    /// <summary>
    /// Gets the state's names: the version its entry gave it and each rebased version an answer has named it by since
    /// (journals.md §2.1); empty while unknown, after an ambiguous write took effect, until an answer names the state.
    /// </summary>
    public ImmutableHashSet<string> Names { get; }

    /// <summary>Gets the state's newest name, or <see langword="null"/> while its names are unknown.</summary>
    public string? Version { get; }

    /// <summary>Gets a value indicating whether the journal is deleted: its opener is a <c>Delete</c>, and no append follows.</summary>
    public bool Deleted { get; }

    /// <summary>Gets a value indicating whether the journal's opener is an <c>Expired</c>, after which the LSM may forget it (journals.md §8.3).</summary>
    public bool Expired { get; }

    /// <summary>Gets a value indicating whether the journal exists: it has content or a <c>Replace</c> opener (journals.md §2.1).</summary>
    public bool Exists => _payloads is not null;

    /// <summary>Gets every version which has named a state of the journal, the current state's names among them.</summary>
    public ImmutableHashSet<string> Held { get; }

    /// <summary>
    /// Gets the <c>Append</c>s since the journal's opener, or since its first entry when it has none: what an archive
    /// store's per-generation limits count (journals.md §2.2, §8.1).
    /// </summary>
    public int Appends => _payloads?.Appends ?? 0;

    /// <summary>
    /// Gets the length of the content's base, the opener's snapshot when it is a <c>Replace</c>, and otherwise 0, against
    /// which a read's replace request measures the content (journals.md §8.7).
    /// </summary>
    public int BaseLength => _payloads?.BaseLength ?? 0;

    /// <summary>Gets the content a read returns: the opener's snapshot when it is a <c>Replace</c>, then each later append's payload.</summary>
    public ImmutableArray<ReadOnlyMemory<byte>> Payloads
    {
        get
        {
            var payloads = new ReadOnlyMemory<byte>[_payloads?.Count ?? 0];
            for (var node = _payloads; node is not null; node = node.Previous)
            {
                payloads[node.Count - 1] = node.Payload;
            }

            return ImmutableCollectionsMarshal.AsImmutableArray(payloads);
        }
    }

    /// <inheritdoc/>
    public bool Equals(JournalModelState? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && Names.SetEquals(other.Names)
            && Expired == other.Expired
            && Deleted == other.Deleted
            && PayloadNode.Equal(_payloads, other._payloads)
            && Held.SetEquals(other.Held);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as JournalModelState);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return HashCode.Combine(SetHash(Names), Deleted, Expired, _payloads?.Hash ?? 0, SetHash(Held));
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var description = new StringBuilder(Names.IsEmpty ? "?" : string.Join("|", Names.Order(StringComparer.Ordinal)));
        if (Deleted)
        {
            return description.Append(" deleted").ToString();
        }

        if (Exists)
        {
            _ = description.Append(" [").AppendJoin(", ", Payloads.Select(static payload => JournalModel.Describe(payload.Span))).Append(']');
        }

        return description.ToString();
    }

    /// <summary>Gets the content a read returns: <see cref="Payloads"/>, concatenated.</summary>
    public byte[] Content => [.. Payloads.SelectMany(static payload => payload.ToArray())];

    /// <summary>Returns whether a read's content is this journal's.</summary>
    internal bool HasContent(ReadOnlySpan<byte> content)
    {
        var end = content.Length;
        for (var node = _payloads; node is not null; node = node.Previous)
        {
            var payload = node.Payload.Span;
            if (end < payload.Length || !content[(end - payload.Length)..end].SequenceEqual(payload))
            {
                return false;
            }

            end -= payload.Length;
        }

        return end == 0;
    }

    /// <summary>
    /// Returns the state <paramref name="version"/> names, which an answer reported: this state, by one of its names;
    /// this state, its content unchanged, by a version which never named a state, which becomes one of its names, as when
    /// a read names an unknown state or a rebased one (journals.md §8.3); a new lifetime, absent at <c>Origin</c> with
    /// nothing held before it, by <c>Origin</c> once an <c>Expired</c> is the opener, which the LSM may then forget
    /// entirely (JS-I10); or <see langword="null"/>, when the version named an earlier state, since no version names two.
    /// </summary>
    internal JournalModelState? At(string version) =>
        Names.Contains(version) ? this
        : Expired && version == JournalModel.Origin ? Absent
        : Held.Contains(version) ? null
        : new JournalModelState(Names.Add(version), version, Deleted, _payloads, Held.Add(version), Expired);

    /// <summary>Returns the state after an entry which applies, named <paramref name="version"/>, or by unknown names.</summary>
    internal JournalModelState Apply(JournalInput input, string? version)
    {
        var held = version is null ? Held : Held.Add(version);
        var names = version is null ? Names.Clear() : Names.Clear().Add(version);
        return input.Kind switch
        {
            JournalOperationKind.Append => new JournalModelState(names, version, deleted: false, new PayloadNode(input.Payload, _payloads), held),
            JournalOperationKind.Replace => new JournalModelState(names, version, deleted: false, new PayloadNode(input.Payload, previous: null, opener: true), held),
            JournalOperationKind.Delete => new JournalModelState(names, version, deleted: true, payloads: null, held),
            JournalOperationKind.Expire => new JournalModelState(names, version, deleted: false, payloads: null, held, expired: true),
            JournalOperationKind.Read => throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "Only writes change a journal."),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "Undefined journal operation."),
        };
    }

    /// <summary>Returns a hash of <paramref name="set"/> independent of its order, which a set does not define.</summary>
    private static int SetHash(ImmutableHashSet<string> set)
    {
        var hash = 0;
        foreach (var item in set)
        {
            hash ^= StringComparer.Ordinal.GetHashCode(item);
        }

        return hash;
    }

    /// <summary>
    /// The content's payloads from the opener, newest first and sharing their tails, so an append allocates one node.
    /// A <c>Replace</c> opener's snapshot is a node of its own, an empty one included, so the journal exists.
    /// </summary>
    private sealed class PayloadNode
    {
        public PayloadNode(ReadOnlyMemory<byte> payload, PayloadNode? previous, bool opener = false)
        {
            Payload = payload;
            Previous = previous;
            Count = (previous?.Count ?? 0) + 1;
            Appends = opener ? 0 : (previous?.Appends ?? 0) + 1;
            BaseLength = opener ? payload.Length : previous?.BaseLength ?? 0;
            var hash = default(HashCode);
            hash.Add(previous?.Hash ?? 0);
            hash.Add(opener);
            hash.AddBytes(payload.Span);
            Hash = hash.ToHashCode();
        }

        public ReadOnlyMemory<byte> Payload { get; }

        public PayloadNode? Previous { get; }

        public int Count { get; }

        /// <summary>Gets the appends from the opener through this node.</summary>
        public int Appends { get; }

        /// <summary>Gets the opener's snapshot length, or 0 when the opener is not a <c>Replace</c>.</summary>
        public int BaseLength { get; }

        public int Hash { get; }

        public static bool Equal(PayloadNode? left, PayloadNode? right)
        {
            for (; left is not null && right is not null; left = left.Previous, right = right.Previous)
            {
                if (ReferenceEquals(left, right))
                {
                    return true;
                }

                if (left.Count != right.Count || left.Hash != right.Hash || !left.Payload.Span.SequenceEqual(right.Payload.Span))
                {
                    return false;
                }
            }

            return left is null && right is null;
        }
    }
}

/// <summary>
/// The sequential specification of one Journals journal (journals.md §2), with opaque versions: writes admitted by
/// version, reads which return the fold of the accepted entries with its version, and outcomes without effect. Histories
/// partition by journal key, since journals are independent.
/// </summary>
/// <remarks>
/// <para>
/// Versions are opaque and compared only for equality, and each names one state of the journal, which may have several
/// names (journals.md §2.1). An absent journal is named <see cref="Origin"/>. An <c>Append</c>, <c>Replace</c>, or
/// <c>Delete</c> applies only when its expected version names the journal's state, and an <c>Expired</c> only when the
/// journal is also deleted (JS-I1). An accepted write must apply at a version the journal has held, since a writer
/// writes only at a version an answer gave it, and the version it reports, its new state's one name, is one which never
/// named a state (JS-I10). An ambiguous write applies wherever the checker linearizes it and the entry applies, leaving
/// the journal's names unknown until an answer names its state; recorded with an unbounded response time, it may also be
/// linearized after everything, which is no effect.
/// </para>
/// <para>
/// A rebase (journals.md §8.3) is a step of the environment: once every ledger which held the journal's rows has dropped,
/// a read names its state, its content unchanged, by a version which never named a state, while a primary holding the
/// key warm still names it by its earlier name. The model takes an answer which reports such a version, or a write which
/// names one as its expected version, as that step, which adds the version to the state's names, and never takes a
/// version which named an earlier state. The simulation's oracles judge when a rebase may happen and which base it takes:
/// every earlier row of the key dropped, and the store's version base. Once an <c>Expired</c> is the opener, the LSM may
/// forget the key entirely, a second environment step: <c>Origin</c> then names the absent state, which starts a new
/// lifetime with nothing held before it, so its <c>Origin</c>-based versions may repeat the earlier lifetime's (JS-I10).
/// </para>
/// <para>
/// <c>Rejected</c>, <c>ReplaceRequired</c>, <c>Moved</c>, <c>Throttled</c>, and <c>Fenced</c> have no effect (JS-I4). A
/// rejection reports its owner's applied state's <c>CurrentVersion</c> (journals.md §2.2): a name of the current state,
/// a fresh one binding as a rebase, and never the write's own expected version. A deposed owner, whose ledger the map no
/// longer lists open, answers from a possibly stale applied state, so only for an output flagged
/// <see cref="JournalOutput.Deposed"/> does the model accept, as a hint which binds nothing, any version the journal held
/// or a rebase of that stale state no answer named, other than the write's own; the white-box oracle and the version
/// checker judge it against the deposed owner's state. The model's premise is that a writer writes only at a version an
/// answer bound: an acceptance, a strict rejection, or a read; a deposed rejection's hint binds nothing, so a writer reads
/// before it writes again, and an entry is accepted only at a version the journal held. An <c>Expired</c> is also
/// rejected for its retention or its key's entries in flight, so only the report is checked. <c>ReplaceRequired</c>
/// answers only an <c>Append</c>, decided at its owner's applied state after the version check: its expected version
/// names the current state, or, deposed, one the journal held; the white-box oracle judges the store's limits.
/// </para>
/// <para>
/// An answered read returns the state: <c>Exists</c>, the version, and the content (JS-I3). A read answered
/// <c>Moved</c>, or without an answer, has no effect; a read is never rejected, throttled, or fenced (§2.3).
/// </para>
/// </remarks>
internal sealed class JournalModel : LinearizabilityModel<JournalModelState, JournalInput, JournalOutput>
{
    /// <summary><c>Origin</c>'s text, an absent journal's version (journals.md §4.3).</summary>
    public const string Origin = "AAA";

    private const int DescribedPayloadBytes = 8;

    private JournalModel()
    {
    }

    /// <summary>Gets the model, which holds no state.</summary>
    public static JournalModel Instance { get; } = new();

    /// <inheritdoc/>
    public override IEqualityComparer<JournalInput> PartitionComparer { get; } = new KeyComparer();

    /// <inheritdoc/>
    public override JournalModelState Init() => JournalModelState.Absent;

    /// <inheritdoc/>
    public override bool Step(JournalModelState state, JournalInput input, JournalOutput output, out JournalModelState next)
    {
        ArgumentNullException.ThrowIfNull(state);
        next = state;
        if (input.Kind == JournalOperationKind.Read)
        {
            return output.Outcome switch
            {
                JournalOutcome.Moved or JournalOutcome.Ambiguous => true,
                JournalOutcome.Accepted => ReadsAs(state, output, out next),
                JournalOutcome.Rejected or JournalOutcome.ReplaceRequired or JournalOutcome.Throttled or JournalOutcome.Fenced => false,
                _ => throw new ArgumentOutOfRangeException(nameof(output), output.Outcome, "Undefined journal outcome."),
            };
        }

        var expected = input.Expected ?? throw new ArgumentOutOfRangeException(nameof(input), "A write names its expected version.");
        switch (output.Outcome)
        {
            case JournalOutcome.Accepted:
                // A writer writes only at a version an answer gave it, which the journal has therefore held.
                if (output.Version is not { } version || !state.Held.Contains(expected) || state.At(expected) is not { } at || !Applies(at, input) || at.Held.Contains(version))
                {
                    return false;
                }

                next = at.Apply(input, version);
                return true;
            case JournalOutcome.Ambiguous:
                // A write's expected version names the version it applies at, as an answer's would: one it never held
                // binds an unknown version or is a rebase.
                if (state.At(expected) is { } bound && Applies(bound, input))
                {
                    next = bound.Apply(input, version: null);
                }

                return true;
            case JournalOutcome.Rejected:
                if (output.Version is not { } current
                    || input.Kind != JournalOperationKind.Expire && string.Equals(expected, current, StringComparison.Ordinal))
                {
                    return false;
                }

                // A rejection reports a name of the current state, a fresh one binding as a rebase does. A deposed owner's,
                // from its stale applied state, is a hint: a version the journal held, or a rebase of that state no answer
                // named, which binds nothing; the white-box oracle and the version checker judge it against its own state.
                return output.Deposed || NamesCurrent(state, current, out next);
            case JournalOutcome.ReplaceRequired:
                // Decided at the owner's applied state, after its version check: the Append's expected version names the
                // current state, or, from a deposed owner, a version the journal held.
                return input.Kind == JournalOperationKind.Append
                    && (output.Deposed ? state.Held.Contains(expected) : NamesCurrent(state, expected, out next));
            case JournalOutcome.Moved or JournalOutcome.Throttled or JournalOutcome.Fenced:
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(output), output.Outcome, "Undefined journal outcome.");
        }
    }

    /// <inheritdoc/>
    public override string DescribeOperation(JournalInput input, JournalOutput output)
    {
        var operation = input.Kind switch
        {
            JournalOperationKind.Append => Invariant($"append({input.Key}, {input.Expected}, {Describe(input.Payload.Span)})"),
            JournalOperationKind.Replace => Invariant($"replace({input.Key}, {input.Expected}, {Describe(input.Payload.Span)})"),
            JournalOperationKind.Delete => Invariant($"delete({input.Key}, {input.Expected})"),
            JournalOperationKind.Expire => Invariant($"expire({input.Key}, {input.Expected})"),
            JournalOperationKind.Read => Invariant($"read({input.Key})"),
            _ => input.Kind.ToString(),
        };
        return Invariant($"{operation} -> {DescribeResponse(input.Kind, output)}");
    }

    /// <summary>
    /// Describes a payload as lowercase hexadecimal, truncated after <see cref="DescribedPayloadBytes"/> bytes.
    /// </summary>
    internal static string Describe(ReadOnlySpan<byte> payload) =>
        payload.Length <= DescribedPayloadBytes
            ? Convert.ToHexStringLower(payload)
            : Invariant($"{Convert.ToHexStringLower(payload[..DescribedPayloadBytes])}…({payload.Length} bytes)");

    /// <summary>
    /// Returns whether <paramref name="version"/> names <paramref name="state"/>: one of its names, or a fresh version,
    /// which becomes one, or <c>Origin</c> after an <c>Expired</c>, which starts a new lifetime; the state it names.
    /// </summary>
    private static bool NamesCurrent(JournalModelState state, string version, out JournalModelState next)
    {
        var at = state.At(version);
        next = at ?? state;
        return at is not null;
    }

    /// <summary>Returns whether an entry applies to <paramref name="state"/>'s content, apart from its version (journals.md §2.1).</summary>
    private static bool Applies(JournalModelState state, JournalInput input) => input.Kind != JournalOperationKind.Expire || state.Deleted;

    /// <summary>Returns whether an answered read is <paramref name="state"/>'s, at its version or a rebase to one it never held.</summary>
    private static bool ReadsAs(JournalModelState state, JournalOutput output, out JournalModelState next)
    {
        next = state;
        if (output.Version is not { } version || output.Exists != state.Exists || !state.HasContent(output.Content.Span) || state.At(version) is not { } at)
        {
            return false;
        }

        next = at;
        return true;
    }

    private static string DescribeResponse(JournalOperationKind kind, JournalOutput output) => output.Outcome switch
    {
        JournalOutcome.Rejected => Invariant($"Rejected({output.Version})"),
        JournalOutcome.Accepted when kind == JournalOperationKind.Read =>
            Invariant($"{(output.Exists ? "exists" : "absent")} at {output.Version} [{Describe(output.Content.Span)}]"),
        JournalOutcome.Accepted => Invariant($"Accepted({output.Version})"),
        JournalOutcome.ReplaceRequired or JournalOutcome.Moved or JournalOutcome.Throttled or JournalOutcome.Fenced or JournalOutcome.Ambiguous => output.Outcome.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(output), output.Outcome, "Undefined journal outcome."),
    };

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private sealed class KeyComparer : IEqualityComparer<JournalInput>
    {
        public bool Equals(JournalInput x, JournalInput y) => string.Equals(x.Key, y.Key, StringComparison.Ordinal);

        public int GetHashCode(JournalInput obj) => StringComparer.Ordinal.GetHashCode(obj.Key);
    }
}
