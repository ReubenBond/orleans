using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The argument checks of <see cref="IObjectStore"/> and <see cref="IAppendableObjectStore"/>, which every provider makes alike.
/// </summary>
public static class ObjectStoreArguments
{
    /// <summary>
    /// Throws unless <paramref name="content"/> is non-empty.
    /// </summary>
    /// <exception cref="ArgumentException">The content is empty.</exception>
    public static void ThrowIfEmpty(ArcBuffer content)
    {
        if (content.Length == 0)
        {
            throw new ArgumentException("An object's content is not empty.", nameof(content));
        }
    }

    /// <summary>
    /// Throws unless <paramref name="expectedLength"/> and <paramref name="content"/> make an append: a length of at least
    /// 0, and from 1 to <see cref="IAppendableObjectStore.MaxAppendLength"/> bytes, ending at most at 2³¹ − 1 bytes, the
    /// most an object holds.
    /// </summary>
    public static void ThrowIfNotAppend(long expectedLength, ArcBuffer content)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        ArgumentOutOfRangeException.ThrowIfZero(content.Length, nameof(content));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(content.Length, IAppendableObjectStore.MaxAppendLength, nameof(content));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expectedLength, (long)int.MaxValue - content.Length, nameof(expectedLength));
    }

    /// <summary>
    /// Throws unless <paramref name="offset"/> and <paramref name="length"/> name a range a read may request.
    /// </summary>
    public static void ThrowIfNotRange(long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
    }

    /// <summary>
    /// Throws when an object of <paramref name="objectLength"/> bytes ends before <c>offset + length</c>.
    /// </summary>
    /// <exception cref="EndOfStreamException">The object ends before the range does.</exception>
    public static void ThrowIfPastEnd(ObjectName name, long offset, int length, long objectLength)
    {
        if (offset + length > objectLength)
        {
            throw new EndOfStreamException(string.Create(
                CultureInfo.InvariantCulture, $"The object '{name}' holds {objectLength} bytes, which end before {offset + length}."));
        }
    }

    /// <summary>
    /// Throws unless <paramref name="startAfter"/> is <see langword="null"/> or a listing prefix, a position in the
    /// ordinal order of names after which a listing starts.
    /// </summary>
    /// <exception cref="ArgumentException">The start is not the start of an object name.</exception>
    public static void ThrowIfNotStart(string? startAfter)
    {
        if (startAfter is not null && !ObjectName.IsValidPrefix(startAfter))
        {
            throw new ArgumentException($"'{startAfter}' is not a listing start: the start of an object name.", nameof(startAfter));
        }
    }

    /// <summary>
    /// Returns whether a listing from <paramref name="startAfter"/> lists <paramref name="name"/>: whether the name lies
    /// after it in ordinal order, or there is no start. A provider checks each name it lists: Azure's <c>startFrom</c> and
    /// Cloud Storage's <c>startOffset</c> include the start's own name, and <see cref="PrefixedObjectStore"/> cuts a start
    /// longer than a name to the longest name, so the inner service may list names at or before the start.
    /// </summary>
    public static bool IsAfter(string name, string? startAfter) =>
        startAfter is null || string.CompareOrdinal(name, startAfter) > 0;

    /// <summary>
    /// Throws unless <paramref name="prefix"/> is a listing prefix.
    /// </summary>
    /// <exception cref="ArgumentException">The prefix is not the start of an object name.</exception>
    public static void ThrowIfNotPrefix(string prefix)
    {
        if (!ObjectName.IsValidPrefix(prefix))
        {
            throw new ArgumentException($"'{prefix}' is not a listing prefix: the start of an object name.", nameof(prefix));
        }
    }
}
