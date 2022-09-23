using System;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Networking;
using Orleans.Networking.Transport;
using Orleans.Networking.Transport.Security;

namespace Orleans
{
    public static class TlsMessageTransportHostingExtensions
    {
        public static void UseServerTls(
            this IMessageTransportBuilder builder,
            Func<TlsOptions> getTlsOptions)
        {
            if (getTlsOptions is null)
            {
                throw new ArgumentNullException(nameof(getTlsOptions));
            }

            var logger = builder.ApplicationServices.GetRequiredService<TransportTrace>();
            builder.AddMiddleware(originalTransport =>
            {
                return new ServerTlsMessageTransport(originalTransport, getTlsOptions(), logger);
            });
        }

        public static void UseClientTls(
            this IMessageTransportBuilder builder,
            Func<TlsOptions> getTlsOptions)
        {
            if (getTlsOptions is null)
            {
                throw new ArgumentNullException(nameof(getTlsOptions));
            }

            var logger = builder.ApplicationServices.GetRequiredService<TransportTrace>();
            builder.AddMiddleware(originalTransport =>
            {
                return new ClientTlsMessageTransport(originalTransport, getTlsOptions(), logger);
            });
        }

        internal static void ThrowNoPrivateKey(X509Certificate2 certificate, string parameterName)
        {
            throw new ArgumentException($"Certificate {certificate.ToString(verbose: true)} does not contain a private key", parameterName);
        }
    }
}
