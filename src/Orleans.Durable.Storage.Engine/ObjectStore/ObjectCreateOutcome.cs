namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The outcome of <see cref="IObjectStore.CreateAsync"/>: <see cref="Created"/> once the new object is durable, or
/// <see cref="Exists"/> when an object of the name durably exists, which may be this call's own earlier attempt (object-store.md
/// §2.1).
/// </summary>
public readonly union ObjectCreateOutcome(ObjectCreateOutcome.Created, ObjectCreateOutcome.Exists) : IEquatable<ObjectCreateOutcome>
{
    /// <summary>
    /// The create stored the object, which is durable.
    /// </summary>
    public sealed record Created
    {
        private Created()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Created Instance { get; } = new();
    }

    /// <summary>
    /// An object of the name exists, durably: another create's, or this call's own earlier attempt, whose answer a
    /// retrying client lost.
    /// </summary>
    public sealed record Exists
    {
        private Exists()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Exists Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(ObjectCreateOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ObjectCreateOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
