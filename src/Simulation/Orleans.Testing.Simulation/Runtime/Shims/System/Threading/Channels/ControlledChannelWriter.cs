using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Channels;

/// <summary>
/// Controlled <see cref="ChannelWriter{T}.WriteAsync"/>. The base implementation, which a writer that does not override
/// it inherits, is an async method compiled into the channels assembly whose await of
/// <see cref="ChannelWriter{T}.WaitToWriteAsync"/> resumes on the thread pool. The rewriter redirects the call sites here
/// (receiver first). A writer which overrides <c>WriteAsync</c>, as the built-in channels' writers do, is called as
/// written; otherwise the shim runs the BCL's algorithm with the same calls on the writer, awaiting through the
/// controlled awaiter, so every step runs on the simulation's logical thread.
/// </summary>
public static class ControlledChannelWriter
{
    private const string WriteAsyncApi = "System.Threading.Channels.ChannelWriter`1.WriteAsync";

    /// <summary>Controlled <see cref="ChannelWriter{T}.WriteAsync(T, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="item">The element to write.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>A value task that completes when the element has been written.</returns>
    public static ValueTask WriteAsync<T>(ChannelWriter<T> writer, T item, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return WriterOverrides<T>.Of(writer).OverridesWriteAsync
            ? writer.WriteAsync(item, cancellationToken)
            : BaseWriteAsyncCore(writer, item, cancellationToken);
    }

    /// <summary>
    /// Controlled base call of <see cref="ChannelWriter{T}.WriteAsync(T, CancellationToken)"/> (<c>base.WriteAsync</c>
    /// in an override): the BCL's base algorithm, whatever the writer overrides.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="writer">The receiving writer.</param>
    /// <param name="item">The element to write.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>A value task that completes when the element has been written.</returns>
    public static ValueTask BaseWriteAsync<T>(ChannelWriter<T> writer, T item, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WriteAsyncApi);
        return BaseWriteAsyncCore(writer, item, cancellationToken);
    }

    // Mirrors ChannelWriter<T>.WriteAsync in System.Threading.Channels verbatim, so its outcomes are the BCL's, including
    // its quirks: the canceled task is a Task.FromCanceled<T>, and every exception, a TryWrite's ChannelClosedException
    // included, gives a faulted value task rather than propagating, where the base ReadAsync lets those propagate.
    private static ValueTask BaseWriteAsyncCore<T>(ChannelWriter<T> writer, T item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        try
        {
            return
                cancellationToken.IsCancellationRequested ? new ValueTask(Task.FromCanceled<T>(cancellationToken)) :
                writer.TryWrite(item) ? default :
                WriteAsyncCore(writer, item, cancellationToken);
        }
        catch (Exception exception)
        {
            return new ValueTask(Task.FromException(exception));
        }
    }

    private static async ValueTask WriteAsyncCore<T>(ChannelWriter<T> writer, T item, CancellationToken cancellationToken)
    {
        while (await new CoordinatedValueTaskAwaitable<bool>(writer.WaitToWriteAsync(cancellationToken)))
        {
            if (writer.TryWrite(item))
            {
                return;
            }
        }

        throw new ChannelClosedException();
    }

    // Records, per writer type, whether it overrides WriteAsync. A delegate bound to a virtual member binds to the most
    // derived override, whose declaring type names the class which supplies it.
    private sealed class WriterOverrides<T>
    {
        private static readonly ConditionalWeakTable<Type, WriterOverrides<T>> Cache = [];

        private WriterOverrides(ChannelWriter<T> writer) =>
            OverridesWriteAsync = ((Func<T, CancellationToken, ValueTask>)writer.WriteAsync).Method.DeclaringType
                != typeof(ChannelWriter<T>);

        public bool OverridesWriteAsync { get; }

        // The factory is static and takes the writer as its argument, so a lookup allocates nothing.
        public static WriterOverrides<T> Of(ChannelWriter<T> writer) =>
            Cache.GetOrAdd(writer.GetType(), static (_, instance) => new WriterOverrides<T>(instance), writer);
    }
}
