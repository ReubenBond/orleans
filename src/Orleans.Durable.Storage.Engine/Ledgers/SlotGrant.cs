namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A <see cref="Sequencer"/>'s grant of one slot to one append: the slot, and the lifetime which granted it, which alone
/// completes it.
/// </summary>
/// <param name="Slot">The granted slot.</param>
/// <param name="Lifetime">The instance ID of the lifetime which granted the slot; a default grant carries none.</param>
internal readonly record struct SlotGrant(Slot Slot, SequencerInstanceId Lifetime);
