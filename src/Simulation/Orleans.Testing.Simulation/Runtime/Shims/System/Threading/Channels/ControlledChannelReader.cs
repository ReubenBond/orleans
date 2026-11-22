using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;
using Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;
using Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Channels;

/// <summary>
/// <para>
/// Controlled <see cref="ChannelReader{T}"/> members whose BCL implementations are async methods compiled into the
/// channels assembly: <see cref="ChannelReader{T}.ReadAllAsync"/>, and the base <see cref="ChannelReader{T}.ReadAsync"/>
/// which a reader that does not override it inherits. Their awaits register BCL continuations, which a channel's
/// asynchronously completed waiters run on the thread pool, outside the coordinator. The rewriter redirects the call
/// sites here (receiver first). A reader which overrides the member is called as written; otherwise the shim runs the
/// BCL's algorithm with the same calls on the reader, observing its <see cref="ChannelReader{T}.WaitToReadAsync"/>
/// through the coordinator, so every step runs on the simulation's logical thread.
/// </para>
/// <para>
/// The other members complete through the reader's own state, which the writers change on the logical thread, and a
/// controlled await observes their tasks, so they remain BCL calls.
/// </para>
/// </summary>
public static class ControlledChannelReader
{
    private const string ReadAsyncApi = "System.Threading.Channels.ChannelReader`1.ReadAsync";
    private const string ReadAllAsyncApi = "System.Threading.Channels.ChannelReader`1.ReadAllAsync";

