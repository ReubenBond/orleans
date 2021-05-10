#if false
using Microsoft.Extensions.DependencyInjection;
using Orleans.MetadataStore;
using Orleans.MetadataStore.Storage;

namespace Orleans.Hosting
{
    public static class MetadataStoreSiloHostBuilderExtensions
    {
        public static ISiloHostBuilder UseMemoryLocalStore(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services => services.AddSingleton<ILocalStore, MemoryLocalStore>());
        }

        public static ISiloBuilder UseMemoryLocalStore(this ISiloBuilder builder)
        {
            return builder.ConfigureServices(services => services.AddSingleton<ILocalStore, MemoryLocalStore>());
        }

        public static ISiloHostBuilder UseMetadataStore(this ISiloHostBuilder builder)
        {
            return builder
                .ConfigureServices((context, services) =>
                {
                    if (context.Properties.TryGetValue(nameof(UseMetadataStore), out var _))
                    {
                        return;
                    }

                    context.Properties[nameof(UseMetadataStore)] = nameof(UseMetadataStore);

                    ConfigureServices(services);
                });
        }

        public static ISiloBuilder UseMetadataStore(this ISiloBuilder builder)
        {
            return builder
                .ConfigureServices((context, services) =>
                {
                    if (context.Properties.TryGetValue(nameof(UseMetadataStore), out var _))
                    {
                        return;
                    }

                    context.Properties[nameof(UseMetadataStore)] = nameof(UseMetadataStore);

                    ConfigureServices(services);
                });
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IStoreReferenceFactory, StoreReferenceFactory>();
            services.AddSingleton<ConfigurationManager>();
            services.AddSingleton<MetadataStoreManager>();
            services.Add(new ServiceDescriptor(
                typeof(IMetadataStore),
                sp => sp.GetRequiredService<MetadataStoreManager>(),
                ServiceLifetime.Singleton));
        }
    }
}
#endif
