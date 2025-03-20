// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestExtensions;
using Xunit;

namespace Tester.AzureUtils
{
    public static class StorageEmulatorUtilities
    {
        public static void EnsureEmulatorIsNotUsed()
        {
            if (TestDefaultConfiguration.DataConnectionString is { Length: > 0 } connectionString
                && (connectionString.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains("devstoreaccount", StringComparison.OrdinalIgnoreCase)))
            {
                throw new SkipException("This test does not support the storage emulator.");
            }
        }
    }
}
