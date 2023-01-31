using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;

namespace TestExtensions
{
    public class SerializationTestEnvironment : IDisposable
    {
        public SerializationTestEnvironment(Action<IClientBuilder> configureClientBuilder = null)
        {
            var host = new HostBuilder()
                .UseOrleansClient((ctx, clientBuilder) =>
                {
                    clientBuilder.UseLocalhostClustering();
                    configureClientBuilder?.Invoke(clientBuilder);
                })
                .ConfigureServices(services =>
                {
                    services.RemoveAll(typeof(ClientMessageCenter));
                    services.AddSingleton<ClientMessageCenter>(sp => new ClientMessageCenter(
                        sp.GetRequiredService<IOptions<ClientMessagingOptions>>(),
                        IPAddress.Loopback,
                        -1000,
                        ClientGrainId.Create(),
                        sp.GetRequiredService<OutsideRuntimeClient>(),
                        sp.GetRequiredService<MessageFactory>(),
                        sp.GetRequiredService<IClusterConnectionStatusListener>(),
                        sp.GetRequiredService<ILoggerFactory>(),
                        sp.GetRequiredService<ConnectionManager>(),
                        sp.GetRequiredService<GatewayManager>()));
                })
                .Build();

            this.Client = host.Services.GetRequiredService<IClusterClient>();
            this.RuntimeClient = this.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
            RuntimeClient.ConsumeServices();
        }

        public IClusterClient Client { get; set; }
        
        internal OutsideRuntimeClient RuntimeClient { get; set; }

        public static SerializationTestEnvironment InitializeWithDefaults(Action<IClientBuilder> configureClientBuilder = null)
        {
            var result = new SerializationTestEnvironment(configureClientBuilder);
            return result;
        }
        
        public IGrainFactory GrainFactory => this.RuntimeClient.InternalGrainFactory;

        internal IInternalGrainFactory InternalGrainFactory => this.RuntimeClient.InternalGrainFactory;

        internal IServiceProvider Services => this.Client.ServiceProvider;

        public DeepCopier DeepCopier => this.RuntimeClient.ServiceProvider.GetRequiredService<DeepCopier>();
        public Serializer Serializer => RuntimeClient.ServiceProvider.GetRequiredService<Serializer>();
        
        public void Dispose()
        {
            this.RuntimeClient?.Dispose();
        }
    }
}