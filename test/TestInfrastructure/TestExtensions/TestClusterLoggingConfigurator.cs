using System;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading;

namespace TestExtensions
{
    public class TestClusterLoggingConfigurator : ISiloBuilderConfigurator, IClientBuilderConfigurator
    {
        private static int ordinal;

        public void Configure(ISiloHostBuilder hostBuilder)
        {
            var ord = Interlocked.Increment(ref ordinal);
            var name = $"Silo_{DateTime.UtcNow.ToString("yyyy_MM_dd_HH_mm_ss_fff")}_{ord}_{Guid.NewGuid().ToString("N").Substring(0, 5)}.log";
            hostBuilder.ConfigureLogging(logging => logging.AddFile(name));
        }

        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            var ord = Interlocked.Increment(ref ordinal);
            var name = $"Client_{DateTime.UtcNow.ToString("yyyy_MM_dd_HH_mm_ss_fff")}_{ord}_{Guid.NewGuid().ToString("N").Substring(0, 5)}.log";
            clientBuilder.ConfigureLogging(logging => logging.AddFile(name));
        }
    }
}