// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Analyzers;

internal static class Constants
{
    public const string SystemNamespace = "System";

    public const string IAddressibleFullyQualifiedName = "Orleans.Runtime.IAddressable";
    public const string GrainBaseFullyQualifiedName = "Orleans.Grain";

    public const string IdAttributeName = "Id";
    public const string IdAttributeFullyQualifiedName = "global::Orleans.IdAttribute";

    public const string GenerateSerializerAttributeName = "GenerateSerializer";
    public const string GenerateSerializerAttributeFullyQualifiedName = "global::Orleans.GenerateSerializerAttribute";

    public const string SerializableAttributeName = "Serializable";

    public const string NonSerializedAttribute = "NonSerialized";
    public const string NonSerializedAttributeFullyQualifiedName = "global::System.NonSerializedAttribute";
  
    public const string AliasAttributeName = "Alias";
    public const string AliasAttributeFullyQualifiedName = "global::Orleans.AliasAttribute"; 
}