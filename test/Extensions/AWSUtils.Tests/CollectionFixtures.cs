// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestExtensions;
using Xunit;

namespace AWSUtils.Tests;

// Assembly collections must be defined once in each assembly
[CollectionDefinition("DefaultCluster")]
public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }

[CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }
