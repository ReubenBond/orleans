// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit.Abstractions;
using Orleans.LeaseProviders;
using TestExtensions.Runners;
using Orleans.Configuration;
using Microsoft.Extensions.Options;

namespace Tester.AzureUtils.Lease
{
    [TestCategory("Functional"), TestCategory("AzureStorage"), TestCategory("Lease")]
    public class AzureBlobLeaseProviderTests : GoldenPathLeaseProviderTestRunner
    {
        public AzureBlobLeaseProviderTests(ITestOutputHelper output)
            :base(CreateLeaseProvider(), output)
        {
        }

        private static ILeaseProvider CreateLeaseProvider()
        {
            TestUtils.CheckForAzureStorage();
            return new AzureBlobLeaseProvider(Options.Create(new AzureBlobLeaseProviderOptions()
            {
                BlobContainerName = "test-blob-container-name"
            }.ConfigureTestDefaults()));
        }
    }
}

