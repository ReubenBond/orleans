namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The outcome of <see cref="IObjectStore.ReplaceAsync"/>: <see cref="Applied"/> with the object's new version once the
/// replace is durable, or <see cref="Mismatch"/> when the object durably holds another version, or is present when the
/// replace expected none, or absent when it expected one, which may be this call's own earlier attempt's doing (object-store.md
/// §2.1).
/// </summary>
public readonly union ObjectReplaceOutcome(ObjectReplaceOutcome.Applied, ObjectReplaceOutcome.Mismatch) : IEquatable<ObjectReplaceOutcome>
{
    /// <summary>
    /// The replace stored the new content, durably, at <paramref name="Version"/>.
    /// </summary>
    /// <param name="Version">The object's version after the replace.</param>
    public sealed record Applied(ObjectVersion Version);

    /// <summary>
    /// The object did not durably hold the expected version when the call looked: another write's doing, or this call's
    /// own earlier attempt's, whose answer a retrying client lost. A caller which needs to know reads the object.
    /// </summary>
    public sealed record Mismatch
    {
        private Mismatch()
        {
        }

        /// <summary>
        /// Gets the one value.
        /// </summary>
        public static Mismatch Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(ObjectReplaceOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ObjectReplaceOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
