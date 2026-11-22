using System.Globalization;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Carries requests from one simulated node to another across Clockwork's network, and their answers back
/// (test/Durable.Storage/docs/spec/verification.md §7.2).
/// </summary>
/// <remarks>
/// <para>
/// Each message crosses the network's partitions, loss, and delay, whose jitter reorders messages, and the network may
/// deliver a request twice. The server serves each delivery on its node's scheduler lane, so a suspended node serves
/// late, and only the first answer to arrive reaches the caller. The caller's deadline fails the call when no answer
/// arrives in time, and the caller's cancellation ends its wait, as a real deadline and cancellation do: the server may
/// still serve the request afterwards, so its effect may land after the caller gave up, and its late answer is
/// discarded. A token cancelled before the call sends nothing.
/// </para>
/// <para>
/// A request and its answer pass by reference, as serialization would copy them. A caller that keeps using a buffer
/// after its call returns sends a copy, and an answer which owns resources has a discard action which releases it when
/// it never reaches the caller.
/// </para>
/// <para>
/// Disposal disarms every message in flight and ends every wait, so the transport leaves no timer armed.
/// </para>
/// </remarks>
public sealed class SimulatedRpc : IAsyncDisposable
{
    /// <summary>
    /// The message of the <see cref="OperationCanceledException"/> which ends a caller's wait when the transport stops.
    /// </summary>
    public const string StoppedMessage = "The transport stopped.";

    private readonly SimulationEnvironment _environment;
    private readonly SimulationRandom _random;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stopped;
    private bool _disposed;

    internal SimulatedRpc(SimulationEnvironment environment)
    {
        _environment = environment;
        _random = environment.CreateRandom("rpc");

        // Messages which disposal cancels still read the token afterwards, which a disposed source would refuse.
        _stopped = _stopping.Token;
    }

    /// <summary>
    /// Gets or sets the probability that the network delivers a request twice.
    /// </summary>
    public double DuplicationRate { get; set; }

    /// <summary>
    /// Gets the number of requests the network delivered twice.
    /// </summary>
    public int Duplicates { get; private set; }

    /// <summary>
    /// Gets the number of messages, requests and answers, which the network's drop rate lost.
    /// </summary>
    public int Lost { get; private set; }

