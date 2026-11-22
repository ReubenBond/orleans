using System.Globalization;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The checks of a service's answers which every object store provider over a service makes alike.
/// </summary>
public static class ObjectStoreResponses
{
    /// <summary>
    /// Throws unless a service answered a read of bytes <c>[offset, offset + length)</c> with exactly those bytes, judged
    /// by where its answer starts, how many bytes it held, and the object's length, which its <c>Content-Range</c> gives.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="offset">The first byte the read asked for.</param>
    /// <param name="length">The number of bytes the read asked for.</param>
    /// <param name="servedOffset">The first byte the answer holds.</param>
    /// <param name="served">The number of bytes the answer held.</param>
    /// <param name="objectLength">The object's length.</param>
    /// <exception cref="EndOfStreamException">The object ends before <c>offset + length</c>.</exception>
    /// <exception cref="IOException">The service answered other bytes than the read asked for: a service fault.</exception>
    public static void ThrowIfNotServed(ObjectName name, long offset, int length, long servedOffset, long served, long objectLength)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (servedOffset == offset && served == length)
        {
            return;
        }

        if (servedOffset == offset && served < length)
        {
            ObjectStoreArguments.ThrowIfPastEnd(name, offset, length, objectLength);
        }

        throw new IOException(string.Create(
            CultureInfo.InvariantCulture,
            $"A read of '{name}' at {offset}+{length} was answered with {served} bytes from {servedOffset} of an object of {objectLength} bytes."));
    }
}
