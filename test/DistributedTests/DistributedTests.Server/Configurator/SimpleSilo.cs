// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace DistributedTests.Server.Configurator
{
    public class SimpleSilo : ISiloConfigurator<object>
    {
        public string Name => nameof(SimpleSilo);

        public List<Option> Options => new();

        public void Configure(ISiloBuilder siloBuilder, object parameters)
        {
            return;
        }
    }
}
