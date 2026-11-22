using System.Globalization;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Records a history of operations as clients invoke them and they respond, timestamped by a
/// <see cref="TimeProvider"/>: virtual time in a simulation or under <c>FakeTimeProvider</c>, real time otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="Invoke(int, TInput, object?)"/> or <see cref="Invoke(TInput, object?)"/> immediately before an operation starts and <see cref="Complete"/> immediately after it
/// returns, so each recorded interval contains the operation's execution. Recording is thread-safe.
/// </para>
/// <para>
/// Timestamps strictly increase in the order the recorder records events: a timestamp which would not exceed the
/// previous one is the previous one plus one tick. An operation which returned before another was invoked is
/// therefore ordered before it even when a virtual clock did not advance in between, while operations which overlapped
/// stay concurrent. The history is as precise as the process's own order of events, and the timestamps stay within a
/// few ticks of the clock's.
/// </para>
/// </remarks>
internal sealed class HistoryRecorder<TInput, TOutput>
{
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly List<(HistoryOperation<TInput, TOutput> Operation, bool Completed)> _operations = [];
    private long _last = long.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryRecorder{TInput, TOutput}"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock whose timestamps the history carries.</param>
    public HistoryRecorder(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Records an invocation.
    /// </summary>
    /// <param name="clientId">The zero-based invoking client.</param>
    /// <param name="input">The operation and its arguments.</param>
    /// <param name="metadata">Information shown with the operation in the visualization.</param>
    /// <returns>The handle with which to record the response.</returns>
    public Invocation Invoke(int clientId, TInput input, object? metadata = null)
    {
        lock (_lock)
        {
            return Add(clientId, input, metadata);
        }
    }

    /// <summary>
    /// Records an invocation by a client of its own, whose ID is the operation's position in the history: one of the
    /// requests a process has in flight at once.
    /// </summary>
    /// <param name="input">The operation and its arguments.</param>
    /// <param name="metadata">Information shown with the operation in the visualization.</param>
    /// <returns>The handle with which to record the response.</returns>
    public Invocation Invoke(TInput input, object? metadata = null)
    {
        lock (_lock)
        {
            return Add(_operations.Count, input, metadata);
        }
    }

    /// <summary>
    /// Records a response.
    /// </summary>
    /// <param name="invocation">The invocation it answers.</param>
    /// <param name="output">The response.</param>
    /// <param name="metadata">Information shown in the visualization, replacing the invocation's when not null.</param>
    public void Complete(Invocation invocation, TOutput output, object? metadata = null) => Respond(invocation, output, metadata, unbounded: false);

    /// <summary>
    /// Records a response whose effect, if any, may take place at any later time, such as an ambiguous write: the
    /// operation's response time is <see cref="HistoryOperation.Unbounded"/>.
    /// </summary>
    /// <param name="invocation">The invocation it answers.</param>
    /// <param name="output">The response, which the model must accept in any state.</param>
    /// <param name="metadata">Information shown in the visualization, replacing the invocation's when not null.</param>
    public void CompleteUnbounded(Invocation invocation, TOutput output, object? metadata = null) => Respond(invocation, output, metadata, unbounded: true);

    /// <summary>
    /// Returns a timestamp on the history's clock, ordered after every event recorded so far, for an annotation.
    /// </summary>
    /// <returns>The timestamp.</returns>
    public long Now()
    {
        lock (_lock)
        {
            return Timestamp();
        }
    }

    /// <summary>
    /// Returns the history recorded so far, in invocation order.
    /// </summary>
    /// <param name="pendingOutput">
    /// The output of an operation which has no response yet, which the model must accept in any state; such an operation
    /// has an unbounded response time. When <see langword="null"/>, a pending operation is an error.
    /// </param>
    /// <returns>The operations.</returns>
    public HistoryOperation<TInput, TOutput>[] History(Func<TInput, TOutput>? pendingOutput = null)
    {
        lock (_lock)
        {
            var history = new HistoryOperation<TInput, TOutput>[_operations.Count];
            for (var index = 0; index < history.Length; index++)
            {
                var (operation, completed) = _operations[index];
                if (!completed)
                {
                    operation = pendingOutput is not null
                        ? operation with { Output = pendingOutput(operation.Input) }
                        : throw new InvalidOperationException(
                            string.Create(CultureInfo.InvariantCulture, $"Operation {index} ({operation.Input}) of client {operation.ClientId} has no response."));
                }

                history[index] = operation;
            }

            return history;
        }
    }

    private void Respond(Invocation invocation, TOutput output, object? metadata, bool unbounded)
    {
        lock (_lock)
        {
            if ((uint)invocation.Index >= (uint)_operations.Count || _operations[invocation.Index].Completed)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Invocation {invocation.Index} is not pending."));
            }

            var operation = _operations[invocation.Index].Operation;
            _operations[invocation.Index] = (
                operation with
                {
                    Output = output,
                    Return = unbounded ? HistoryOperation.Unbounded : Timestamp(),
                    Metadata = metadata ?? operation.Metadata,
                },
                true);
        }
    }

    private Invocation Add(int clientId, TInput input, object? metadata)
    {
        _operations.Add((new HistoryOperation<TInput, TOutput>(clientId, input, Timestamp(), default!, HistoryOperation.Unbounded, metadata), false));
        return new Invocation(_operations.Count - 1);
    }

    private long Timestamp()
    {
        var now = _timeProvider.GetTimestamp();
        _last = now > _last ? now : _last + 1;
        return _last;
    }

    /// <summary>
    /// A recorded invocation awaiting its response.
    /// </summary>
    /// <param name="Index">The operation's position in the history.</param>
    internal readonly record struct Invocation(int Index);
}
