
namespace Orleans.Durable.Storage.Control;

/// <summary>
/// A registration's outcome is unknown: the MetaStore did not answer the write which carried it, or the read which
/// decides it, or it was decided again. The registration may still apply, and the silo registers again (conventions.md
/// §6).
/// </summary>
internal sealed class NodeRegistryUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NodeRegistryUnavailableException"/> class.</summary>
    public NodeRegistryUnavailableException()
        : base("The node registry did not answer; the registration's outcome is unknown.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NodeRegistryUnavailableException"/> class.</summary>
    public NodeRegistryUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NodeRegistryUnavailableException"/> class.</summary>
    public NodeRegistryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Wraps <paramref name="cause"/>, the failure which left the outcome unknown.</summary>
    internal static NodeRegistryUnavailableException Of(Exception cause) =>
        cause as NodeRegistryUnavailableException ?? new("The node registry did not answer; the registration's outcome is unknown.", cause);
}
