// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestExtensions;
using Xunit;

namespace Tester.Redis;

// Assembly collections must be defined once in each assembly

[CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
public class TestEnvironmentFixtureCollection : ICollectionFixture<CommonFixture> { }