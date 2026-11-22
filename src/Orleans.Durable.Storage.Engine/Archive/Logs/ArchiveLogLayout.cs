namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>How a store packs an append run into units (archive-logs.md §8.2).</summary>
public enum ArchiveLogPacking
{
    /// <summary>
    /// Greedily, in command order, into units of up to <see cref="ArchiveLogLayout.MaxAppendBytes"/>, so no command spans
    /// units: Azure's blocks, and the memory store's model of them.
    /// </summary>
    Blocks,

    /// <summary>The whole run as one unit: the chunk layout's chunks; ahead of need, for row 3.9's chunk store.</summary>
    Chunks,
}

/// <summary>
/// A store's layout (archive-logs.md §4, §7.5): its name in the store's record, its packing, the limits the pure members
/// read, and the version of its rule for log names, all of which the store's record holds for the cluster's life
/// (HI-I3). The chunk packing and the unbounded limits are ahead of need, for row 3.9's chunk store.
/// </summary>
public sealed record ArchiveLogLayout
{
    /// <summary>Initializes a new instance of the <see cref="ArchiveLogLayout"/> class.</summary>
    /// <param name="name">The layout's name in the store's record: <c>azure-append-blob</c>, <c>chunks</c>, or <c>memory</c>.</param>
    /// <param name="packing">How an append run packs into units.</param>
    /// <param name="maxAppendBytes">The largest <c>Append</c>, and the largest block, or <see langword="null"/> for unbounded.</param>
    /// <param name="maxUnits">The most units an append run may bring a generation to, or <see langword="null"/> for unbounded.</param>
    /// <param name="replaceRequestUnits">The units at which the store asks for a <c>Replace</c>, below <paramref name="maxUnits"/>.</param>
    /// <param name="nameRule">
    /// The version of the store's rule for the log names it can form, of <c>[a-z0-9-]</c>, which
    /// <see cref="IArchiveLogStore.AdmitsName"/> evaluates: <c>azure-blob-2</c> on Azure, <c>any</c> in memory.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The name or the name rule is empty or not of <c>[a-z0-9-]</c>; a limit is not positive; blocks have no size; or the
    /// request is not below the limit.
    /// </exception>
    public ArchiveLogLayout(string name, ArchiveLogPacking packing, int? maxAppendBytes, ulong? maxUnits, ulong replaceRequestUnits, string nameRule)
    {
        CheckWord(name, nameof(name));
        CheckWord(nameRule, nameof(nameRule));

        if (!Enum.IsDefined(packing))
        {
            throw new ArgumentOutOfRangeException(nameof(packing));
        }

        if (packing == ArchiveLogPacking.Blocks && maxAppendBytes is null)
        {
            throw new ArgumentException("A layout of blocks bounds a block's size.", nameof(maxAppendBytes));
        }

        if (maxAppendBytes is { } bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes, nameof(maxAppendBytes));
        }

        ArgumentOutOfRangeException.ThrowIfZero(replaceRequestUnits);
        if (maxUnits is { } units)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(replaceRequestUnits, units);
        }

        Name = name;
        Packing = packing;
        MaxAppendBytes = maxAppendBytes;
        MaxUnits = maxUnits;
        ReplaceRequestUnits = replaceRequestUnits;
        NameRule = nameRule;
    }

    /// <summary>Gets the layout's name in the store's record.</summary>
    public string Name { get; }

    /// <summary>Gets how an append run packs into units.</summary>
    public ArchiveLogPacking Packing { get; }

    /// <summary>Gets the largest <c>Append</c>, and on blocks the largest block, or <see langword="null"/> for unbounded.</summary>
    public int? MaxAppendBytes { get; }

    /// <summary>Gets the most units an append run may bring a generation to, or <see langword="null"/> for unbounded.</summary>
    public ulong? MaxUnits { get; }

    /// <summary>Gets the units at which the store asks for a <c>Replace</c>, with headroom before <see cref="MaxUnits"/>.</summary>
    public ulong ReplaceRequestUnits { get; }

    /// <summary>Gets the version of the store's rule for the log names it can form, which the store's record records.</summary>
    public string NameRule { get; }

    private static void CheckWord(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameter);
        if (value.AsSpan().ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-"))
        {
            throw new ArgumentException($"A layout's {parameter} is of [a-z0-9-], not '{value}'.", parameter);
        }
    }
}
