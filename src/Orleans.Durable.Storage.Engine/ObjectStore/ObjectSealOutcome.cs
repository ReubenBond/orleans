namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The outcome of <see cref="IAppendableObjectStore.SealAsync"/>: <see cref="Sealed"/> once an appendable object is
/// durably sealed, <see cref="NotAppendable"/> when the object is not appendable, as a tombstone is, or
/// <see cref="Absent"/> (object-store.md §2.2).
/// </summary>
public readonly union ObjectSealOutcome(ObjectSealOutcome.Sealed, ObjectSealOutcome.NotAppendable, ObjectSealOutcome.Absent) : IEquatable<ObjectSealOutcome>
{
    /// <summary>
    /// The object is appendable and durably sealed: no append lands on it again.
    /// </summary>
    public sealed record Sealed
    {
        private Sealed()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Sealed Instance { get; } = new();
    }

    /// <summary>
    /// The object durably exists and is not appendable: created whole, or replaced by its tombstone. The seal changed
    /// nothing.
    /// </summary>
    public sealed record NotAppendable
    {
        private NotAppendable()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static NotAppendable Instance { get; } = new();
    }

    /// <summary>
    /// No object of the name durably exists.
    /// </summary>
    public sealed record Absent
    {
        private Absent()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Absent Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(ObjectSealOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ObjectSealOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
