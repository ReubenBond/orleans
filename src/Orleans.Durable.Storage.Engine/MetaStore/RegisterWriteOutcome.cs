namespace Orleans.Durable.Storage.Engine.MetaStore;

/// <summary>
/// The definite outcome of a conditional register write: <see cref="Applied"/> with the register's new version, or
/// <see cref="Mismatch"/> (metastore.md §2.2). A write whose outcome is unknown throws instead.
/// </summary>
public readonly union RegisterWriteOutcome(
    RegisterWriteOutcome.Applied,
    RegisterWriteOutcome.Mismatch) : IEquatable<RegisterWriteOutcome>
{
    /// <summary>
    /// The write applied: the register now holds its value, durably, at <paramref name="Version"/>, a version the
    /// register never had (MS-I1, MS-I5). A later write may name it as its expected version.
    /// </summary>
    /// <param name="Version">The register's version after the write.</param>
    public sealed record Applied(MetaStoreVersion Version);

    /// <summary>
    /// The backend reported that the register's version did not match the expected version when it evaluated the
    /// condition, or the expected version is a token the backend cannot have issued. After a backend-SDK retry it may
    /// follow an earlier attempt of the same write which applied.
    /// </summary>
    public sealed record Mismatch
    {
        private Mismatch()
        {
        }

        /// <summary>
        /// Gets the one mismatch.
        /// </summary>
        public static Mismatch Instance { get; } = new();
    }

    /// <inheritdoc/>
    public bool Equals(RegisterWriteOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RegisterWriteOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
