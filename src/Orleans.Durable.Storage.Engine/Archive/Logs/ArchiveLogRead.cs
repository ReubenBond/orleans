using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// What a read of a log found (archive-logs.md §3, conventions.md §15): <see cref="Absent"/>, <see cref="Stale"/>, or
/// the log as of one linearizable state.
/// </summary>
public readonly union ArchiveLogRead(ArchiveLogRead.Absent, ArchiveLogRead.Stale, ArchiveLogRead.Read)
{
    /// <summary>The log is absent: no drain has written it.</summary>
    public sealed record Absent
    {
        private Absent()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Absent Instance { get; } = new();
    }

    /// <summary>The log changed during the read, or an object it names is gone; the caller reads again.</summary>
    public sealed record Stale
    {
        private Stale()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Stale Instance { get; } = new();
    }

    /// <summary>
    /// The log as of one linearizable state, which owns its content; its caller disposes it. Whether the current
    /// generation has a base and its tombstone follow from its opener, its base's length, and its position alone, so every
    /// store answers them by one rule.
    /// </summary>
    public sealed class Read : IDisposable
    {
        /// <summary>
        /// The largest content a read answers, <see cref="int.MaxValue"/> bytes: an archive prefix's bound (deferred.md,
        /// content above 2 GiB).
        /// </summary>
        public const int MaxContentLength = int.MaxValue;

        private readonly ArcBuffer _content;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="Read"/> class, which retains its own lease on <paramref name="content"/>.</summary>
        /// <param name="position">The log's position, which is never <see cref="ArchiveLogPosition.Origin"/>.</param>
        /// <param name="opener">The opener which created the current generation, or <see langword="null"/> for a generation 1 a key's first append run created.</param>
        /// <param name="baseLength">The length of the generation's base, which <paramref name="content"/> begins with; 0 without an opener.</param>
        /// <param name="content">The log's content: the generation's base, then its appends; the caller keeps its own lease.</param>
        /// <exception cref="ArgumentException">
        /// The position is <c>Origin</c> or names other base rows than the opener; a generation without an opener has a base;
        /// or the content is not the base and the position's appends.
        /// </exception>
        public Read(ArchiveLogPosition position, ArchiveLogOpener? opener, int baseLength, ArcBuffer content)
        {
            if (position.IsOrigin)
            {
                throw new ArgumentException("A log which exists has a generation.", nameof(position));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(baseLength);
            if (opener is null && baseLength > 0)
            {
                throw new ArgumentException("A generation a key's first append run created has no base.", nameof(baseLength));
            }

            if (position.BaseRows != (opener?.Rows ?? 0))
            {
                throw new ArgumentException("A position's base rows are its generation's opener's.", nameof(position));
            }

            if ((ulong)content.Length != (ulong)baseLength + position.Length)
            {
                throw new ArgumentException("A log's content is its base followed by its position's appends.", nameof(content));
            }

            Position = position;
            Opener = opener;
            BaseLength = baseLength;
            _content = content.Retain();
        }

        /// <summary>Gets the log's position.</summary>
        public ArchiveLogPosition Position { get; }

        /// <summary>Gets the opener which created the current generation, or <see langword="null"/> for a generation 1 a key's first append run created.</summary>
        public ArchiveLogOpener? Opener { get; }

        /// <summary>Gets the length of the generation's base, with which the content begins.</summary>
        public int BaseLength { get; }

        /// <summary>Gets a value indicating whether a <c>Replace</c> opened the current generation, an empty one included.</summary>
        public bool HasBase => Opener?.Kind == ArchiveLogOpenerKind.Replace;

        /// <summary>
        /// Gets the opener's tombstone while nothing followed it: an empty base and no appends; otherwise
        /// <see langword="null"/>.
        /// </summary>
        public ulong? Tombstone => BaseLength == 0 && Position.Length == 0 ? Opener?.Tombstone : null;

        /// <summary>Gets the log's content, the base and then the appends, which the read owns; valid until it is disposed.</summary>
        public ArcBuffer Content => _content;

        /// <summary>Throws unless content of <paramref name="length"/> bytes fits a read.</summary>
        /// <exception cref="NotSupportedException">The content would pass <see cref="MaxContentLength"/>.</exception>
        public static void CheckContentLength(long length)
        {
            if (length > MaxContentLength)
            {
                throw new NotSupportedException(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"A key's content of {length} bytes passes the {MaxContentLength}-byte bound of an archive log's read."));
            }
        }

        /// <summary>Releases the content.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _content.Dispose();
            }
        }
    }
}
/// <summary>
/// A log's properties (archive-logs.md §3): its position and its current generation's opener, as of one linearizable
/// state.
/// </summary>
/// <param name="Position">The log's position, never <see cref="ArchiveLogPosition.Origin"/>.</param>
/// <param name="Opener">The opener which created the current generation, or <see langword="null"/> for a generation 1 a key's first append run created.</param>
public sealed record ArchiveLogProperties(ArchiveLogPosition Position, ArchiveLogOpener? Opener);

/// <summary>
/// The outcome of <see cref="IArchiveLogStore.AppendAsync"/> and <see cref="IArchiveLogStore.OpenAsync"/>
/// (archive-logs.md §3).
/// </summary>
public readonly union ArchiveLogWriteOutcome(ArchiveLogWriteOutcome.Applied, ArchiveLogWriteOutcome.ConditionFailed) : IEquatable<ArchiveLogWriteOutcome>
{
    /// <summary>Every request of the write is durable.</summary>
    public sealed record Applied
    {
        private Applied()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Applied Instance { get; } = new();
    }

    /// <summary>
    /// A request's condition failed, which asserts nothing about why: a racing write, a straggler, or a retry which met its
    /// own landing. Its caller reads the log's properties and classifies them (archive-logs.md §8.3).
    /// </summary>
    public sealed record ConditionFailed
    {
        private ConditionFailed()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static ConditionFailed Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(ArchiveLogWriteOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ArchiveLogWriteOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
