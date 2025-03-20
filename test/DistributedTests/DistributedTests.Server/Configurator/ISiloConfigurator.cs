// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace DistributedTests.Server.Configurator;

public interface ISiloConfigurator<T>
{
    string Name { get; }

    List<Option> Options { get; }

    void Configure(ISiloBuilder siloBuilder, T parameters);
}
