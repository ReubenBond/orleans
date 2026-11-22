using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.GroupCommit;

/// <summary>
/// A value's encoding for the write which holds it: the finished bytes, and the CRC-32C the store verifies, taken over
/// them when they were encoded, for a store which verifies one.
/// </summary>
/// <param name="Content">The bytes, which the committer owns and releases once the write's task completes.</param>
/// <param name="Crc32C">The CRC-32C of <paramref name="Content"/>, or <see langword="null"/> for a store which verifies none.</param>
public readonly record struct GroupCommitEncoding(ArcBuffer Content, uint? Crc32C = null);
