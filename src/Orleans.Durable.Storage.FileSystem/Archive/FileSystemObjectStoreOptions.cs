namespace Orleans.Durable.Storage.FileSystem.Archive;

/// <summary>
/// Configures how a <see cref="FileSystemObjectStore"/> reclaims the temporary files a crash or a fail-stop leaves behind
/// (archive §9).
/// </summary>
public sealed record FileSystemObjectStoreOptions
{
    /// <summary>The least age a temporary file reaches before a reclamation deletes it.</summary>
    public static readonly TimeSpan MinTemporaryFileAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets the age past which a temporary file is reclaimed, from its last write by the file system's clock to the
    /// store's wall-clock time: far above any write's duration, so a reclamation rarely meets a write in flight, which
    /// then fails with its outcome unknown. At least <see cref="MinTemporaryFileAge"/>.
    /// </summary>
    public TimeSpan TemporaryFileAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Validates the options.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option lies outside its range.</exception>
    public void Validate() => ArgumentOutOfRangeException.ThrowIfLessThan(TemporaryFileAge, MinTemporaryFileAge, nameof(TemporaryFileAge));
}
