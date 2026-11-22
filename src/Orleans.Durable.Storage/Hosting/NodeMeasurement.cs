namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// A node's disk measurement in one control interval (orleans.md §5.9): <see cref="Measured"/>, with its resources;
/// <see cref="Failed"/>, logged, and measured again at the next interval; or <see cref="Stopped"/>, when the failure stopped
/// the process, after which the node governs nothing more.
/// </summary>
internal readonly union NodeMeasurement(NodeMeasurement.Measured, NodeMeasurement.Failed, NodeMeasurement.Stopped)
{
    /// <summary>The node measured its resources.</summary>
    /// <param name="Resources">The resources.</param>
    public sealed record Measured(NodeResources Resources);

    /// <summary>The measurement failed and was logged; the next interval measures again.</summary>
    public sealed record Failed
    {
        private Failed()
        {
        }

        /// <summary>Gets the one failed answer.</summary>
        public static Failed Instance { get; } = new();
    }

    /// <summary>The measurement's failure stopped the process.</summary>
    public sealed record Stopped
    {
        private Stopped()
        {
        }

        /// <summary>Gets the one stopped answer.</summary>
        public static Stopped Instance { get; } = new();
    }
}
