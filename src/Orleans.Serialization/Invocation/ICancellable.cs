#nullable enable

using System.Threading;

namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Represents an invokable object which can have its invocation canceled.
    /// </summary>
    public interface ICancellable
    {
        /// <summary>
        /// Cancels execution of this method.
        /// </summary>
        void Cancel();

        /// <summary>
        /// Gets the cancellation token representing cancellation of this method.
        /// </summary>
        CancellationToken CancellationToken { get; }
    }
}
