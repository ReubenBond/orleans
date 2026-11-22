namespace Orleans.Durable.Storage.FileSystem.Archive;

/// <summary>
/// What one reclamation of a <see cref="FileSystemObjectStore"/>'s temporary files did (archive §9).
/// </summary>
/// <param name="Reclaimed">The temporary files past the age which it deleted, or found gone or written again.</param>
/// <param name="Refused">The temporary files past the age whose deletion another handle refused, which a later pass
/// deletes.</param>
public readonly record struct TemporaryFileReclamation(int Reclaimed, int Refused);
