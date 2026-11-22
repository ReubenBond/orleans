using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// A manifest value with the object version it was read or written at, which is <see langword="null"/> for the absent
/// manifest.
/// </summary>
/// <param name="Value">The manifest's value.</param>
/// <param name="Version">The manifest object's version, or <see langword="null"/> when no manifest exists.</param>
public sealed record ManifestSnapshot(ManifestValue Value, ObjectVersion? Version);
