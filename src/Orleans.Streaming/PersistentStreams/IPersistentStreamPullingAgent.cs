// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Providers.Streams.Common;

namespace Orleans.Streams;

internal interface IPersistentStreamPullingAgent : ISystemTarget, IStreamProducerExtension
{
    Task Initialize();
    Task Shutdown();
}

internal interface IPersistentStreamPullingManager : ISystemTarget
{
    Task Initialize();
    Task Stop();
    Task StartAgents();
    Task StopAgents();
    Task<object> ExecuteCommand(PersistentStreamProviderCommand command, object arg);
}
