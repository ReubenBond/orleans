using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// A proven empty lower bound for one live ledger carried by <see cref="ArchiveStoreRead.Absent"/> (archive.md §8.1):
/// the store proves the key has no row in <see cref="Ledger"/> below <see cref="Below"/>.
/// </summary>
/// <param name="Ledger">The live ledger.</param>
/// <param name="Below">The slot below which the key has no row in <paramref name="Ledger"/>.</param>
public readonly record struct LedgerAbsentBound(LedgerId Ledger, Slot Below);

/// <summary>
/// An archive store's answer to a read (archive.md §3, conventions.md §15): <see cref="Absent"/>, the key's prefix, or
/// <see cref="Stale"/>.
/// </summary>
public readonly union ArchiveStoreRead(ArchiveStoreRead.Absent, ArchiveStoreRead.Read, ArchiveStoreRead.Stale)
{
    /// <summary>
    /// The key's prefix would hold no command, as one read of the store begun after the call found, with optional
    /// per-ledger bounds below which the store proves the key has no row in each listed ledger (archive.md §8.1).
    /// </summary>
    public sealed record Absent
    {
        private Absent(ImmutableArray<LedgerAbsentBound> bounds) => Bounds = bounds;

        /// <summary>Gets the absent answer carrying no per-ledger bounds.</summary>
        public static Absent Instance { get; } = new([]);

        /// <summary>
        /// Gets the per-ledger slot bounds below which the store proves the key has no row in each listed ledger, in
        /// ascending ledger-ID order; a listed ledger absent from this array has bound <see cref="Slot.Zero"/>.
        /// </summary>
        public ImmutableArray<LedgerAbsentBound> Bounds { get; }

        /// <summary>
        /// Returns an <see cref="Absent"/> answer carrying <paramref name="bounds"/> (ignoring any zero bound and
        /// canonicalizing by ledger ID), or <see cref="Instance"/> when none is above <see cref="Slot.Zero"/>.
        /// </summary>
        /// <param name="bounds">The per-ledger slot bounds proven empty for the key.</param>
        public static Absent WithBounds(ReadOnlySpan<LedgerAbsentBound> bounds)
        {
            if (bounds.IsEmpty)
            {
                return Instance;
            }

            ImmutableArray<LedgerAbsentBound>.Builder? builder = null;
            foreach (var entry in bounds)
            {
                if (entry.Below > Slot.Zero)
                {
                    (builder ??= ImmutableArray.CreateBuilder<LedgerAbsentBound>(bounds.Length)).Add(entry);
                }
            }

            if (builder is null)
            {
                return Instance;
            }

            if (builder.Count > 1)
            {
                builder.Sort(static (left, right) => left.Ledger.CompareTo(right.Ledger));
                var write = 0;
                for (var read = 1; read < builder.Count; read++)
                {
                    if (builder[read].Ledger == builder[write].Ledger)
                    {
                        if (builder[read].Below > builder[write].Below)
                        {
                            builder[write] = builder[read];
                        }
                    }
                    else
                    {
                        builder[++write] = builder[read];
                    }
                }

                builder.Count = write + 1;
            }

            return new Absent(builder.DrainToImmutable());
        }

        /// <summary>Returns the proven empty bound for <paramref name="ledger"/>, or <see cref="Slot.Zero"/> when none.</summary>
        public Slot Bound(LedgerId ledger) => BoundOf(Bounds, ledger);

        /// <inheritdoc/>
        public bool Equals(Absent? other) =>
            other is not null && Bounds.AsSpan().SequenceEqual(other.Bounds.AsSpan());

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = default(HashCode);
            foreach (var entry in Bounds)
            {
                hash.Add(entry);
            }

            return hash.ToHashCode();
        }

        internal static Slot BoundOf(ImmutableArray<LedgerAbsentBound> bounds, LedgerId ledger)
        {
            foreach (var entry in bounds)
            {
                if (entry.Ledger == ledger)
                {
                    return entry.Below;
                }
            }

            return Slot.Zero;
        }
    }

    /// <summary>The key's prefix in the store, which the read owns; its caller disposes it.</summary>
    public sealed class Read : IDisposable
    {
        /// <summary>Initializes a new instance of the <see cref="Read"/> class, which takes <paramref name="prefix"/>.</summary>
        /// <param name="prefix">The prefix, which the read takes.</param>
        public Read(ArchivePrefix prefix)
        {
            ArgumentNullException.ThrowIfNull(prefix);
            Prefix = prefix;
        }

        /// <summary>Gets the key's prefix, which the read owns.</summary>
        public ArchivePrefix Prefix { get; }

        /// <summary>Releases the prefix's content.</summary>
        public void Dispose() => Prefix.Dispose();
    }

    /// <summary>
    /// The store's own snapshot proved out of date, or the caller's map value older than it; the caller reads the
    /// ledger map and the store again.
    /// </summary>
    public sealed record Stale
    {
        private Stale()
        {
        }

        /// <summary>Gets the one stale answer.</summary>
        public static Stale Instance { get; } = new();
    }
}
