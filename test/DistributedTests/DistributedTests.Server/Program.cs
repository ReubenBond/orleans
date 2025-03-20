// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using DistributedTests.Server;
using DistributedTests.Server.Configurator;

var root = new RootCommand();

root.Add(Server.CreateCommand(new SimpleSilo()));
root.Add(Server.CreateCommand(new EventGeneratorStreamingSilo()));

await root.InvokeAsync(args);