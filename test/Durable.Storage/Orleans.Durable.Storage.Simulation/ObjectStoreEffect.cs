namespace Orleans.Durable.Storage.Simulation;

/// <summary>What a request did at the instant it took effect at a <see cref="SimulatedObjectStore"/>.</summary>
public enum ObjectStoreEffectKind
{
    /// <summary>A create, a replace, an appendable create, or a tombstone applied: the object holds the content.</summary>
    Written,

    /// <summary>An append landed on an open object: the content follows the object's earlier bytes.</summary>
    Appended,

    /// <summary>A seal sealed an appendable object.</summary>
    Sealed,

    /// <summary>A delete removed the object.</summary>
    Deleted,

    /// <summary>A listing of the prefix was served.</summary>
    Listed,

    /// <summary>A read of the object was served, whether or not the object exists.</summary>
    Read,
}

/// <summary>
/// A request's effect at a <see cref="SimulatedObjectStore"/>, which <see cref="SimulatedObjectStore.Effects"/> reports
/// at the instant it takes effect.
/// </summary>
/// <param name="Kind">What the request did.</param>
/// <param name="Name">The object's name, or a listing's prefix.</param>
/// <param name="Content">A write's content, or an append's bytes, which the store keeps and the observer must not change;
/// otherwise <see langword="null"/>.</param>
public readonly record struct ObjectStoreEffect(ObjectStoreEffectKind Kind, string Name, byte[]? Content);
