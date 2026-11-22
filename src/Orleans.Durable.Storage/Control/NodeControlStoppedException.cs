
namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The node's control runtime has stopped, or never started (orleans.md §5.6): the call did not start its work, and had no
/// effect. The controller treats a compaction it fails as an unknown outcome, which its timeout covers.
/// </summary>
internal sealed class NodeControlStoppedException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="NodeControlStoppedException"/> class.</summary>
    public NodeControlStoppedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NodeControlStoppedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public NodeControlStoppedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NodeControlStoppedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public NodeControlStoppedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
