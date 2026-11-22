using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// A change a worker submits to the manifest committer (archive §2.9, §4): a drain or a compaction.
/// </summary>
/// <remarks>
/// A change's sections are kept in position order, each position once, so the manifest merges them in one pass.
/// Equality compares the case and its fields, the sections element by element, so the committer answers identical
/// submissions alike.
/// </remarks>
public readonly union ManifestChange(ManifestChange.Drain, ManifestChange.Compaction) : IEquatable<ManifestChange>
{
    /// <summary>
    /// A drain of <see cref="Ledger"/>'s decided rows with slots in <c>[From, Below)</c> into <see cref="Sections"/>,
    /// which applies only while the ledger is incomplete and its watermark is <see cref="From"/> (LSM-I1), and which, when
    /// <see cref="Complete"/>, copies the ledger's last rows: <see cref="Below"/> is its recovered tail. A drain of no
    /// slots, <see cref="From"/> equal to <see cref="Below"/>, only completes its ledger, so it is complete and has no
    /// sections.
    /// </summary>
    public sealed record Drain
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Drain"/> class.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="ledger"/> is the default, two sections lie at one
        /// position, or a drain of no slots is incomplete or has sections.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="below"/> lies below <paramref name="from"/>.</exception>
        public Drain(LedgerId ledger, Slot from, Slot below, IEnumerable<SectionRef> sections, bool complete)
        {
            if (ledger == default)
            {
                throw new ArgumentException("A drain names a ledger; the default ledger ID names none.", nameof(ledger));
            }

            if (below < from)
            {
                throw new ArgumentOutOfRangeException(nameof(below), "A drain ends at or above the watermark it starts from.");
            }

            Ledger = ledger;
            From = from;
            Below = below;
            Sections = InPositionOrder(sections, nameof(sections));
            Complete = complete;
            if (below == from && (!complete || !Sections.IsEmpty))
            {
                throw new ArgumentException(
                    "A drain of no slots completes its ledger and adds no sections; any other drain copies at least one slot.",
                    complete ? nameof(sections) : nameof(complete));
            }
        }

        /// <summary>Gets the drained ledger.</summary>
        public LedgerId Ledger { get; }

        /// <summary>Gets the watermark the drain starts from.</summary>
        public Slot From { get; }

        /// <summary>Gets the slot below which the drain copied the ledger's rows.</summary>
        public Slot Below { get; }

        /// <summary>Gets the sections the drain uploaded.</summary>
        public ImmutableArray<SectionRef> Sections { get; }

        /// <summary>Gets a value indicating whether the drain copied the ledger's last rows.</summary>
        public bool Complete { get; }

        /// <inheritdoc/>
        public bool Equals(Drain? other) =>
            other is not null && Ledger == other.Ledger && From == other.From && Below == other.Below && Complete == other.Complete
            && Sections.SequenceEqual(other.Sections);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Ledger, From, Below, Complete, Sections.Length);
    }

    /// <summary>
    /// A compaction of <see cref="Inputs"/> into <see cref="Outputs"/>, which applies only while every input is listed
    /// (LSM-I5).
    /// </summary>
    public sealed record Compaction
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Compaction"/> class.
        /// </summary>
        /// <exception cref="ArgumentException">The compaction has no input, or two of its sections lie at one position.</exception>
        public Compaction(IEnumerable<SectionRef> inputs, IEnumerable<SectionRef> outputs)
        {
            Inputs = InPositionOrder(inputs, nameof(inputs));
            Outputs = InPositionOrder(outputs, nameof(outputs));
            if (Inputs.IsEmpty)
            {
                throw new ArgumentException("A compaction consumes at least one section.", nameof(inputs));
            }

            if (Outputs.Any(output => Inputs.BinarySearch(output, SectionRef.PositionOrder) >= 0))
            {
                throw new ArgumentException("A compaction's outputs lie at positions none of its inputs holds.", nameof(outputs));
            }
        }

        /// <summary>Gets the sections the compaction merged.</summary>
        public ImmutableArray<SectionRef> Inputs { get; }

        /// <summary>Gets the sections the compaction uploaded, none when every row it read was dropped.</summary>
        public ImmutableArray<SectionRef> Outputs { get; }

        /// <inheritdoc/>
        public bool Equals(Compaction? other) =>
            other is not null && Inputs.SequenceEqual(other.Inputs) && Outputs.SequenceEqual(other.Outputs);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Inputs.Length, Outputs.Length);
    }

    /// <inheritdoc/>
    public bool Equals(ManifestChange other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ManifestChange other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>
    /// Returns <paramref name="sections"/> in position order, each position once, as the manifest lists them.
    /// </summary>
    /// <exception cref="ArgumentException">Two sections lie at one position.</exception>
    private static ImmutableArray<SectionRef> InPositionOrder(IEnumerable<SectionRef> sections, string name)
    {
        if (sections is null)
        {
            throw new ArgumentNullException(name);
        }

        var ordered = sections.Order(SectionRef.PositionOrder).ToImmutableArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (SectionRef.CompareByPosition(ordered[index - 1], ordered[index]) == 0)
            {
                throw new ArgumentException("A change lists each section position once.", name);
            }
        }

        return ordered;
    }
}
