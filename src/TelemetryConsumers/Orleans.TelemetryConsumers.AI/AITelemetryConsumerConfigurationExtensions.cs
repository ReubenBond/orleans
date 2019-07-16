using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.TelemetryConsumers.AI;

namespace Orleans.Hosting
{
    public static class AITelemetryConsumerConfigurationExtensions
    {
        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="AITelemetryConsumer"/>.
        /// </summary>
        /// <param name="hostBuilder"></param>
        /// <param name="instrumentationKey">The Application Insights instrumentation key.</param>
        public static ISiloBuilder AddApplicationInsightsTelemetryConsumer(this ISiloBuilder hostBuilder, string instrumentationKey = null)
        {
            return hostBuilder.ConfigureServices((context, services) => ConfigureServices(services, instrumentationKey));
        }

        /// <summary>
        /// Adds a metrics telemetric consumer provider of type <see cref="AITelemetryConsumer"/>.
        /// </summary>
        /// <param name="clientBuilder"></param>
        /// <param name="instrumentationKey">The Application Insights instrumentation key.</param>
        public static IClientBuilder AddApplicationInsightsTelemetryConsumer(this IClientBuilder clientBuilder, string instrumentationKey = null)
        {
            return clientBuilder.ConfigureServices((context, services) => ConfigureServices( services, instrumentationKey));
        }

        private static void ConfigureServices(IServiceCollection services, string instrumentationKey)
        {
            services.ConfigureFormatter<ApplicationInsightsTelemetryConsumerOptions>();
            services.Configure<TelemetryOptions>(options => options.AddConsumer<AITelemetryConsumer>());
            if (!string.IsNullOrWhiteSpace(instrumentationKey))
                services.Configure<ApplicationInsightsTelemetryConsumerOptions>(options => options.InstrumentationKey = instrumentationKey);
        }
    }
}
