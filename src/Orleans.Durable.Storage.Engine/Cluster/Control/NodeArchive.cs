using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// The cluster's archive store as a node's control work uses it (orleans.md §5.6): <see cref="Lsm"/>, with the node's
/// drain worker, compaction worker, and manifest view; or <see cref="Logs"/>, with the node's archive-log drains.
/// </summary>
internal readonly union NodeArchive(NodeArchive.Lsm, NodeArchive.Logs)
{
    /// <summary>The LSM: the node drains into it and compacts its partitions, from its manifest view.</summary>
    /// <param name="Drains">The node's drain worker.</param>
    /// <param name="Compactions">The node's compaction worker.</param>
    /// <param name="Manifest">The node's manifest view.</param>
    public sealed record Lsm(DrainWorker Drains, CompactionWorker Compactions, ManifestView Manifest);

    /// <summary>Archive logs: the node's drains, its undrained keys, and its share of the request budget.</summary>
    /// <param name="Drains">The node's archive-log drains.</param>
    public sealed record Logs(ArchiveLogNodeDrains Drains);
}

/// <summary>
/// The cluster's archive store as the controller uses it (orleans.md §5.6): <see cref="Lsm"/>, whose manifest it commits
/// and whose compactions it assigns; or <see cref="Logs"/>, whose request budget it shares among the nodes.
/// </summary>
internal readonly union ClusterArchive(ClusterArchive.Lsm, ClusterArchive.Logs)
{
    /// <summary>The LSM.</summary>
    /// <param name="View">The controller's manifest view, through which its committer reads and to which it gives each value it writes.</param>
    /// <param name="Committer">The manifest's committer.</param>
    /// <param name="Options">The archive's options.</param>
    public sealed record Lsm(ManifestView View, ManifestCommitter Committer, ArchiveOptions Options);

    /// <summary>Archive logs.</summary>
    public sealed record Logs
    {
        private Logs()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Logs Instance { get; } = new();
    }
}
