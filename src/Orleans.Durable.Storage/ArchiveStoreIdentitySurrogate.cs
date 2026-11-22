using Orleans.Durable.Storage.Engine.Archive;

namespace Orleans.Durable.Storage;

/// <summary>
/// An archive store's identity as a grain message carries it (orleans.md §7.2): its kind, the value <c>CLNR</c> records
/// (§7.3), its 128-bit ID in two halves, and its store prefix, so the Archive names no Orleans type.
/// </summary>
[GenerateSerializer]
[Immutable]
internal readonly struct ArchiveStoreIdentitySurrogate
{
    /// <summary>Gets or initializes the store's kind, 0 for the default identity.</summary>
    [Id(0)]
    public int Kind { get; init; }

    /// <summary>Gets or initializes the upper 64 bits of the store's ID.</summary>
    [Id(1)]
    public ulong UpperId { get; init; }

    /// <summary>Gets or initializes the lower 64 bits of the store's ID.</summary>
    [Id(2)]
    public ulong LowerId { get; init; }

    /// <summary>Gets or initializes the store's prefix, null for the default identity.</summary>
    [Id(3)]
    public string? Prefix { get; init; }
}

/// <summary>
/// Converts an archive store's identity to its surrogate and back. The default identity, which names no store, crosses as
/// all zeros, for its receiver to refuse; any other surrogate the identity's constructor refuses, an undefined kind, a
/// prefix which is none, or the zero ID, is refused with <see cref="InvalidDataException"/>.
/// </summary>
[RegisterConverter]
internal sealed class ArchiveStoreIdentityConverter : IConverter<ArchiveStoreIdentity, ArchiveStoreIdentitySurrogate>
{
    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">The kind is undefined or the ID is zero, and the surrogate is not all zeros.</exception>
    public ArchiveStoreIdentity ConvertFromSurrogate(in ArchiveStoreIdentitySurrogate surrogate)
    {
        if (surrogate is { Kind: 0, UpperId: 0, LowerId: 0, Prefix: null })
        {
            return default;
        }

        var fields = (Kind: (ArchiveStoreKind)surrogate.Kind, surrogate.Prefix, Id: new UInt128(surrogate.UpperId, surrogate.LowerId));
        return SurrogateFields.Build("archive store", fields, static fields => new ArchiveStoreIdentity(fields.Kind, ArchiveStorePrefix.IsValid(fields.Prefix) ? new ArchiveStorePrefix(fields.Prefix!) : default, new ArchiveStoreId(fields.Id)));
    }

    /// <inheritdoc/>
    public ArchiveStoreIdentitySurrogate ConvertToSurrogate(in ArchiveStoreIdentity value)
    {
        if (value == default)
        {
            return default;
        }

        return new() { Kind = (int)value.Kind, UpperId = (ulong)(value.Id.Value >> 64), LowerId = unchecked((ulong)value.Id.Value), Prefix = value.Prefix.Value };
    }
}
