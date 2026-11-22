using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The faults <see cref="SimulatedRpc"/> carries from Clockwork's network and nodes fire as configured: loss,
/// partitions, pauses, duplication, a server which is down, and a caller which cancels; and the transport leaves nothing
/// armed after teardown.
/// </summary>
public sealed class SimulatedRpcTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// A network which loses every message fails each call at its deadline, and the server serves nothing.
    /// </summary>
    [Fact]
    public void ALosingNetworkFailsEveryCallAtItsDeadline() =>
        Testing.SeededSimulation.Run(1, static async environment =>
        {
            var (client, server) = Nodes(environment);
            environment.Network.MessageDropRate = 1;
            var served = 0;
            var start = environment.TimeProvider.GetUtcNow();
            for (var call = 0; call < 5; call++)
            {
                _ = await Assert.ThrowsAsync<TimeoutException>(() => environment.Rpc.CallAsync(client, server, () => ValueTask.FromResult(++served), Deadline, cancellationToken: CancellationToken.None));
            }

            Assert.Equal(0, served);
            Assert.Equal(5 * Deadline, environment.TimeProvider.GetUtcNow() - start);
        });

    /// <summary>
    /// An isolated server neither hears nor answers until it reconnects.
    /// </summary>
    [Fact]
    public void AnIsolatedServerAnswersOnlyAfterItReconnects() =>
        Testing.SeededSimulation.Run(2, static async environment =>
        {
            var (client, server) = Nodes(environment);
            var served = 0;
            environment.Network.IsolateNode(server.NetworkAddress);
            _ = await Assert.ThrowsAsync<TimeoutException>(() => environment.Rpc.CallAsync(client, server, () => ValueTask.FromResult(++served), Deadline, cancellationToken: CancellationToken.None));
            Assert.Equal(0, served);

            environment.Network.ReconnectNode(server.NetworkAddress);
            Assert.Equal(1, await environment.Rpc.CallAsync(client, server, () => ValueTask.FromResult(++served), Deadline, cancellationToken: CancellationToken.None));
        });

    /// <summary>
    /// A paused server serves late: a call whose deadline passes first fails, and the server still serves it once the
    /// pause ends, discarding the answer nobody receives.
    /// </summary>
    [Fact]
    public void APausedServerServesAfterThePauseAndItsLateAnswerIsDiscarded() =>
        Testing.SeededSimulation.Run(3, static async environment =>
        {
            var (client, server) = Nodes(environment);
            var served = 0;
            var discarded = 0;
            var pause = 3 * Deadline;
            server.SuspendFor(pause);
            var start = environment.TimeProvider.GetUtcNow();
            _ = await Assert.ThrowsAsync<TimeoutException>(() => environment.Rpc.CallAsync(
                client, server, () => ValueTask.FromResult(++served), Deadline, _ => discarded++, CancellationToken.None));
            Assert.Equal(0, served);

            await Task.Delay(pause, environment.TimeProvider);
            Assert.Equal(1, served);
            Assert.Equal(1, discarded);
            Assert.True(environment.TimeProvider.GetUtcNow() - start >= pause);
        });

    /// <summary>
    /// A server whose process is down sends nothing, so its caller observes only the deadline; any other failure is
    /// the server's answer.
    /// </summary>
    [Fact]
    public void ADownServerSendsNothingWhileAFailingServerAnswersWithItsFailure() =>
        Testing.SeededSimulation.Run(4, static async environment =>
        {
            var (client, server) = Nodes(environment);
            var start = environment.TimeProvider.GetUtcNow();
            _ = await Assert.ThrowsAsync<TimeoutException>(() => environment.Rpc.CallAsync<int>(
                client, server, () => throw new NodeDownException(server.NetworkAddress), Deadline, cancellationToken: CancellationToken.None));
            Assert.Equal(Deadline, environment.TimeProvider.GetUtcNow() - start);

            start = environment.TimeProvider.GetUtcNow();
            _ = await Assert.ThrowsAsync<InvalidDataException>(() => environment.Rpc.CallAsync<int>(
                client, server, () => throw new InvalidDataException("The request was malformed."), Deadline, cancellationToken: CancellationToken.None));
            Assert.Equal(2 * Delay, environment.TimeProvider.GetUtcNow() - start);
        });

    /// <summary>
    /// A token cancelled before the call sends nothing. One cancelled after it ends the caller's wait at once, while
    /// the server still serves the request and its late answer is discarded (ledger-replicas.md §6.3).
    /// </summary>
    [Fact]
    public void CancellationEndsTheWaitWhileTheRequestMayStillLand() =>
        Testing.SeededSimulation.Run(5, static async environment =>
        {
            var (client, server) = Nodes(environment);
            var served = 0;
            var discarded = 0;
            using var cancelled = new CancellationTokenSource();
            Cancel(cancelled);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => environment.Rpc.CallAsync(
                client, server, () => ValueTask.FromResult(++served), Deadline, _ => discarded++, cancelled.Token));
            await Task.Delay(Deadline, environment.TimeProvider);
            Assert.Equal(0, served);

            using var cancellation = new CancellationTokenSource();
            var call = environment.Rpc.CallAsync(client, server, () => ValueTask.FromResult(++served), Deadline, _ => discarded++, cancellation.Token);
            Cancel(cancellation);
            var start = environment.TimeProvider.GetUtcNow();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            Assert.Equal(start, environment.TimeProvider.GetUtcNow());
            await Task.Delay(Deadline, environment.TimeProvider);
            Assert.Equal(1, served);
            Assert.Equal(1, discarded);
        });

    /// <summary>
    /// A duplicated request is served twice, and only one answer reaches the caller; the other is discarded.
    /// </summary>
    [Fact]
    public void ADuplicatedRequestIsServedTwiceAndAnsweredOnce() =>
        Testing.SeededSimulation.Run(6, static async environment =>
        {
            var (client, server) = Nodes(environment);
            environment.Rpc.DuplicationRate = 1;
            var served = 0;
            var discarded = 0;
            var answer = await environment.Rpc.CallAsync(client, server, () => ValueTask.FromResult(++served), Deadline, _ => discarded++);
            await Task.Delay(Deadline, environment.TimeProvider);
            Assert.InRange(answer, 1, 2);
            Assert.Equal(2, served);
            Assert.Equal(1, discarded);
            Assert.Equal(1, environment.Rpc.Duplicates);
        });

    /// <summary>
    /// Cancels on the simulation's thread: a test's own token source runs its callbacks where it cancels, and
    /// <see cref="CancellationTokenSource.CancelAsync"/> would run them on the thread pool, outside the simulation.
    /// </summary>
    private static void Cancel(CancellationTokenSource source) => source.Cancel();

    private static (SimulationNode Client, SimulationNode Server) Nodes(SimulationEnvironment environment)
    {
        environment.Network.BaseMessageDelay = Delay;
        environment.Network.MaxJitter = TimeSpan.Zero;
        return (environment.Cluster.AddNode("client"), environment.Cluster.AddNode("server"));
    }
}
