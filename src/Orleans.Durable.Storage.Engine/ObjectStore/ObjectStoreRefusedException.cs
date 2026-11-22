namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// The object store's service refused a request for a reason no repetition changes, such as a missing container, a
/// credential it does not accept, or access it does not grant (object-store.md §9): a definite failure, with no effect, which
/// its caller reports rather than retries. A refusal of the caller's credential or access, <see cref="IsAuthorization"/>,
/// is definite for the request, yet an administrator's grant, or one still propagating, may change the next answer; a
/// caller which waits for the store may repeat it.
/// </summary>
public sealed class ObjectStoreRefusedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ObjectStoreRefusedException"/> class.</summary>
    public ObjectStoreRefusedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ObjectStoreRefusedException"/> class.</summary>
    public ObjectStoreRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ObjectStoreRefusedException"/> class.</summary>
    public ObjectStoreRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ObjectStoreRefusedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The service's answer.</param>
    /// <param name="isAuthorization">Whether the service refused the caller's credential or access.</param>
    public ObjectStoreRefusedException(string message, Exception innerException, bool isAuthorization)
        : base(message, innerException)
    {
        IsAuthorization = isAuthorization;
    }

    /// <summary>
    /// Gets a value indicating whether the service refused the caller's credential or its access to the store, an
    /// authorization which a grant may yet change, rather than the request itself or the store's existence.
    /// </summary>
    public bool IsAuthorization { get; }
}
