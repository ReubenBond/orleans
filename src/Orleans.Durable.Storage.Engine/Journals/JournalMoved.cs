using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// The answer of a write, a read, or a load whose key's open ledger this process does not serve the primary of
/// (journals.md §2.2, §2.3), which has no effect (JS-I4). <see cref="Owner"/> names the primary the process's map lists,
/// a hint: absent when the map lists no open ledger for the key, as before bootstrap. A <c>JournalStore</c> never
/// names its own node: it routes again once its writer registry has reconciled with its map, and otherwise names none,
/// so the caller reads the map again (journals.md §8.5).
/// </summary>
/// <param name="Owner">The node of the key's primary in this process's map, if it lists one.</param>
public sealed record JournalMoved(NodeId? Owner);
