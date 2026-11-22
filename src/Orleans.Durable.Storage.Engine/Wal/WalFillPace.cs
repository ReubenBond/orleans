namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// Paces the zero-fill of a preallocated log file: <see cref="WalDirectory.CreateFileAsync"/> awaits it before each
/// zero-fill write.
/// </summary>
/// <param name="written">The bytes of the file written so far, including its header.</param>
/// <param name="count">The bytes the next write holds.</param>
/// <param name="cancellationToken">The creation's cancellation token.</param>
/// <returns>A task which completes when the write may start.</returns>
public delegate ValueTask WalFillPace(long written, int count, CancellationToken cancellationToken);
