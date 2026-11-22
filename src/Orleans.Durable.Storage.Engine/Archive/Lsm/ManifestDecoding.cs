namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// What decoding a manifest gives (conventions.md §15): its value, or the <see cref="InvalidDataException"/> naming the
/// first rule its bytes break, with the rule's reason code under <see cref="Orleans.Durable.Storage.Engine.DecodeRejection.ReasonKey"/>.
/// </summary>
internal readonly union ManifestDecoding(ManifestValue, InvalidDataException);
