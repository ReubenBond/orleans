using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// An owned row with its tag (archive.md §4): how the LSM's sections hold a key's rows, and how a fold takes them.
/// </summary>
/// <param name="Tag">The row's tag.</param>
/// <param name="Row">The row, which owns a lease on its body.</param>
public readonly record struct TaggedRow(Tag Tag, Row Row);
