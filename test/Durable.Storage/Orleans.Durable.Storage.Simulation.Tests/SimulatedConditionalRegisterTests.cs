using System.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The simulated MetaStore register injects the faults its contract allows and stays within the contract: clients'
/// histories are linearizable (MS-I1), an unknown write applies at most once and only at the version it named (MS-I3),
/// absent reads differently from empty (MS-I2), and neither a failure's type nor cancellation reveals an outcome
/// (metastore.md §6).
/// </summary>
public sealed class SimulatedConditionalRegisterTests
{
    private const int Clients = 4;
    private const int Rounds = 12;

    /// <summary>
    /// Gets the seed corpus.
    /// </summary>
    public static TheoryData<int> Seeds => SeededSimulation.Corpus(1, 9);

    /// <summary>
    /// Concurrent read-then-write clients meet unknown outcomes, delayed landings, failed reads, an outage, and their
    /// own cancellations. Their history is linearizable against a conditional register, every write applies at most
    /// once, and every fault fires.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void FaultsFireWithinTheRegisterContract(int seed) =>
        SeededSimulation.Run(seed, environment => SimulateAsync(environment));

    /// <summary>
    /// The register's simulation replays from its seed and from its artifact.
    /// </summary>
    [Fact]
    public void TheRegisterSimulationReplaysFromItsSeed() =>
        SeededSimulation.RunAndReplay(101, SimulateAsync);

    /// <summary>
    /// A register which was never written reads as absent; one holding an empty value reads as present and empty, and
    /// a version from another register, or a write expecting absent, mismatches (MS-I2, MS-I5).
    /// </summary>
    [Fact]
    public void AbsentIsDistinctFromEmptyAndVersionsAreFresh() =>
        SeededSimulation.Run(4, static async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "node-registry");
            Assert.Null(await register.ReadAsync());
            Assert.False((await register.WriteAsync(new MetaStoreVersion("0"), ReadOnlySequence<byte>.Empty)).IsApplied());
            Assert.True((await register.WriteAsync(expectedVersion: null, ReadOnlySequence<byte>.Empty)).IsApplied());
            using var empty = Assert.IsType<MetaStoreEntry>(await register.ReadAsync());
            Assert.Equal(0, empty.Value.Length);
            Assert.False((await register.WriteAsync(expectedVersion: null, [1])).IsApplied());
            var applied = (await register.WriteAsync(empty.Version, [2])).AppliedVersion();
            using var written = Assert.IsType<MetaStoreEntry>(await register.ReadAsync());
            Assert.Equal(applied, written.Version);
            Assert.NotEqual(empty.Version, written.Version);
            Assert.False((await register.WriteAsync(empty.Version, [3])).IsApplied());
        });

    /// <summary>
    /// A token cancelled before a write sends nothing. One cancelled after the write is dispatched ends the caller's
    /// wait with <see cref="OperationCanceledException"/>, never <see langword="false"/>, while the write still applies
    /// (metastore.md §6).
    /// </summary>
    [Fact]
    public void CancellationBeforeDispatchSendsNothingAndAfterDispatchMayStillApply() =>
        SeededSimulation.Run(5, static async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map");
            var settle = 2 * register.MaxLatency + TimeSpan.FromMilliseconds(1);
            using (var cancelled = new CancellationTokenSource())
            {
                Cancel(cancelled);
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => register.WriteAsync(null, ConditionalValues.Encode(1), cancelled.Token).AsTask());
                await Task.Delay(settle, environment.TimeProvider);
                Assert.Null(register.Version);
            }

            using (var cancellation = new CancellationTokenSource())
            {
                var write = register.WriteAsync(null, ConditionalValues.Encode(2), cancellation.Token);
                Cancel(cancellation);
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.AsTask());
                await Task.Delay(settle, environment.TimeProvider);
                Assert.Equal([2], register.AppliedWrites.Select(ConditionalValues.Decode));
            }

            using (var cancellation = new CancellationTokenSource())
            {
                var mismatch = register.WriteAsync(null, ConditionalValues.Encode(3), cancellation.Token);
                Cancel(cancellation);
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mismatch.AsTask());
                await Task.Delay(settle, environment.TimeProvider);
                Assert.Equal([2], register.AppliedWrites.Select(ConditionalValues.Decode));
            }
        });

    /// <summary>
    /// A failed write's exception type does not reveal its outcome: writes which applied and writes which did not each
    /// fail with both types.
    /// </summary>
    [Fact]
    public void AFailuresTypeDoesNotRevealItsOutcome() =>
        SeededSimulation.Run(6, static async environment =>
        {
            await using var register = new SimulatedConditionalRegister(environment, "ledger-map") { UnknownOutcomeRate = 1 };
            List<(int Value, Type Failure)> failures = [];
            for (var value = 0; value < 60; value++)
            {
                try
                {
                    _ = await register.WriteAsync(register.Version, ConditionalValues.Encode(value));
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    failures.Add((value, exception.GetType()));
                }
            }

            // Let every delayed write land before deciding which applied.
            await Task.Delay(register.MaxLandingDelay + TimeSpan.FromMilliseconds(1), environment.TimeProvider);
            var applied = register.AppliedWrites.Select(ConditionalValues.Decode).ToHashSet();
            foreach (var type in new[] { typeof(IOException), typeof(TimeoutException) })
            {
                Assert.Contains(failures, failure => failure.Failure == type && applied.Contains(failure.Value));
                Assert.Contains(failures, failure => failure.Failure == type && !applied.Contains(failure.Value));
            }
        });

    private static async Task SimulateAsync(SimulationEnvironment environment)
    {
        await using var register = new SimulatedConditionalRegister(environment, "ledger-map")
        {
            UnknownOutcomeRate = 0.3,
            AppliedMismatchRate = 0.3,
            ReadFailureRate = 0.1,
        };
        var history = new HistoryRecorder<ConditionalInput, ConditionalOutput>(environment.TimeProvider);

        async Task OutageAsync()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30), environment.TimeProvider);
            register.IsAvailable = false;
            environment.Trace.Record("register outage begins");
            await Task.Delay(TimeSpan.FromMilliseconds(15), environment.TimeProvider);
            register.IsAvailable = true;
            environment.Trace.Record("register outage ends");
        }

        await SimulationTasks.WhenAll([.. Enumerable.Range(0, Clients).Select(client => RunClientAsync(environment, register, history, client)), OutageAsync()]);

        // Let every delayed write land before the final read.
        await Task.Delay(register.MaxLandingDelay + 2 * register.MaxLatency, environment.TimeProvider);
        _ = await ReadAsync(register, history, Clients);

