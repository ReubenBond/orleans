namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// An object a listing names (object-store.md §2.1): its name and its length in bytes, which every service's listing carries.
/// </summary>
/// <param name="Name">The object's name.</param>
/// <param name="Length">The object's length in bytes, at least 1.</param>
public readonly record struct ListedObject(ObjectName Name, long Length);
