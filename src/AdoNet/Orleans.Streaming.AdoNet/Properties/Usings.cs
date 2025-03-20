// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

global using System;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Diagnostics.CodeAnalysis;
global using System.Linq;
global using System.Threading.Tasks;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Options;
global using Orleans.Configuration;
global using Orleans.Configuration.Overrides;
global using Orleans.Hosting;
global using Orleans.Providers.Streams.Common;
global using Orleans.Runtime;
global using Orleans.Serialization;
global using Orleans.Streaming.AdoNet.Storage;
global using Orleans.Streams;
