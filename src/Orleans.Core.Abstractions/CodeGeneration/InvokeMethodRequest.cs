using System;


namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Data object holding metadata associated with a grain Invoke request.
    /// </summary>
    [Serializable]
    public sealed class InvokeMethodRequest
    {
        /// <summary> InterfaceId for this Invoke request. </summary>
        public int InterfaceId { get; private set; }
        public ushort InterfaceVersion { get; private set; }
        /// <summary> MethodId for this Invoke request. </summary>
        public int MethodId { get; private set; }
        /// <summary> Arguments for this Invoke request. </summary>
        public object[] Arguments { get; private set; }

        internal InvokeMethodRequest(int interfaceId, ushort interfaceVersion, int methodId, object[] arguments)
        {
            InterfaceId = interfaceId;
            InterfaceVersion = interfaceVersion;
            MethodId = methodId;
            Arguments = arguments;
        }

        /// <summary> 
        /// String representation for this Invoke request. 
        /// </summary>
        /// <remarks>
        /// Note: This is not the serialized wire form of this Invoke request.
        /// </remarks>
        public override string ToString()
        {
            return String.Format("InvokeMethodRequest {0}:{1}", InterfaceId, MethodId);
        }
    }

    /// <summary>
    /// Invoke options for an <c>InvokeMethodRequest</c>
    /// </summary>
    /// <remarks>
    /// These flag values are used in Orleans generated invoker code, and should not be altered. </remarks>
    [Flags]
    public enum InvokeMethodOptions
    {
        /// <summary>No options defined.</summary>
        None = 0,

        /// <summary>Invocation is one-way with no feedback on whether the call succeeds or fails.</summary>
        OneWay = 0x04,

        /// <summary>Invocation is read-only and can interleave with other read-only invocations.</summary>
        ReadOnly = 0x08,

        /// <summary>Invocation does not care about ordering and can consequently be optimized.</summary>
        Unordered = 0x10,


        /// <summary>Obsolete field.</summary>
        [Obsolete]
        DelayForConsistency = 0x20,

        /// <summary>The invocation can interleave with any other request type, including write requests.</summary>
        AlwaysInterleave = 0x100,

        // Transactional method options.  
        // NOTE: keep in sync with TransactionOption enum.
        // We use a mask to define a set of bits we use for transaction options.
        TransactionMask = 0xE00,
        TransactionSuppress = 0x200,
        TransactionCreateOrJoin = 0x400,
        TransactionCreate = 0x600,
        TransactionJoin = 0x800,
        TransactionSupported = 0xA00,
        TransactionNotAllowed = 0xC00,
    }

    public static class InvokeMethodOptionsExtensions
    {
        public static bool IsTransactional(this InvokeMethodOptions options)
        {
            return (options & InvokeMethodOptions.TransactionMask) != 0;
        }

        public static bool IsTransactionOption(this InvokeMethodOptions options, InvokeMethodOptions test)
        {
            return (options & InvokeMethodOptions.TransactionMask) == test;
        }
    }

    /// <summary>
    /// Represents an object which holds an invocation target as well as target extensions.
    /// </summary>
    public interface IGrainHolder
    {
        /// <summary>
        /// Gets the target.
        /// </summary>
        /// <typeparam name="TTarget">The target type.</typeparam>
        /// <returns>The target.</returns>
        TTarget GetTarget<TTarget>();

        /// <summary>
        /// Gets the extension object with the specified type.
        /// </summary>
        /// <typeparam name="TExtension">The extension type.</typeparam>
        /// <returns>The extension object with the specified type.</returns>
        TExtension GetExtension<TExtension>();
    }

    /// <summary>
    /// Represents an object which can be invoked asynchronously.
    /// </summary>
    public interface IInvokable
    {
        /// <summary>
        /// Gets the invocation target.
        /// </summary>
        /// <typeparam name="TTarget">The target type.</typeparam>
        /// <returns>The invocation target.</returns>
        TTarget GetTarget<TTarget>();

        /// <summary>
        /// Sets the invocation target from an instance of <see cref="IGrainHolder"/>.
        /// </summary>
        /// <typeparam name="TGrainHolder">The target holder type.</typeparam>
        /// <param name="holder">The invocation target.</param>
        void SetTarget<TGrainHolder>(TGrainHolder holder) where TGrainHolder : IGrainHolder;

        /// <summary>
        /// Invoke the object.
        /// </summary>
        /// <returns>A <see cref="ValueTask"/> which will complete when the invocation is complete.</returns>
        ValueTask Invoke();

        /// <summary>
        /// Gets or sets the result of invocation.
        /// </summary>
        /// <remarks>This property is internally set by <see cref="Invoke"/>.</remarks>
        object Result { get; set; }

        /// <summary>
        /// Gets the result.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <returns>The result.</returns>
        TResult GetResult<TResult>();

        /// <summary>
        /// Sets the result.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="value">The result value.</param>
        void SetResult<TResult>(in TResult value);

        /// <summary>
        /// Gets the number of arguments.
        /// </summary>
        int ArgumentCount { get; }

        /// <summary>
        /// Gets the argument at the specified index.
        /// </summary>
        /// <typeparam name="TArgument">The argument type.</typeparam>
        /// <param name="index">The argument index.</param>
        /// <returns>The argument at the specified index.</returns>
        TArgument GetArgument<TArgument>(int index);

        /// <summary>
        /// Sets the argument at the specified index.
        /// </summary>
        /// <typeparam name="TArgument">The argument type.</typeparam>
        /// <param name="index">The argument index.</param>
        /// <param name="value">The argument value</param>
        void SetArgument<TArgument>(int index, in TArgument value);

        /// <summary>
        /// Resets this instance.
        /// </summary>
        void Reset();
    }
}
