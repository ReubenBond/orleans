using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Clustering.Cosmos;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("AzureCosmosDB", "Clustering", "Silo", typeof(CosmosClusteringProviderBuilder))]
[assembly: RegisterProvider("AzureCosmosDB", "Clustering", "Client", typeof(CosmosClusteringProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class CosmosClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseCosmosClustering((OptionsBuilder<CosmosClusteringOptions> optionsBuilder) =>
        {
            optionsBuilder.Bind(configurationSection);
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var containerName = configurationSection[nameof(CosmosClusteringOptions.ContainerName)];
                if (!string.IsNullOrEmpty(containerName))
                {
                    options.ContainerName = containerName;
                }

                var databaseName = configurationSection[nameof(CosmosClusteringOptions.DatabaseName)];
                if (!string.IsNullOrEmpty(databaseName))
                {
                    options.DatabaseName = databaseName;
                }

                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Get a client by name.
                    options.ConfigureCosmosClient(sp => ValueTask.FromResult(sp.GetRequiredKeyedService<CosmosClient>(serviceKey)));
                }
                else
                {
                    throw new InvalidOperationException("The 'ServiceKey' property must be specified.");
                }
            });
        });
    }

    public void Configure(IClientBuilder builder, string name, IConfigurationSection configurationSection)
    {
        builder.UseCosmosGatewayListProvider((OptionsBuilder<CosmosClusteringOptions> optionsBuilder) =>
        {
            optionsBuilder.Bind(configurationSection);
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var containerName = configurationSection[nameof(CosmosClusteringOptions.ContainerName)];
                if (!string.IsNullOrEmpty(containerName))
                {
                    options.ContainerName = containerName;
                }

                var databaseName = configurationSection[nameof(CosmosClusteringOptions.DatabaseName)];
                if (!string.IsNullOrEmpty(databaseName))
                {
                    options.DatabaseName = databaseName;
                }

                var serviceKey = configurationSection["ServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Get a client by name.
                    options.ConfigureCosmosClient(sp => ValueTask.FromResult(sp.GetRequiredKeyedService<CosmosClient>(serviceKey)));
                }
                else
                {
                    throw new InvalidOperationException("The 'ServiceKey' property must be provided.");
                }
            });
        });
    }
}