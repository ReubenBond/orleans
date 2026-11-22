namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A <see cref="RowVersion"/>'s encoding (ledgers.md §7): the base's length (<c>u8</c>, at most
/// <see cref="RowToken.MaxLength"/>), the base's bytes, and the count as a shortest varint. Ledgers' rows store a version
/// before so, and Journals' entry record and version text carry an expected version so (journals.md §7).
/// </summary>
internal static class RowVersionCodec
{
    /// <summary>The most bytes a version's encoding takes.</summary>
    public const int MaxLength = 1 + RowToken.MaxLength + Varint.MaxLength;

    /// <summary>What reading a version's encoding found, each a rule of its layout in order.</summary>
    public enum Status : byte
    {
        /// <summary>A valid encoding.</summary>
        Valid,

        /// <summary>The base's length is above <see cref="RowToken.MaxLength"/>.</summary>
        BaseTooLong,

        /// <summary>The bytes end within the base's length or its bytes.</summary>
        FieldTruncated,

        /// <summary>The count is overlong, longer than 10 bytes, above the <c>u64</c> maximum, or unterminated.</summary>
        BadVarint,
    }

    /// <summary>Returns the bytes <paramref name="version"/> takes.</summary>
    public static int Length(RowVersion version) => 1 + version.Base.Length + Varint.Length(version.Count);

    /// <summary>Writes <paramref name="version"/> at the start of <paramref name="destination"/> and returns its length.</summary>
    public static int Write(Span<byte> destination, RowVersion version)
    {
        destination[0] = (byte)version.Base.Length;
        version.Base.AsSpan().CopyTo(destination[1..]);
        var length = 1 + version.Base.Length;
        return length + Varint.Write(destination[length..], version.Count);
    }

    /// <summary>Reads a version's encoding from the start of <paramref name="source"/>, checking its rules in layout order.</summary>
    public static Status Read(ReadOnlySpan<byte> source, out RowVersion version, out int length)
    {
        version = default;
        length = 0;
        if (source.IsEmpty)
        {
            return Status.FieldTruncated;
        }

        var baseLength = source[0];
        if (baseLength > RowToken.MaxLength)
        {
            return Status.BaseTooLong;
        }

        if (source.Length < 1 + baseLength)
        {
            return Status.FieldTruncated;
        }

        if (Varint.Read(source[(1 + baseLength)..], out var count, out var countLength) != Varint.Status.Valid)
        {
            return Status.BadVarint;
        }

        version = new RowVersion(new RowToken(source.Slice(1, baseLength)), count);
        length = 1 + baseLength + countLength;
        return Status.Valid;
    }
}
