using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.TestingHost;

namespace TestExtensions
{
    public class DefaultClusterFixture : Xunit.IAsyncLifetime
    {
        static DefaultClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
        }

        public InProcessTestCluster HostedCluster { get; private set; }

        public IGrainFactory GrainFactory => this.HostedCluster?.Client;

        public IClusterClient Client => this.HostedCluster?.Client;

        public ILogger Logger { get; private set; }

        public virtual async Task InitializeAsync()
        {
            var builder = new InProcessTestClusterBuilder();
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder
                    .Configure<SiloMessagingOptions>(o => o.ClientGatewayShutdownNotificationTimeout = default)
                    .UseInMemoryReminderService()
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("MemoryStore");
            });

            ConfigureCluster(builder);
            var testCluster = builder.Build();
            await testCluster.DeployAsync().ConfigureAwait(false);

            this.HostedCluster = testCluster;
            this.Logger = this.Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        }

        public virtual void ConfigureCluster(InProcessTestClusterBuilder builder)
        {
        }

        public virtual async Task DisposeAsync()
        {
            var cluster = this.HostedCluster;
            if (cluster is null) return;

            try
            {
                await cluster.StopAllSilosAsync().ConfigureAwait(false);
            }
            finally
            {
                await cluster.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
