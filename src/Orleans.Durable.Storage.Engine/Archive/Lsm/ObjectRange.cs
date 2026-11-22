namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// A non-empty byte range of an object: <c>[Offset, Offset + Length)</c>, which ends at or below
/// <see cref="long.MaxValue"/>.
/// </summary>
public readonly record struct ObjectRange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectRange"/> struct.
    /// </summary>
    /// <param name="offset">The first byte's offset, at least 0.</param>
    /// <param name="length">The number of bytes, at least 1, which end at or below <see cref="long.MaxValue"/>.</param>
    public ObjectRange(long offset, long length)
    {
        if (!IsValid(offset, length))
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), "A byte range starts at or above 0, holds at least one byte, and ends at or below 2^63 - 1.");
        }

        Offset = offset;
        Length = length;
    }

    /// <summary>
    /// Gets the first byte's offset.
    /// </summary>
    public long Offset { get; }

    /// <summary>
    /// Gets the number of bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Returns whether <paramref name="offset"/> and <paramref name="length"/> make a range.
    /// </summary>
    public static bool IsValid(long offset, long length) => offset >= 0 && length >= 1 && length <= long.MaxValue - offset;
}
