using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Cassandra;
using Orleans.Runtime;

namespace Orleans.Clustering.Cassandra.Hosting;

/// <summary>
/// Options for configuring Cassandra clustering.
/// </summary>
public class CassandraClusteringOptions
{
    /// <summary>
    /// Gets or sets the legacy Cassandra TTL option. This must be <see langword="false"/>.
    /// Membership cleanup atomically removes eligible dead rows and advances the table version.
    /// </summary>
    /// <remarks>
    /// Enabling TTL is rejected during initialization, before connecting.
    /// Existing expiring cells require migration or a new cluster identifier before upgrading.
    /// </remarks>
    public bool UseCassandraTtl { get; set; }

    internal void Validate()
    {
        if (UseCassandraTtl)
        {
            throw new OrleansConfigurationException("Cassandra membership TTL must be disabled so that row removal and table version changes commit atomically. Use versioned membership cleanup instead.");
        }
    }

    /// <summary>
    /// Specifies the maximum amount of time to wait after encountering
    /// contention during initialization before retrying.
    /// </summary>
    /// <remarks>This is generally only encountered with large numbers of silos connecting
    /// in a short time period and using multi-datacenter Cassandara clusters</remarks>
    public TimeSpan InitializeRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Configures the Cassandra client.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="keyspace">The keyspace.</param>
    /// <remarks>The membership provider owns the created cluster and disposes it if initialization is canceled or the provider is disposed.</remarks>
    public void ConfigureClient(string connectionString, string keyspace = "orleans")
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(keyspace);
        OwnsSession = true;
        CreateSessionAsync = async sp =>
        {
            var c = Cluster.Builder().WithConnectionString(connectionString)
                .Build();

            var connected = false;
            try
            {
                var session = await c.ConnectAsync(keyspace).ConfigureAwait(false);
                connected = true;
                return session;
            }
            finally
            {
                if (!connected)
                {
                    c.Dispose();
                }
            }
        };
    }

    /// <summary>
    /// Configures the Cassandra client.
    /// </summary>
    /// <param name="configurationDelegate">The connection string.</param>
    /// <remarks>Sessions returned by this delegate remain owned by the caller and are not disposed by the membership provider.</remarks>
    public void ConfigureClient(Func<IServiceProvider, Task<ISession>> configurationDelegate)
    {
        ArgumentNullException.ThrowIfNull(configurationDelegate);
        OwnsSession = false;
        CreateSessionAsync = configurationDelegate;
    }

    [NotNull]
    internal Func<IServiceProvider, Task<ISession>> CreateSessionAsync { get; private set; } = default!;

    internal bool OwnsSession { get; private set; }
}