#pragma warning disable RS0030 // The checker is test-host code, which only polls the test's token.
        LinearizabilityAssert.Linearizable(ConditionalValueModel.Instance, history.History(), cancellationToken: TestContext.Current.CancellationToken);
#pragma warning restore RS0030
        var applied = register.AppliedWrites.Select(ConditionalValues.Decode).ToArray();
        Assert.Equal(applied.Length, applied.Distinct().Count());
        Assert.True(register.UnknownOutcomes > 0, "No write had an unknown outcome.");
        Assert.True(register.AppliedMismatches > 0, "No write which applied answered a mismatch.");
        Assert.True(register.Failures > 0, "No request failed.");
    }

    private static async Task RunClientAsync(
        SimulationEnvironment environment,
        SimulatedConditionalRegister register,
        HistoryRecorder<ConditionalInput, ConditionalOutput> history,
        int client)
    {
        for (var round = 0; round < Rounds; round++)
        {
            await Task.Delay(environment.Random.NextTimeSpan(TimeSpan.FromMilliseconds(8)), environment.TimeProvider);
            var (read, version, expected) = await ReadAsync(register, history, client);
            if (!read)
            {
                continue;
            }

            var value = client * 1000 + round;
            var invocation = history.Invoke(client, ConditionalInput.Write(expected, value));
            using var cancellation = new CancellationTokenSource();
            var write = register.WriteAsync(version, ConditionalValues.Encode(value), cancellation.Token);
            if (environment.Random.Chance(0.1))
            {
                // The client gives up once its write is dispatched; the write may still apply.
                Cancel(cancellation);
            }

            try
            {
                var outcome = await write;
                history.Complete(invocation, new ConditionalOutput(outcome.IsApplied() ? ConditionalOutcome.Applied : ConditionalOutcome.Mismatch));
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
            {
                history.CompleteUnbounded(invocation, new ConditionalOutput(ConditionalOutcome.Failed));
            }
        }
    }

    /// <summary>
    /// Reads the register and records the read, returning whether it succeeded, and the version and value it read,
    /// <see langword="null"/> while the register is absent.
    /// </summary>
    private static async Task<(bool Read, MetaStoreVersion? Version, int? Value)> ReadAsync(
        SimulatedConditionalRegister register,
        HistoryRecorder<ConditionalInput, ConditionalOutput> history,
        int client)
    {
        var invocation = history.Invoke(client, ConditionalInput.Read());
        try
        {
            using var entry = await register.ReadAsync();
            int? value = entry is null ? null : ConditionalValues.Decode(entry.Value.ToArray());
            history.Complete(invocation, new ConditionalOutput(ConditionalOutcome.Read, value));
            return (true, entry?.Version, value);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            history.Complete(invocation, new ConditionalOutput(ConditionalOutcome.Failed));
            return (false, null, null);
        }
    }

    /// <summary>
    /// Cancels on the simulation's thread: a test's own token source runs its callbacks where it cancels, and
    /// <see cref="CancellationTokenSource.CancelAsync"/> would run them on the thread pool, outside the simulation.
    /// </summary>
    private static void Cancel(CancellationTokenSource source) => source.Cancel();
}
