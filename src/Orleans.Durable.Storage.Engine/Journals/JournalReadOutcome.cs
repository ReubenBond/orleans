namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// The outcome of a journal read (journals.md §2.3, conventions.md §15): the <see cref="JournalReadResult"/>, which the
/// caller disposes, or <see cref="JournalMoved"/>, as a write answers it, when this process does not serve the key's open
/// ledger's writer or its read was not that writer's.
/// </summary>
public readonly union JournalReadOutcome(JournalReadResult, JournalMoved);
