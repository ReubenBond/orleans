// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Tester.StorageFacet.Abstractions;

[AttributeUsage(AttributeTargets.Parameter)]
public class ExampleStorageAttribute : Attribute, IFacetMetadata, IExampleStorageConfig
{
    public string StorageProviderName { get; }

    public string StateName { get; }

    public ExampleStorageAttribute(string storageProviderName = null, string stateName = null)
    {
        this.StorageProviderName = storageProviderName;
        this.StateName = stateName;
    }
}
