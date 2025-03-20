// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Orleans.Serialization;

/// <summary>
/// Options for <see cref="JsonCodec"/>.
/// </summary>
public class JsonCodecOptions
{
    /// <summary>
    /// Gets or sets the <see cref="JsonSerializerOptions"/>.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets the <see cref="JsonReaderOptions"/>.
    /// </summary>
    public JsonReaderOptions ReaderOptions { get; set; }

    /// <summary>
    /// Gets or sets a delegate used to determine if a type is supported by the JSON serializer for serialization and deserialization.
    /// </summary>
    public Func<Type, bool?> IsSerializableType { get; set; }

    /// <summary>
    /// Gets or sets a delegate used to determine if a type is supported by the JSON serializer for copying.
    /// </summary>
    public Func<Type, bool?> IsCopyableType { get; set; }
}