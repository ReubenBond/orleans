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
            var feature = connection.Features.Get<IHttpConnectionFeature>();
            if (feature == null) return null;
            return $"{feature.RemoteIpAddress}:{feature.RemotePort}";
        }

        public static string GetLocalEndPoint(this ConnectionContext connection)
        {
            var feature = connection.Features.Get<IHttpConnectionFeature>();
            if (feature == null) return null;
            return $"{feature.LocalIpAddress}:{feature.LocalPort}";
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
