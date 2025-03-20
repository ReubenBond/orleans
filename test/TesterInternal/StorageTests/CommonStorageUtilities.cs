// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;


namespace UnitTests.StorageTests.Relational;

/// <summary>
/// Common utilities to be used while testing storage providers.
/// </summary>
public static class CommonStorageUtilities
{
    /// <summary>
    /// Asserts certain information is present in the <see cref="Orleans.Storage.InconsistentStateException"/>.
    /// </summary>
    /// <param name="exceptionMessage">The exception message to assert.</param>
    public static void AssertRelationalInconsistentExceptionMessage(string exceptionMessage)
    {
        Assert.Contains("ServiceId=", exceptionMessage);
        Assert.Contains("ProviderName=", exceptionMessage);
        Assert.Contains("GrainType=", exceptionMessage);
        Assert.Contains("GrainId=", exceptionMessage);
        Assert.Contains("ETag=", exceptionMessage);
    }
}
