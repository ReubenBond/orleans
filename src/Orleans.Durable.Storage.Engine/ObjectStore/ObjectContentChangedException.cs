namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// A create's or replace's content no longer matches the CRC-32C its writer computed when it verified the bytes, so the
/// store wrote nothing (object-store.md §2.1, §6). The writer's buffer changed after verification: a use-after-free or a
/// memory fault in this process, which may have damaged other in-flight state too, so its writer reports it as a defect
/// and the process stops (conventions.md §14).
/// </summary>
/// <remarks>
/// Every provider raises this one type: the memory, file-system, and Azure stores when their own check of the content
/// fails, and a cloud store when its service, reached over TLS, refuses the bytes for a checksum computed in this
/// process, which then also means the bytes changed here (<see cref="ForServiceRefusal"/>).
/// </remarks>
public sealed class ObjectContentChangedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ObjectContentChangedException"/> class.</summary>
    public ObjectContentChangedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ObjectContentChangedException"/> class.</summary>
    public ObjectContentChangedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ObjectContentChangedException"/> class.</summary>
    public ObjectContentChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Returns the exception for a write of <paramref name="name"/> whose content does not match its writer's CRC-32C,
    /// with the service's refusal, if a service refused it, as its inner exception.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="refusal">The service's refusal, or <see langword="null"/> when the store's own check failed.</param>
    public static ObjectContentChangedException For(ObjectName name, Exception? refusal = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var message = $"The content of '{name}' does not match the CRC-32C its writer computed when it verified the bytes, so the store wrote nothing: the writer's buffer changed after verification.";
        return refusal is null ? new ObjectContentChangedException(message) : new ObjectContentChangedException(message, refusal);
    }

    /// <summary>
    /// Returns the exception for a service's refusal of a write of <paramref name="name"/> for its bytes' checksum, which
    /// this process computed. Over TLS, which carries the bytes intact or not at all, the bytes changed in this process:
    /// <see cref="ObjectContentChangedException"/>. Over plain HTTP, they may have changed in transit instead, so the
    /// refusal is an <see cref="IOException"/>: an unknown outcome, which the caller meets as any other.
    /// </summary>
    /// <param name="name">The object's name.</param>
    /// <param name="refusal">The service's refusal.</param>
    /// <param name="overTls">Whether the store reached the service over TLS.</param>
    public static Exception ForServiceRefusal(ObjectName name, Exception refusal, bool overTls)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(refusal);
        return overTls
            ? For(name, refusal)
            : new IOException($"The service refused a write of '{name}' for its checksum. Over plain HTTP the bytes may have changed in transit, so the write's outcome is unknown.", refusal);
    }
}
