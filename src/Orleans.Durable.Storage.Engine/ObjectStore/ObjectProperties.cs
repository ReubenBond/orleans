namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>An object's properties, which one metadata read of the object answers.</summary>
/// <param name="Length">The object's length in bytes, possibly 0.</param>
/// <param name="Crc32c">
/// The CRC-32C of the object's content, which a create or a tombstone records for a non-empty object;
/// <see langword="null"/> for an empty object and for an appendable one, whose length grows.
/// </param>
public sealed record ObjectProperties(long Length, uint? Crc32c);
