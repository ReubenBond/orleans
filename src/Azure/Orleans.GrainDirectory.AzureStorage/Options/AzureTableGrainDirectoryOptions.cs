// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.GrainDirectory.AzureStorage;

namespace Orleans.Configuration;

public class AzureTableGrainDirectoryOptions : AzureStorageOperationOptions
{
    /// <summary>
    /// Table name for Azure Storage
    /// </summary>
    public override string TableName { get; set; } = DEFAULT_TABLE_NAME;
    public const string DEFAULT_TABLE_NAME = "GrainDirectory";
}

public class AzureTableGrainDirectoryOptionsValidator : AzureStorageOperationOptionsValidator<AzureTableGrainDirectoryOptions>
{
    public AzureTableGrainDirectoryOptionsValidator(AzureTableGrainDirectoryOptions options, string name) : base(options, name)
    {
    }
}
