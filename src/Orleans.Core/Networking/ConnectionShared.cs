// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Orleans.Placement.Repartitioning;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionCommon(
        IServiceProvider serviceProvider,
        MessageFactory messageFactory,
        MessagingTrace messagingTrace,
        NetworkingTrace networkingTrace,
        IMessageStatisticsSink messageStatisticsSink)
    {
        public MessageFactory MessageFactory { get; } = messageFactory;
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public NetworkingTrace NetworkingTrace { get; } = networkingTrace;
        public IMessageStatisticsSink MessageStatisticsSink { get; } = messageStatisticsSink;
        public MessagingTrace MessagingTrace { get; } = messagingTrace;
    }
}
