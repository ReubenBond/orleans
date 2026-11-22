using Orleans.Configuration;

namespace Orleans.Durable.Storage.Tests;

/// <summary>Builds <see cref="ReplicaCallLimits"/> for tests from the options' replica limits and Orleans' messaging options.</summary>
internal static class TestReplicaLimits
{
    /// <summary>Returns the limits of options whose replica limits <paramref name="configure"/> sets, under a deadline and message body limit.</summary>
    public static ReplicaCallLimits Of(Action<DurableStorageOptions>? configure = null, TimeSpan? deadline = null, int maxMessageBodySize = 100 << 20) =>
        ReplicaCallLimits.From(Options(configure), Messaging(deadline, maxMessageBodySize));

    /// <summary>Returns valid options whose replica limits <paramref name="configure"/> sets.</summary>
    public static DurableStorageOptions Options(Action<DurableStorageOptions>? configure = null)
    {
        var options = new DurableStorageOptions
        {
            Cluster = Guid.NewGuid(),
            Root = Path.Combine(Path.GetTempPath(), "clog-orleans-unused"),
            ArchiveStore = Orleans.Durable.Storage.Engine.Archive.ArchiveStoreKind.Lsm,
        };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>Returns Orleans' messaging options with a response timeout and a message body limit.</summary>
    public static SiloMessagingOptions Messaging(TimeSpan? deadline = null, int maxMessageBodySize = 100 << 20) => new()
    {
        ResponseTimeout = deadline ?? TimeSpan.FromSeconds(30),
        MaxMessageBodySize = maxMessageBodySize,
    };
}
