#nullable enable

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
    }
}