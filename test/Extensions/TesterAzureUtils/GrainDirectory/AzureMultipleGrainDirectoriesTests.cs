// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.TestingHost;
using Tester.AzureUtils;
using UnitTests.Grains.Directories;

namespace Tester.Directories;

[TestCategory("AzureStorage")]
public class AzureMultipleGrainDirectoriesTests : MultipleGrainDirectoriesTests
{
    public class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddAzureTableGrainDirectory(
                CustomDirectoryGrain.DIRECTORY,
                options => options.TableServiceClient = AzureStorageOperationOptionsExtensions.GetTableServiceClient());
        }
    }

    protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForAzureStorage();

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        EnsurePreconditionsMet();

        base.ConfigureTestCluster(builder);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }
}
