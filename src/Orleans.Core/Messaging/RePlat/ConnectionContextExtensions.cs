using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Orleans.Runtime.Messaging
{
    internal static class ConnectionContextExtensions
    {
        public static string GetRemoteEndPoint(this ConnectionContext connection)
        {
            var feature = connection.GetRequiredFeature<IHttpConnectionFeature>();
            return $"{feature.RemoteIpAddress}:{feature.RemotePort}";
        }

        public static IConnectionLifetimeFeature GetLifetime(this ConnectionContext connection) => connection.GetRequiredFeature<IConnectionLifetimeFeature>();

        public static TFeature GetRequiredFeature<TFeature>(this ConnectionContext connection) where TFeature : class
        {
            return connection.Features.Get<TFeature>() ?? ThrowMissingFeature();

            TFeature ThrowMissingFeature() => throw new InvalidOperationException($"Connection does not have required {typeof(TFeature)} feature.");
        }

        public static ConnectionMessageSender GetMessageSender(this ConnectionContext connection)
        {
            return (ConnectionMessageSender)connection.Items[ConnectionMessageSender.ContextItemKey];
        }
    }
}
