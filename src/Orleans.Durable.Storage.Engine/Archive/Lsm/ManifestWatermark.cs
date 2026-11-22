using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// A ledger's entry in the manifest's watermark table (archive §2.2): the slot below which the archive holds the
/// ledger's rows, and whether it holds every row of the ledger.
/// </summary>
/// <param name="Below">The ledger's watermark: the slot below which the archive holds the ledger's rows.</param>
/// <param name="Complete">Whether the archive holds every row of the ledger.</param>
public readonly record struct ManifestWatermark(Slot Below, bool Complete);
