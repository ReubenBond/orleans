namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// The manifest committer's verdict on a change (archive §2.9), with the epoch of the manifest value, written or read
/// after the change was submitted, which decided it.
/// </summary>
public readonly union ManifestCommitOutcome(ManifestCommitOutcome.Applied, ManifestCommitOutcome.Superseded) : IEquatable<ManifestCommitOutcome>
{
    /// <summary>
    /// The manifest at <paramref name="Epoch"/> holds the change's goal.
    /// </summary>
    /// <param name="Epoch">The epoch of the manifest which decided the change.</param>
    public sealed record Applied(ulong Epoch);

    /// <summary>
    /// The manifest at <paramref name="Epoch"/> lacks the change's goal and fails its commit rule; no attempt of the change
    /// applies from then on.
    /// </summary>
    /// <param name="Epoch">The epoch of the manifest which decided the change.</param>
    public sealed record Superseded(ulong Epoch);

    /// <inheritdoc/>
    public bool Equals(ManifestCommitOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ManifestCommitOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
}
