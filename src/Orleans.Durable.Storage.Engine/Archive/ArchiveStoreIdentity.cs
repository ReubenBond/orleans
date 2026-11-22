namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// An archive store's kind (archive.md §2.4): the implementation a cluster's store is, which a composition records with
/// the store's ID. The values are the Archive's own, pinned and persisted: a composition which records a kind, as the
/// Orleans node registry does (orleans.md §7.3), records these values.
/// </summary>
public enum ArchiveStoreKind
{
    /// <summary>The LSM (archive-lsm.md).</summary>
    Lsm = 1,

    /// <summary>Archive logs (archive-logs.md).</summary>
    Logs = 2,

    /// <summary>The memory store of tests and simulation (archive.md §9).</summary>
    Memory = 3,
}

/// <summary>
/// An archive store's identity (archive.md §2.4): its kind, the prefix it lies under in its provider's bucket or container,
/// and its ID, which a composition records for its cluster's life and checks every process against (OR-I20). Its
/// constructor refuses an undefined kind, the default prefix, and the default ID; the default identity, which bypasses it,
/// names no store, and a codec which reads one refuses kind 0.
/// </summary>
public readonly record struct ArchiveStoreIdentity
{
    /// <summary>Initializes a new instance of the <see cref="ArchiveStoreIdentity"/> struct.</summary>
    /// <param name="kind">The store's kind.</param>
    /// <param name="prefix">The prefix the store lies under in its bucket or container.</param>
    /// <param name="id">The store's ID, from its record.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="kind"/> is undefined, or <paramref name="prefix"/> or <paramref name="id"/> is the default, which
    /// no store holds.
    /// </exception>
    public ArchiveStoreIdentity(ArchiveStoreKind kind, ArchiveStorePrefix prefix, ArchiveStoreId id)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException("An archive store's kind is the LSM, archive logs, or the memory store.", nameof(kind));
        }

        if (prefix.Value is null)
        {
            throw new ArgumentException("The default ArchiveStorePrefix is not a store's prefix.", nameof(prefix));
        }

        if (id == default)
        {
            throw new ArgumentException("The default ArchiveStoreId is not a store ID.", nameof(id));
        }

        Kind = kind;
        Prefix = prefix;
        Id = id;
    }

    /// <summary>The name of the prefix in a mismatch (OR-I20): <c>prefix</c>.</summary>
    public const string PrefixField = "prefix";

    /// <summary>Gets the store's kind.</summary>
    public ArchiveStoreKind Kind { get; }

    /// <summary>Gets the prefix the store lies under in its bucket or container.</summary>
    public ArchiveStorePrefix Prefix { get; }

    /// <summary>Gets the store's ID.</summary>
    public ArchiveStoreId Id { get; }

    /// <summary>
    /// Checks that a store of <paramref name="kind"/> is of this identity's kind (OR-I20), which needs no request, so a
    /// caller checks it before it touches the store.
    /// </summary>
    /// <param name="kind">The store's kind.</param>
    /// <exception cref="ArchiveStoreMismatchException">The kinds differ: the mismatch names <see cref="ArchiveStoreRecord.KindField"/>.</exception>
    public void CheckKind(ArchiveStoreKind kind)
    {
        if (kind != Kind)
        {
            throw new ArchiveStoreMismatchException(new ArchiveStoreRecordField(ArchiveStoreRecord.KindField, IsParameter: false), kind.ToString(), Kind.ToString());
        }
    }

    /// <summary>
    /// Checks that a process configured with <paramref name="prefix"/> places its store under this identity's prefix
    /// (OR-I20), which needs no request, so a caller checks it before it touches the store: one under another prefix is
    /// another store, wherever its record's ID.
    /// </summary>
    /// <param name="prefix">The prefix the process places its store under.</param>
    /// <exception cref="ArchiveStoreMismatchException">The prefixes differ: the mismatch names <see cref="PrefixField"/>.</exception>
    public void CheckPrefix(ArchiveStorePrefix prefix)
    {
        if (prefix != Prefix)
        {
            throw new ArchiveStoreMismatchException(new ArchiveStoreRecordField(PrefixField, IsParameter: false), prefix.ToString(), Prefix.ToString());
        }
    }

    /// <summary>
    /// Checks that a store of <paramref name="kind"/>, whose record holds <paramref name="recordId"/>, is this identity's
    /// store (OR-I20): its kind, then its record's ID, an absent record refused like another ID.
    /// </summary>
    /// <param name="kind">The store's kind.</param>
    /// <param name="recordId">The ID its record holds, or <see langword="null"/> when it holds no record.</param>
    /// <exception cref="ArchiveStoreMismatchException">
    /// The kinds differ, or the record is absent or holds another ID: the mismatch names
    /// <see cref="ArchiveStoreRecord.KindField"/> or <see cref="ArchiveStoreRecord.IdField"/>.
    /// </exception>
    public void Check(ArchiveStoreKind kind, ArchiveStoreId? recordId)
    {
        CheckKind(kind);
        if (recordId != Id)
        {
            throw new ArchiveStoreMismatchException(new ArchiveStoreRecordField(ArchiveStoreRecord.IdField, IsParameter: false), recordId?.ToString() ?? "nothing", Id.ToString());
        }
    }
}
