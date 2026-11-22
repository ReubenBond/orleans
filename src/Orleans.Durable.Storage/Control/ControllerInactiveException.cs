
namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The controller grain's activation is deactivating (orleans.md §5.6): it refused a submission before handing it to a
/// committer, so the change was not submitted and never applies. A node's commit delegate relays it as the one definite
/// refusal; every other failure of a commit call leaves the change's outcome unknown.
/// </summary>
internal sealed class ControllerInactiveException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="ControllerInactiveException"/> class.</summary>
    public ControllerInactiveException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ControllerInactiveException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ControllerInactiveException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ControllerInactiveException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ControllerInactiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
