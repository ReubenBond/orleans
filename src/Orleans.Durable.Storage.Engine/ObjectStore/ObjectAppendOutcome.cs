namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The outcome of <see cref="IAppendableObjectStore.AppendAsync"/>: <see cref="Appended"/> once the bytes are durable at
/// the expected length, <see cref="LengthMismatch"/> when the object is open at another length, <see cref="Sealed"/> when
/// it is sealed or not appendable and does not hold the bytes there, or <see cref="Absent"/> (object-store.md §2.2).
/// </summary>
public readonly union ObjectAppendOutcome(ObjectAppendOutcome.Appended, ObjectAppendOutcome.LengthMismatch, ObjectAppendOutcome.Sealed, ObjectAppendOutcome.Absent) : IEquatable<ObjectAppendOutcome>
{
    /// <summary>
    /// The call's bytes lie durably at the expected length, and the object held <paramref name="Length"/> bytes once they
    /// landed: this attempt's append, or an earlier attempt's which landed before a seal.
    /// </summary>
    /// <param name="Length">The expected length plus the content's length.</param>
    public sealed record Appended(long Length);

    /// <summary>
    /// The object was open but did not durably hold the expected length when the call looked: another append's doing, or
    /// this call's own earlier attempt's, whose answer a retrying client lost. A caller which needs to know reads the
    /// object.
    /// </summary>
    /// <param name="Length">The object's durable length.</param>
    public sealed record LengthMismatch(long Length);

    /// <summary>
    /// The object is sealed, or not appendable, durably, and does not hold the call's bytes at the expected length: no
    /// attempt of this call landed, and none lands again.
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
    public bool Equals(ObjectAppendOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ObjectAppendOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
