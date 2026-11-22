namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// The outcome of a journal write (journals.md §2.2, conventions.md §15): <see cref="Accepted"/> with the journal's
/// version after the entry, <see cref="Rejected"/> with the applied <c>CurrentVersion</c>, <see cref="ReplaceRequired"/>,
/// <see cref="JournalMoved"/>, <see cref="Fenced"/>, or <see cref="Ambiguous"/>. <c>Rejected</c>, <c>ReplaceRequired</c>,
/// <c>Moved</c>, and <c>Fenced</c> have no effect (JS-I4).
/// </summary>
public readonly union JournalWriteOutcome(
    JournalWriteOutcome.Accepted,
    JournalWriteOutcome.Rejected,
    JournalWriteOutcome.ReplaceRequired,
    JournalMoved,
    JournalWriteOutcome.Fenced,
    JournalWriteOutcome.Ambiguous) : IEquatable<JournalWriteOutcome>
{
    /// <summary>The entry is durable, and the owner's warm state has applied it (JS-I2).</summary>
    /// <param name="Version">The journal's version after the entry.</param>
    /// <param name="ReplaceRequested">
    /// Whether the archive store asks for a <c>Replace</c> of the journal (<c>IsReplaceRequested</c> of the key's
    /// admitted write), a hint the writer latches until a <c>Replace</c> is accepted (journals.md §2.4).
    /// </param>
    public sealed record Accepted(JournalVersion Version, bool ReplaceRequested);

    /// <summary>
    /// The entry applies to no state the key's in-flight entries can leave on the owner; <paramref name="CurrentVersion"/>
    /// is the owner's applied state's, a version the journal held (JS-I1).
    /// </summary>
    /// <param name="CurrentVersion">The applied state's <c>CurrentVersion</c>.</param>
    public sealed record Rejected(JournalVersion CurrentVersion);

    /// <summary>
    /// An <c>Append</c> which the archive store's per-generation limits refuse (<c>Admits</c>), decided against the owner's
    /// applied state with nothing of its key in flight; the writer's next write is a <c>Replace</c> (journals.md §8.1).
    /// </summary>
    public sealed record ReplaceRequired
    {
        private ReplaceRequired()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static ReplaceRequired Instance { get; } = new();
    }

    /// <summary>The entry is not in the ledger and never will be.</summary>
    public sealed record Fenced
    {
        private Fenced()
        {
        }

        /// <summary>Gets the one fenced outcome.</summary>
        public static Fenced Instance { get; } = new();
    }

    /// <summary>Asserts nothing: the entry may still be decided, at most once.</summary>
    public sealed record Ambiguous
    {
        private Ambiguous()
        {
        }

        /// <summary>Gets the one ambiguous outcome.</summary>
        public static Ambiguous Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(JournalWriteOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is JournalWriteOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
