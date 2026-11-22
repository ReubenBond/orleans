using Orleans.Durable.Storage.Engine.Wal.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// The kind of local storage operation whose failure a <see cref="LedgerStoreNode"/> injects, each a fault hook of
/// <see cref="SimulatedWalStorage"/> (docs/spec/verification.md §7.3).
/// </summary>
public enum StorageFault
{
    /// <summary>A WAL write fails before any of its bytes reach the disk.</summary>
    Write,

    /// <summary>A WAL write fails after a seeded subset of its sectors, each old, new, or torn, reached the disk.</summary>
    TornWrite,

    /// <summary>A WAL read fails.</summary>
    Read,

    /// <summary>A named file's creation, such as a checkpoint's, fails before its bytes reach the disk.</summary>
    NamedWrite,

    /// <summary>A named file's read fails.</summary>
    NamedRead,

    /// <summary>A WAL file's deletion fails.</summary>
    Delete,

    /// <summary>A named file's deletion fails.</summary>
    NamedDelete,

    /// <summary>The directory flush which makes a file's creation or deletion durable fails.</summary>
    DirectoryFlush,
}