    /// <summary>
    /// Sends a request from <paramref name="client"/> to <paramref name="server"/> and waits for its answer.
    /// </summary>
    /// <param name="client">The calling node.</param>
    /// <param name="server">The serving node.</param>
    /// <param name="serve">
    /// Serves the request on the server, completing to answer, throwing the failure to report, or throwing
    /// <see cref="NodeDownException"/> to send nothing.
    /// </param>
    /// <param name="deadline">How long the caller waits for the answer.</param>
    /// <param name="cancellationToken">Ends the caller's wait; the request may still land.</param>
    /// <returns>A task which completes with the answer, or fails with <see cref="TimeoutException"/> at the deadline.</returns>
    public Task CallAsync(
        SimulationNode client,
        SimulationNode server,
        Func<ValueTask> serve,
        TimeSpan deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serve);
        return CallAsync(
            client,
            server,
            async () =>
            {
                await serve();
                return default(Nothing);
            },
            deadline,
            discard: null,
            cancellationToken);
    }

    /// <summary>
    /// Sends a request from <paramref name="client"/> to <paramref name="server"/> and returns its answer.
    /// </summary>
    /// <typeparam name="T">The answer's type.</typeparam>
    /// <param name="client">The calling node.</param>
    /// <param name="server">The serving node.</param>
    /// <param name="serve">
    /// Serves the request on the server, returning the answer, throwing the failure to report, or throwing
    /// <see cref="NodeDownException"/> to send nothing.
    /// </param>
    /// <param name="deadline">How long the caller waits for the answer.</param>
    /// <param name="discard">Releases an answer which never reaches the caller.</param>
    /// <param name="cancellationToken">Ends the caller's wait; the request may still land.</param>
    /// <returns>The answer, or a <see cref="TimeoutException"/> at the deadline.</returns>
    public async Task<T> CallAsync<T>(
        SimulationNode client,
        SimulationNode server,
        Func<ValueTask<T>> serve,
        TimeSpan deadline,
        Action<T>? discard = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(serve);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var call = new Call<T>(client, server, serve, discard);
        Send(call);
        if (_random.Chance(DuplicationRate))
        {
            Duplicates++;
            _environment.Trace.Record(Invariant($"rpc {client.NetworkAddress} -> {server.NetworkAddress}: request duplicated"));
            Send(call);
        }

        // The answer, the deadline, the caller's cancellation, and the transport's disposal race to end the call, and the
        // first wins; an answer which arrives later is discarded.
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(_stopped);
        _environment.Schedule(
            _environment.Cluster.SchedulerLane,
            deadline,
            () => _ = call.Answer.TrySetException(new TimeoutException(
                Invariant($"{server.NetworkAddress} did not answer {client.NetworkAddress} within {deadline.TotalMilliseconds:0.###} ms."))),
            timer.Token,
            () => _ = call.Answer.TrySetException(new OperationCanceledException(StoppedMessage)));
        using var registration = cancellationToken.Register(() => _ = call.Answer.TrySetException(new OperationCanceledException(cancellationToken)));
        try
        {
            return await call.Answer.Task;
        }
        finally
        {
            // Disarms the deadline.
            await timer.CancelAsync();
        }
    }

    /// <summary>
    /// Disarms every message in flight and ends every caller's wait.
    /// </summary>
    /// <returns>A task which completes once the transport has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync();
        _stopping.Dispose();
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private void Send<T>(Call<T> call)
    {
        if (Delivers(call.Client, call.Server))
        {
            _environment.Schedule(call.Server.Context.SchedulerLane, _environment.Network.GetMessageDelay(), () => _ = ServeAsync(call), _stopped);
        }
    }

    /// <summary>Returns whether the network delivers a message from <paramref name="source"/> to <paramref name="target"/>, counting one its drop rate loses.</summary>
    private bool Delivers(SimulationNode source, SimulationNode target)
    {
        var status = _environment.Network.CheckDelivery(source.NetworkAddress, target.NetworkAddress);
        Lost += status == DeliveryStatus.Dropped ? 1 : 0;
        return status == DeliveryStatus.Success;
    }

    private async Task ServeAsync<T>(Call<T> call)
    {
        T result;
        try
        {
            result = await call.Serve();
        }
        catch (NodeDownException)
        {
            // A process which is down sends nothing; the caller observes its deadline.
            return;
        }
#pragma warning disable CA1031 // The server's failure is its answer, which crosses the network to the caller.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Reply(call, () => _ = call.Answer.TrySetException(error), discard: null);
            return;
        }

        Reply(
            call,
            () =>
            {
                if (!call.Answer.TrySetResult(result))
                {
                    call.Discard?.Invoke(result);
                }
            },
            call.Discard is { } discard ? () => discard(result) : null);
    }

    private void Reply<T>(Call<T> call, Action deliver, Action? discard)
    {
        if (Delivers(call.Server, call.Client))
        {
            _environment.Schedule(call.Client.Context.SchedulerLane, _environment.Network.GetMessageDelay(), deliver, _stopped, discard);
        }
        else
        {
            discard?.Invoke();
        }
    }

    private readonly record struct Nothing;

    private sealed class Call<T>(SimulationNode client, SimulationNode server, Func<ValueTask<T>> serve, Action<T>? discard)
    {
        public SimulationNode Client { get; } = client;

        public SimulationNode Server { get; } = server;

        public Func<ValueTask<T>> Serve { get; } = serve;

        public Action<T>? Discard { get; } = discard;

        public TaskCompletionSource<T> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