    /// <summary>Controlled <see cref="ChannelReader{T}.ReadAsync(CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>A value task that completes with the element read.</returns>
    public static ValueTask<T> ReadAsync<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);
        return ReaderOverrides<T>.Of(reader).OverridesReadAsync
            ? reader.ReadAsync(cancellationToken)
            : BaseReadAsyncCore(reader, cancellationToken);
    }

    /// <summary>
    /// Controlled base call of <see cref="ChannelReader{T}.ReadAsync(CancellationToken)"/> (<c>base.ReadAsync</c> in an
    /// override): the BCL's base algorithm, whatever the reader overrides.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>A value task that completes with the element read.</returns>
    public static ValueTask<T> BaseReadAsync<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAsyncApi);
        return BaseReadAsyncCore(reader, cancellationToken);
    }

    /// <summary>Controlled <see cref="ChannelReader{T}.ReadAllAsync(CancellationToken)"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="cancellationToken">The token that cancels the enumeration.</param>
    /// <returns>An enumerable that reads every element from the channel.</returns>
    /// <remarks>
    /// As the BCL's iterator does, the enumerable reads nothing until it is enumerated; each enumeration awaits
    /// <see cref="ChannelReader{T}.WaitToReadAsync"/> and then reads with <see cref="ChannelReader{T}.TryRead"/> until it
    /// fails, and ends when <see cref="ChannelReader{T}.WaitToReadAsync"/> returns <see langword="false"/>. A token passed
    /// to <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator"/> is combined with this one, as
    /// <see cref="EnumeratorCancellationAttribute"/> combines them.
    /// </remarks>
    public static IAsyncEnumerable<T> ReadAllAsync<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAllAsyncApi);
        return ReaderOverrides<T>.Of(reader).OverridesReadAllAsync
            ? reader.ReadAllAsync(cancellationToken)
            : BaseReadAllAsyncCore(reader, cancellationToken);
    }

    /// <summary>
    /// Controlled base call of <see cref="ChannelReader{T}.ReadAllAsync(CancellationToken)"/> (<c>base.ReadAllAsync</c>
    /// in an override): the BCL's base iterator, whatever the reader overrides.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="reader">The receiving reader.</param>
    /// <param name="cancellationToken">The token that cancels the enumeration.</param>
    /// <returns>An enumerable that reads every element from the channel.</returns>
    public static IAsyncEnumerable<T> BaseReadAllAsync<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(ReadAllAsyncApi);
        return BaseReadAllAsyncCore(reader, cancellationToken);
    }

    // Mirrors ChannelReader<T>.ReadAsync in System.Threading.Channels verbatim, so its outcomes are the BCL's: a canceled
    // token gives a canceled value task before TryRead runs, a TryRead which throws gives a faulted value task, except
    // that ChannelClosedException and OperationCanceledException propagate synchronously, and a completed channel throws
    // ChannelClosedException.
    private static ValueTask<T> BaseReadAsyncCore<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (cancellationToken.IsCancellationRequested)
        {
            return new ValueTask<T>(Task.FromCanceled<T>(cancellationToken));
        }

        try
        {
            if (reader.TryRead(out T? item))
            {
                return new ValueTask<T>(item);
            }
        }
        catch (Exception exception) when (exception is not (ChannelClosedException or OperationCanceledException))
        {
            return new ValueTask<T>(Task.FromException<T>(exception));
        }

        return ReadAsyncCore(reader, cancellationToken);
    }

    private static async ValueTask<T> ReadAsyncCore<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!await new CoordinatedValueTaskAwaitable<bool>(reader.WaitToReadAsync(cancellationToken)))
            {
                throw new ChannelClosedException();
            }

            if (reader.TryRead(out T? item))
            {
                return item;
            }
        }
    }

    private static ReadAllAsyncEnumerable<T> BaseReadAllAsyncCore<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ReadAllAsyncEnumerable<T>(reader, cancellationToken);
    }

    // Records, per reader type, whether it overrides ReadAsync and ReadAllAsync. A delegate bound to a virtual member
    // binds to the most derived override, whose declaring type names the class which supplies it.
    private sealed class ReaderOverrides<T>
    {
        private static readonly ConditionalWeakTable<Type, ReaderOverrides<T>> Cache = [];

        private ReaderOverrides(ChannelReader<T> reader)
        {
            OverridesReadAsync = ((Func<CancellationToken, ValueTask<T>>)reader.ReadAsync).Method.DeclaringType
                != typeof(ChannelReader<T>);
            OverridesReadAllAsync = ((Func<CancellationToken, IAsyncEnumerable<T>>)reader.ReadAllAsync).Method.DeclaringType
                != typeof(ChannelReader<T>);
        }

        public bool OverridesReadAsync { get; }

        public bool OverridesReadAllAsync { get; }

        // The factory is static and takes the reader as its argument, so a lookup allocates nothing.
        public static ReaderOverrides<T> Of(ChannelReader<T> reader) =>
            Cache.GetOrAdd(reader.GetType(), static (_, instance) => new ReaderOverrides<T>(instance), reader);
    }

    private sealed class ReadAllAsyncEnumerable<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
        : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken enumeratorCancellationToken = default)
        {
            SimulationRuntimeDispatch.RequireActiveSimulation(ReadAllAsyncApi);
            if (cancellationToken.Equals(default))
            {
                return new ReadAllAsyncEnumerator<T>(reader, enumeratorCancellationToken);
            }

            if (enumeratorCancellationToken.Equals(cancellationToken) || enumeratorCancellationToken.Equals(default))
            {
                return new ReadAllAsyncEnumerator<T>(reader, cancellationToken);
            }

            // A linked source would unregister from both tokens when disposed, waiting for a cancellation callback
            // running on an operation the coordinator has paused; the enumerator unregisters without waiting instead.
            var combined = new CancellationTokenSource();
            return new ReadAllAsyncEnumerator<T>(
                reader,
                combined.Token,
                cancellationToken.UnsafeRegister(CancelCombined, combined),
                enumeratorCancellationToken.UnsafeRegister(CancelCombined, combined));
        }

        private static void CancelCombined(object? combined) =>
            SimulationCompletion.Cancel((CancellationTokenSource)combined!, ReadAllAsyncApi);
    }

    /// <summary>
    /// <para>
    /// The BCL's <c>while (await WaitToReadAsync(ct)) while (TryRead(out item)) yield return item;</c>. Like the
    /// compiler-generated iterator, the enumerator is the value-task source of its own <see cref="MoveNextAsync"/>, so
    /// a step allocates nothing.
    /// </para>
    /// <para>
    /// A pending step's status is the status of the <see cref="ChannelReader{T}.WaitToReadAsync"/> value task it waits
    /// on. A controlled await polls that status through the coordinator, registering nothing with the source, and then
    /// calls <see cref="GetResult"/>, which reads the element: a wake costs the consumer's own controlled hop and
    /// nothing else. A reader which loses the element to another reader between the wait and its
    /// <see cref="ChannelReader{T}.TryRead"/> waits again within <see cref="GetResult"/>, pumping the coordinator as a
    /// controlled synchronous wait does.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    private sealed class ReadAllAsyncEnumerator<T>(
        ChannelReader<T> reader,
        CancellationToken cancellationToken,
        CancellationTokenRegistration firstLink = default,
        CancellationTokenRegistration secondLink = default)
        : IAsyncEnumerator<T>, IValueTaskSource<bool>
    {
        private ValueTask<bool> _wait;
        private ExceptionDispatchInfo? _failure;
        private short _version;
        private bool _waiting;
        private bool _yielded;
        private bool _finished;

        public T Current { get; private set; } = default!;

        public ValueTask<bool> MoveNextAsync()
        {
            SimulationRuntimeDispatch.RequireActiveSimulation(ReadAllAsyncApi);
            if (_finished)
            {
                return new ValueTask<bool>(false);
            }

            try
            {
                // After a yield the iterator resumes in its inner loop, with a TryRead; it first waits otherwise.
                if (_yielded && TryYield())
                {
                    return new ValueTask<bool>(true);
                }

                while (true)
                {
                    ValueTask<bool> wait = reader.WaitToReadAsync(cancellationToken);
                    if (!wait.IsCompleted)
                    {
                        _wait = wait;
                        _waiting = true;
                        return new ValueTask<bool>(this, ++_version);
                    }

                    if (!Consume(wait))
                    {
                        Finish();
                        return new ValueTask<bool>(false);
                    }

                    if (TryYield())
                    {
                        return new ValueTask<bool>(true);
                    }
                }
            }
            catch (Exception exception)
            {
                // The iterator records the failure in its own source, which reports an OperationCanceledException as
                // canceled and anything else as faulted.
                Finish();
                _failure = ExceptionDispatchInfo.Capture(exception);
                return new ValueTask<bool>(this, ++_version);
            }
        }

        public ValueTask DisposeAsync()
        {
            // The compiler-generated iterator cannot be disposed while a MoveNextAsync is pending. Disposal changes no
            // simulation state, so it needs no active simulation.
            if (_waiting)
            {
                throw new NotSupportedException();
            }

            Finish();
            return default;
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            ValidateToken(token);
            if (_failure is { } failure)
            {
                return failure.SourceException is OperationCanceledException
                    ? ValueTaskSourceStatus.Canceled
                    : ValueTaskSourceStatus.Faulted;
            }

            ValueTask<bool> wait = _wait;
            return !wait.IsCompleted ? ValueTaskSourceStatus.Pending
                : wait.IsCompletedSuccessfully ? ValueTaskSourceStatus.Succeeded
                : wait.IsCanceled ? ValueTaskSourceStatus.Canceled
                : ValueTaskSourceStatus.Faulted;
        }

        public bool GetResult(short token)
        {
            ValidateToken(token);
            _failure?.Throw();
            ValueTask<bool> wait = _wait;
            _wait = default;
            _waiting = false;
            try
            {
                // The wait has normally completed. If another reader took the element since, the step waits again here,
                // since GetResult returns its outcome synchronously, pumping the coordinator as a controlled synchronous
                // wait does.
                bool readable = WaitSynchronously(wait);
                while (readable && !TryYield())
                {
                    readable = WaitSynchronously(reader.WaitToReadAsync(cancellationToken));
                }

                if (!readable)
                {
                    Finish();
                }

                return readable;
            }
            catch
            {
                Finish();
                throw;
            }
        }

        // Code the rewriter never sees awaits through the BCL awaiter, which registers here; the continuation runs as
        // controlled work once the step can complete, as a controlled await's does.
        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            ValidateToken(token);
            ArgumentNullException.ThrowIfNull(continuation);
            ExecutionContext? context = (flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0
                ? ExecutionContext.Capture()
                : null;
            SimulationTaskRuntime.ScheduleWhenReady(
                () => token != _version || GetStatus(token) != ValueTaskSourceStatus.Pending,
                () => SimulationTaskRuntime.RunWithCapturedExecutionContext(context, () => continuation(state)),
                ReadAllAsyncApi);
        }

        // Completes a wait, consuming the happens-before edge its completion published, as a controlled await does.
        private static bool Consume(ValueTask<bool> wait)
        {
            ControlledValueTask.WaitCompletion(wait);
            return wait.GetAwaiter().GetResult();
        }

        private static bool WaitSynchronously(ValueTask<bool> wait)
        {
            if (!wait.IsCompleted)
            {
                SimulationTaskRuntime.DrainUntilCompleted(wait, ReadAllAsyncApi);
            }

            return Consume(wait);
        }

        private bool TryYield()
        {
            if (!reader.TryRead(out T? item))
            {
                return false;
            }

            _yielded = true;
            Current = item;
            return true;
        }

        // An ended iterator's Current is default, as the compiler-generated iterator's is.
        private void Finish()
        {
            _finished = true;
            Current = default!;
            firstLink.Unregister();
            secondLink.Unregister();
        }

        private void ValidateToken(short token)
        {
            if (token != _version)
            {
                throw new InvalidOperationException("The value task was already consumed or belongs to an earlier step.");
            }
        }
    }
}
