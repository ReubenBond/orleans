using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// What a key's write came to (archive-logs.md §8.2, conventions.md §15): the verdict of
/// <see cref="Control.ArchiveLogWriter.WriteAsync(ArchiveLogName, ArchiveWrite, System.Buffers.ReadOnlySequence{byte}, CancellationToken)"/>. A contradiction throws <see cref="ArchiveCorruptException"/>.
/// </summary>
public readonly union ArchiveLogConditionOutcome(
    ArchiveLogConditionOutcome.Held,
    ArchiveLogConditionOutcome.PredecessorMissing) : IEquatable<ArchiveLogConditionOutcome>
{
    /// <summary>The log holds the whole write, applied or superseded.</summary>
    public sealed record Held
    {
        private Held()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Held Instance { get; } = new();
    }

    /// <summary>The log lacks a write before this one: its predecessor, which the drain decides on (archive-logs.md §8.1).</summary>
    public sealed record PredecessorMissing
    {
        private PredecessorMissing()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static PredecessorMissing Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(ArchiveLogConditionOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ArchiveLogConditionOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? "default";
}

/// <summary>
/// The verdict on an opener whose condition failed (archive-logs.md §8.3, conventions.md §15), from one properties read.
/// A contradiction throws <see cref="ArchiveCorruptException"/>.
/// </summary>
public readonly union ArchiveLogOpenerConditionOutcome(
    ArchiveLogOpenerConditionOutcome.Held,
    ArchiveLogOpenerConditionOutcome.Retry,
    ArchiveLogOpenerConditionOutcome.PredecessorMissing) : IEquatable<ArchiveLogOpenerConditionOutcome>
{
    /// <summary>The log holds the opener, applied or superseded.</summary>
    public sealed record Held
    {
        private Held()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Held Instance { get; } = new();
    }

    /// <summary>
    /// The log is at the opener's start: it was behind it when the condition failed, and its predecessor landed since, so
    /// the opener is issued once more.
    /// </summary>
    public sealed record Retry
    {
        private Retry()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Retry Instance { get; } = new();
    }

    /// <summary>The log lacks a write before this one: its predecessor, which the drain decides on (archive-logs.md §8.1).</summary>
    public sealed record PredecessorMissing
    {
        private PredecessorMissing()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static PredecessorMissing Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(ArchiveLogOpenerConditionOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ArchiveLogOpenerConditionOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? "default";
}

/// <summary>
/// The verdict on an append run whose condition failed (archive-logs.md §8.3, conventions.md §15), from one properties
/// read. A contradiction throws <see cref="ArchiveCorruptException"/>.
/// </summary>
public readonly union ArchiveLogAppendConditionOutcome(
    ArchiveLogAppendConditionOutcome.Held,
    ArchiveLogAppendConditionOutcome.Resume,
    ArchiveLogAppendConditionOutcome.PredecessorMissing) : IEquatable<ArchiveLogAppendConditionOutcome>
{
    /// <summary>The log holds the whole run, applied or superseded.</summary>
    public sealed record Held
    {
        private Held()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Held Instance { get; } = new();
    }

    /// <summary>
    /// The log holds the run's units before <paramref name="Unit"/>, and the run resumes there; at unit 0, the log was
    /// behind the run when its condition failed, and its predecessor landed since.
    /// </summary>
    /// <param name="Unit">The first unit the log lacks, counting from 0.</param>
    public sealed record Resume(int Unit);

    /// <summary>The log lacks a write before this one: its predecessor, which the drain decides on (archive-logs.md §8.1).</summary>
    public sealed record PredecessorMissing
    {
        private PredecessorMissing()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static PredecessorMissing Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(ArchiveLogAppendConditionOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ArchiveLogAppendConditionOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? "default";
}

/// <summary>
/// What a log holds of a key's write (archive-logs.md §8.3): the pure classifier of a failed condition, one table per
/// write kind, and <c>Holds</c>, the same function over a prefix's position, so the write path and the read path agree on
/// what a log holds.
/// </summary>
/// <remarks>
/// Positions only grow (LOG-I2), and every write lands at most once at its position (LOG-I1), so one properties read made
/// after a failed condition decides the write, however far the log has moved since: past it, held; short of its start,
/// its predecessor missing; at a unit boundary of a run, resumed there; and at the write's own start, which a log behind
/// the write when its condition failed reaches once its predecessor lands meanwhile, issued once more. A read which no
/// conforming store and no single write from the token could leave throws: a log absent after a write failed against it,
/// an opener's generation past its start, a position inside a unit, units other than the run packed, and a first run
/// beside an opener-created first generation.
/// </remarks>
public static class ArchiveLogConditions
{
    /// <summary>A mutant of <see cref="Holds(ArchiveLogPosition, ArchiveWrite, int, HoldsMutant)"/>, for tests.</summary>
    internal enum HoldsMutant
    {
        /// <summary>The function as specified.</summary>
        None,

        /// <summary>Judges a key's first append run as an opener, so a log at <c>(1, 0, 0, 0)</c> holds it.</summary>
        FirstRunAsOpener,

        /// <summary>Judges an append run's commands by the run's whole length, ignoring the count.</summary>
        IgnoresCount,
    }

    /// <summary>
    /// The table of an append run from <paramref name="from"/>, <c>Origin</c> being a run on generation 1 from
    /// <c>(1, 0, 0, 0)</c>, whose units start at <paramref name="boundaries"/> <c>q₀ &lt; … &lt; q_m</c>, against the properties
    /// read <paramref name="read"/>, <see langword="null"/> for an absent log.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The read contradicts the run (see the remarks).</exception>
    internal static ArchiveLogAppendConditionOutcome ClassifyAppend(ArchiveLogPosition from, ReadOnlySpan<ulong> boundaries, ArchiveLogProperties? read)
    {
        var start = RunStart(from);
        if (boundaries.Length < 2 || boundaries[0] != start.Length)
        {
            throw new ArgumentException("A run has a unit, starting at its token's length.", nameof(boundaries));
        }

        if (read is null)
        {
            return from.IsOrigin
                ? throw Contradiction($"A first append run failed its condition, yet the log is absent, though no log is ever deleted")
                : ArchiveLogAppendConditionOutcome.PredecessorMissing.Instance;
        }

        var log = read.Position;
        if (from.IsOrigin && log.Generation == start.Generation && read.Opener is not null)
        {
            throw Contradiction($"A key's first append run meets a generation 1 which an opener created, at {log}: a key has one first write");
        }

        if (log.Generation > start.Generation)
        {
            return ArchiveLogAppendConditionOutcome.Held.Instance;
        }

        if (log.Generation < start.Generation || log.Length < start.Length)
        {
            return ArchiveLogAppendConditionOutcome.PredecessorMissing.Instance;
        }

        var units = (ulong)(boundaries.Length - 1);
        var end = boundaries[^1];
        if (log.Length >= end)
        {
            var packed = start.Units + units;
            var unitsAgree = log.Length == end ? log.Units == packed : log.Units > packed;
            return unitsAgree
                ? ArchiveLogAppendConditionOutcome.Held.Instance
                : throw Contradiction($"The log is at {log}, whose units are not the {packed} the append run from {start} to length {end} packs, and the appends after it");
        }

        var unit = boundaries[..^1].IndexOf(log.Length);
        if (unit < 0)
        {
            throw Contradiction($"The log is at {log}, inside a unit of the append run from {start} to length {end}");
        }

        return log.Units == start.Units + (ulong)unit
            ? new ArchiveLogAppendConditionOutcome.Resume(unit)
            : throw Contradiction($"The log is at {log}, whose units are not the {start.Units + (ulong)unit} the append run from {start} packs there");
    }

    /// <summary>
    /// The table of an opener from <paramref name="from"/>, <c>Origin</c> being generation 0, against the properties read
    /// <paramref name="read"/>, <see langword="null"/> for an absent log.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The read contradicts the opener (see the remarks).</exception>
    internal static ArchiveLogOpenerConditionOutcome ClassifyOpener(ArchiveLogPosition from, ArchiveLogProperties? read)
    {
        if (read is null)
        {
            return from.Generation == 0
                ? throw Contradiction($"An opener from Origin failed its condition, yet the log is absent, though no log is ever deleted")
                : ArchiveLogOpenerConditionOutcome.PredecessorMissing.Instance;
        }

        var log = read.Position;
        if (log.Generation > from.Generation)
        {
            return ArchiveLogOpenerConditionOutcome.Held.Instance;
        }

        if (log.Generation < from.Generation || log.Length < from.Length)
        {
            return ArchiveLogOpenerConditionOutcome.PredecessorMissing.Instance;
        }

        return log == from
            ? ArchiveLogOpenerConditionOutcome.Retry.Instance
            : throw Contradiction($"The log is at {log}, past the opener's start {from} on its own generation, which only a generation tag that disagrees with its metadata leaves");
    }

    /// <summary>
    /// Returns whether a log at <paramref name="position"/>, <c>Origin</c> for an absent one, holds the first
    /// <paramref name="count"/> commands of <paramref name="write"/>, applied or superseded: an append run's when the log
    /// is past its generation or reaches its start plus their lengths, so a log at <c>(1, 0, 0, 0)</c> holds none of a key's
    /// first run; an opener's, for <paramref name="count"/> above 0, when the log is at or past the generation it opens.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative or above the write's commands.</exception>
    /// <exception cref="ArchiveCorruptException">The write's token is malformed.</exception>
    public static bool Holds(ArchiveLogPosition position, ArchiveWrite write, int count) => Holds(position, write, count, HoldsMutant.None);

    /// <inheritdoc cref="Holds(ArchiveLogPosition, ArchiveWrite, int)"/>
    internal static bool Holds(ArchiveLogPosition position, ArchiveWrite write, int count, HoldsMutant mutant)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, write.Commands.Length);
        if (count == 0)
        {
            return true;
        }

        var from = ArchiveLogTokens.PositionOf(write.Token);
        if (write.IsOpener || mutant == HoldsMutant.FirstRunAsOpener && from.IsOrigin)
        {
            return position.Generation > from.Generation;
        }

        var start = RunStart(from);
        var reach = start.Length;
        var counted = mutant == HoldsMutant.IgnoresCount ? write.Commands.Length : count;
        for (var index = 0; index < counted; index++)
        {
            reach += (ulong)write.Commands[index].ContentLength;
        }

        return position.Generation > start.Generation || position.Generation == start.Generation && position.Length >= reach;
    }

    /// <summary>Returns where an append run from <paramref name="from"/> starts: <c>(1, 0, 0, 0)</c> from <c>Origin</c>.</summary>
    internal static ArchiveLogPosition RunStart(ArchiveLogPosition from) => from.IsOrigin ? new ArchiveLogPosition(1, 0, 0, 0) : from;

    /// <summary>Returns the corruption of a log which contradicts a write's chain.</summary>
    internal static ArchiveCorruptException Contradiction(FormattableString message) =>
        new(message.ToString(CultureInfo.InvariantCulture) + ": it contradicts the write's chain.");
}
