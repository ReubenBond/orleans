using System.Globalization;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// The simulation driver's own guarantees: a seed decides the whole run, an invariant is checked after every step, the
/// run tears its components down and checks what they leave, and a failure leaves a report, the history, and Clockwork's
/// replay artifact (test/Durable.Storage/docs/spec/verification.md §7.4).
/// </summary>
public sealed class SeededSimulationTests
{
    private const int Messages = 20;

    /// <summary>
    /// Two runs from one seed, and the exact replay of the first run's artifact, record the same trace.
    /// </summary>
    [Fact]
    public void EqualSeedsReplayIdentically()
    {
        var run = SeededSimulation.RunAndReplay(11, ExchangeMessagesAsync);

        Assert.Equal(Messages, run.Trace.Count(static entry => entry.Contains("answer", StringComparison.Ordinal) || entry.Contains("lost", StringComparison.Ordinal)));
        Assert.True(run.Steps > Messages);
    }

    /// <summary>
    /// Different seeds take different paths through the same scenario.
    /// </summary>
    [Fact]
    public void DifferentSeedsTakeDifferentPaths()
    {
        var first = SeededSimulation.Run(21, ExchangeMessagesAsync);
        var second = SeededSimulation.Run(22, ExchangeMessagesAsync);

        Assert.NotEqual(first.Trace, second.Trace);
    }

    /// <summary>
    /// An invariant which fails after some step fails the run at that step, with the seed, step, trace, and a command
    /// which replays only this test case in its report, and leaves the history and a replay artifact which records the
    /// failure; the schedule is minimized only on request.
    /// </summary>
    [Fact]
    public void AViolatedInvariantFailsTheRunAtItsStepWithReplayEvidence()
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(31, CountPastThreeAsync));

        var violation = Assert.IsType<InvariantViolationException>(failure.InnerException);
        Assert.Equal("the counter stays below three", violation.Violation!.Invariant);
        Assert.Equal("the counter is 3", violation.Violation.Description);
        Assert.True(violation.Violation.Step > 0);
        Assert.Equal(31, failure.Seed);
        Assert.Contains("Seed 31 failed after step", failure.Message, StringComparison.Ordinal);
        Assert.Contains("counter 3", failure.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"CLOG_SIM_SEEDS=31 dotnet test --project test/Durable.Storage/Orleans.Durable.Storage.Simulation.Tests/Orleans.Durable.Storage.Simulation.Tests.csproj -c {TestDiagnostics.Configuration} -- --filter-display-name \"{TestContext.Current.Test!.TestDisplayName}\"",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Contains("Not minimized: set CLOG_SIM_MINIMIZE=1", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.MinimizedArtifactPath);
        Assert.Contains("InvariantViolationException: the counter stays below three failed after step", File.ReadAllText(failure.HistoryPath!), StringComparison.Ordinal);

        var artifact = ReplayArtifactSerializer.Read(failure.ArtifactPath!);
        Assert.Equal(ReplayTerminationKind.Faulted, artifact.Outcome.Kind);
        Assert.Equal(typeof(InvariantViolationException).FullName, artifact.Outcome.FailureIdentity);
    }

    /// <summary>
    /// A minimized schedule fails as the original did, with the same invariant, in no more decisions.
    /// </summary>
    [Fact]
    public void AMinimizedScheduleFailsWithTheSameInvariant()
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(31, CountPastThreeAsync, minimize: true));

        var artifact = ReplayArtifactSerializer.Read(failure.ArtifactPath!);
        if (failure.MinimizedArtifactPath is null)
        {
            Assert.Contains("Not minimized: no decision of", failure.Message, StringComparison.Ordinal);
            return;
        }

        var minimized = ReplayArtifactSerializer.Read(failure.MinimizedArtifactPath);
        Assert.Equal(artifact.Outcome.FailureIdentity, minimized.Outcome.FailureIdentity);
        Assert.True(minimized.Decisions.Count < artifact.Decisions.Count);
        Assert.Contains(typeof(InvariantViolationException).FullName!, File.ReadAllText(failure.MinimizedTracePath!), StringComparison.Ordinal);
    }

    /// <summary>
    /// A scenario which fails fails the run with its own exception, which identifies the failure in the artifact.
    /// </summary>
    [Fact]
    public void AFailedScenarioFailsTheRunWithItsException()
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(41, static async environment =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(3), environment.TimeProvider);
            throw new InvalidDataException("The scenario observed a wrong answer.");
        }));

        Assert.IsType<InvalidDataException>(failure.InnerException);
        Assert.Equal(typeof(InvalidDataException).FullName, ReplayArtifactSerializer.Read(failure.ArtifactPath!).Outcome.FailureIdentity);
    }

    /// <summary>
    /// A scenario which waits for something that never happens fails the run as stalled once nothing can run.
    /// </summary>
    [Fact]
    public void AStalledScenarioFailsTheRun()
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(51, static environment =>
            new TaskCompletionSource().Task));

        var stalled = Assert.IsType<SimulationStalledException>(failure.InnerException);
        Assert.Contains("nothing is runnable", stalled.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scenario which keeps running without ever waiting keeps virtual time from advancing, and fails the run as
    /// livelocked.
    /// </summary>
    [Fact]
    public void ALivelockedScenarioFailsTheRun()
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(52, static async environment =>
        {
            while (true)
            {
                await Task.Yield();
            }
        }));

        var stalled = Assert.IsType<SimulationStalledException>(failure.InnerException);
        Assert.Contains("livelocked", stalled.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Teardown disposes the tracked components, the last tracked first, runs the work due at its instant, and then
    /// runs the teardown checks.
    /// </summary>
    [Fact]
    public void TeardownDisposesComponentsLastFirstThenRunsItsChecks()
    {
        List<string> events = [];
        _ = SeededSimulation.Run(61, environment =>
        {
            _ = environment.Track(new Component("first", events, environment));
            _ = environment.Track(new Component("second", events, environment));
            environment.AddTeardownCheck("every component is disposed", () =>
            {
                events.Add("checked");
                return null;
            });
            return Task.CompletedTask;
        });

        Assert.Equal(["second disposed", "first disposed", "second's due work ran", "first's due work ran", "checked"], events);
    }

    /// <summary>
    /// A teardown check which fails fails the run as a violated invariant does, with a replay artifact.
    /// </summary>
    [Fact]
    public void AFailedTeardownCheckFailsTheRunWithReplayEvidence()
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(62, static environment =>
        {
            environment.AddTeardownCheck("buffers return to their pool", static () => "one buffer is outstanding");
            return Task.CompletedTask;
        }));

        var violation = Assert.IsType<InvariantViolationException>(failure.InnerException);
        Assert.Equal("buffers return to their pool", violation.Violation!.Invariant);
        Assert.Equal(ReplayTerminationKind.Faulted, ReplayArtifactSerializer.Read(failure.ArtifactPath!).Outcome.Kind);
    }

    /// <summary>
    /// A step invariant or teardown check which throws on the driver's thread cannot vouch for its invariant, so it fails
    /// the run with its violation, which names the check and carries its exception (issue #150).
    /// </summary>
    /// <param name="atTeardown">Whether the check runs at teardown rather than after every step.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACheckWhichThrowsFailsTheRunWithItsViolation(bool atTeardown)
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(75, environment =>
        {
            var checks = atTeardown ? environment.TeardownChecks : environment.Invariants;
            checks.Add("the probe holds", static () => throw new InvalidOperationException("the probe broke"));
            return Task.CompletedTask;
        }));

        var violation = Assert.IsType<InvariantViolationException>(failure.InnerException);
        Assert.Equal("the probe holds", violation.Violation!.Invariant);
        Assert.Equal("the check threw InvalidOperationException: the probe broke", violation.Violation.Description);
        Assert.Equal(typeof(InvariantViolationException).FullName, ReplayArtifactSerializer.Read(failure.ArtifactPath!).Outcome.FailureIdentity);
    }

    /// <summary>
    /// Every run checks at teardown that nothing outlives its components, and the driver advances no virtual time after
    /// teardown, so a timer which outlives the scenario is still armed when the check runs, and fails it.
    /// </summary>
    [Fact]
    public void ATimerArmedAtTeardownFailsTheQuiescenceCheck()
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(63, static environment =>
        {
            _ = Task.Delay(TimeSpan.FromHours(1), environment.TimeProvider);
            return Task.CompletedTask;
        }));

        var violation = Assert.IsType<InvariantViolationException>(failure.InnerException);
        Assert.Equal(SeededSimulation.QuiescenceCheck, violation.Violation!.Invariant);
    }

    /// <summary>
    /// The transport leaves nothing armed once it is disposed, though messages and deadlines were in flight when the
    /// scenario ended, and teardown drains a partitioned network and a suspended node.
    /// </summary>
    [Fact]
    public void TheTransportLeavesNothingArmedAfterTeardown()
    {
        Task<int>? call = null;
        _ = SeededSimulation.Run(64, async environment =>
        {
            environment.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(5);
            var client = environment.Cluster.AddNode("client");
            var server = environment.Cluster.AddNode("server");
            call = environment.Rpc.CallAsync(client, server, static () => ValueTask.FromResult(1), TimeSpan.FromSeconds(1));
            environment.Network.IsolateNode(client.NetworkAddress);
            server.Suspend();
            await Task.Yield();
        });

        // The transport's disposal ended the caller's wait, though neither its answer nor its deadline had come.
        Assert.True(call!.IsCanceled);
    }

    /// <summary>
    /// A bounded wait which suppresses its timeout returns, without throwing, once its grace passes on virtual time
    /// (ReubenBond/Clockwork#38).
    /// </summary>
    [Fact]
    public void AWaitWhichSuppressesItsTimeoutReturnsAtItsGrace()
    {
        var grace = TimeSpan.FromMilliseconds(7);
        var waited = TimeSpan.Zero;
        var run = SeededSimulation.Run(65, async _ =>
        {
            using var cancellation = new CancellationTokenSource();
            waited = await ClockworkConformance.WaitOutGraceAsync(grace, cancellation.Token);
        });

        Assert.Equal(grace, waited);
        Assert.Equal(ReplayTerminationKind.Completed, run.Artifact.Outcome.Kind);
    }

    /// <summary>
    /// An asynchronous sequence enumerated inside an asynchronous disposable, both configured not to capture a context,
    /// yields every item, and both are disposed (ReubenBond/Clockwork#39).
    /// </summary>
    [Fact]
    public void ConfiguredAsyncDisposablesAndSequencesYieldEveryItemAndDispose()
    {
        (List<int> Items, bool Disposed) result = default;
        _ = SeededSimulation.Run(66, async _ => result = await ClockworkConformance.EnumerateInsideADisposableAsync(5));

        Assert.Equal([0, 1, 2, 3, 4], result.Items);
        Assert.True(result.Disposed);
    }

    /// <summary>
    /// Task completion sources which tasks complete, raced and continued, take the same schedule and give the same results
    /// in two runs from one seed (ReubenBond/Clockwork#40).
    /// </summary>
    [Fact]
    public void CompletionSourceRacesTakeTheSameScheduleFromOneSeed()
    {
        var results = new List<(int Winner, int Continued)>();
        var first = SeededSimulation.Run(68, RaceAsync);
        var second = SeededSimulation.Run(68, RaceAsync);

        Assert.Equal(first.ScheduleId, second.ScheduleId);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(11, results[0].Continued);

        async Task RaceAsync(SimulationEnvironment _)
        {
            using var cancellation = new CancellationTokenSource();
            results.Add(await ClockworkConformance.RaceCompletionSourcesAsync(cancellation.Token));
        }
    }

    /// <summary>
    /// The kit's combinators complete inside the simulation over a task which runs its continuations asynchronously, as a
    /// task completion source created so does; the BCL's, which a test assembly would call unrewritten, complete on the
    /// real thread pool and post their waiters' continuations from there, outside the recorded schedule (#360).
    /// </summary>
    [Fact]
    public void TheKitsCombinatorsCompleteInsideTheSimulation()
    {
        bool? anyInside = null;
        bool? allInside = null;
        _ = SeededSimulation.Run(76, async environment =>
        {
            var scheduler = environment.Cluster.Scheduler;
            var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var never = new TaskCompletionSource();
            var any = SimulationTasks.WhenAny(answered.Task, never.Task);
            var all = SimulationTasks.WhenAll(answered.Task, Task.CompletedTask);
            _ = any.ContinueWith(_ => anyInside = scheduler.IsSimulationThread, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = all.ContinueWith(_ => allInside = scheduler.IsSimulationThread, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            answered.SetResult();
            await all;
            _ = await any;
            never.SetResult();
        });

        Assert.True(anyInside, "WhenAny completed outside the simulation.");
        Assert.True(allInside, "WhenAll completed outside the simulation.");
    }

    /// <summary>
    /// A wait which times out, and one whose token is cancelled first, each end at their instant with their exception and
    /// leave the run completed: Clockwork records the continuation the wait withdraws as a diagnostic, and completes a run
    /// in which no operation faulted, raced, or deadlocked (ReubenBond/Clockwork#44).
    /// </summary>
    /// <param name="timeoutMilliseconds">The wait's timeout.</param>
    /// <param name="cancelAfterMilliseconds">When the wait's token is cancelled.</param>
    /// <param name="ended">The exception which ends the wait.</param>
    [Theory]
    [InlineData(5, 50, typeof(TimeoutException))]
    [InlineData(50, 5, typeof(OperationCanceledException))]
    public void AWaitWhichTimesOutOrIsCancelledLeavesTheRunCompleted(int timeoutMilliseconds, int cancelAfterMilliseconds, Type ended)
    {
        (Exception? Ended, TimeSpan Waited) result = default;
        var run = SeededSimulation.Run(67, async _ => result = await ClockworkConformance.WaitForNothingAsync(
            TimeSpan.FromMilliseconds(timeoutMilliseconds),
            TimeSpan.FromMilliseconds(cancelAfterMilliseconds)));

        Assert.IsAssignableFrom(ended, result.Ended);
        Assert.Equal(TimeSpan.FromMilliseconds(Math.Min(timeoutMilliseconds, cancelAfterMilliseconds)), result.Waited);
        Assert.Equal(ReplayTerminationKind.Completed, run.Artifact.Outcome.Kind);
    }

    /// <summary>
    /// A debug assertion which fails in rewritten code on the scheduler lane, and whose exception that code catches, fails
    /// the run at its step, with its seed and replay evidence: the probe never continues past its waits, and an invariant
    /// which the same step violates gives way to the assertion (verification §11.2).
    /// </summary>
    [Fact]
    public void ACaughtDebugAssertionFailsTheRunAtItsStepWithItsSeed()
    {
        var failure = Assert.Throws<SimulationFailedException>(static () => SeededSimulation.Run(69, static environment =>
        {
            environment.Invariants.Add(
                "the probe has not asserted",
                () => environment.Trace.Entries.Any(static entry => entry.EndsWith(" asserting", StringComparison.Ordinal)) ? "the probe asserted" : null);
            return ClockworkConformance.CatchADebugAssertionAsync(environment, "the probe's assertion");
        }));

        var assertion = Assert.IsType<DebugAssertionException>(failure.InnerException);
        Assert.Equal(TestContext.Current.Test!.TestDisplayName, assertion.TestName);
        Assert.Equal("Debug assertion failed: the probe's assertion", assertion.Message);
        Assert.Contains(nameof(ClockworkConformance.CatchADebugAssertionAsync), assertion.StackTrace!.Split(Environment.NewLine)[0], StringComparison.Ordinal);
        Assert.Contains($"{nameof(ClockworkConformance)}+<{nameof(ClockworkConformance.CatchADebugAssertionAsync)}>", assertion.SiteKey, StringComparison.Ordinal);
        Assert.Equal(69, failure.Seed);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"Seed 69 failed after step {failure.Steps}: DebugAssertionException: Debug assertion failed: the probe's assertion"),
            failure.Message,
            StringComparison.Ordinal);

        var history = File.ReadAllLines(failure.HistoryPath!);
        Assert.Contains(history, static entry => entry.EndsWith(" asserting", StringComparison.Ordinal));
        Assert.Contains(history, static entry => entry.EndsWith(" caught", StringComparison.Ordinal));
        Assert.DoesNotContain(history, static entry => entry.EndsWith(" continued", StringComparison.Ordinal));
        Assert.Contains(history, static entry => entry.Contains("DebugAssertionException: Debug assertion failed: the probe's assertion", StringComparison.Ordinal));

        var artifact = ReplayArtifactSerializer.Read(failure.ArtifactPath!);
        Assert.Equal(ReplayTerminationKind.Faulted, artifact.Outcome.Kind);
        Assert.Equal(typeof(DebugAssertionException).FullName, artifact.Outcome.FailureIdentity);
        Assert.Empty(DebugAssertions.TakeRaisedBy(TestContext.Current.Test));
    }

    /// <summary>
    /// A debug assertion which fails under one of a <see cref="Ledger"/>'s replicas fails the run with that assertion, although
    /// the <see cref="Ledger"/> counts its exception as the replica's failure and still reaches its quorum (verification §11.2).
    /// </summary>
    [Fact]
    public void AnAssertionUnderAQuorumReplicaFailsTheRunWithTheAssertion()
    {
        var failure = Assert.Throws<SimulationFailedException>(static () => SeededSimulation.Run(72, static async environment =>
        {
            var replicas = Enumerable.Range(0, 3)
                .Select(static index =>
                {
                    var replica = new InMemoryLedgerReplica(new LedgerReplicaOptions { WindowSize = 8 });
                    return index == 0 ? (ILedgerReplica)new AssertingLedgerReplica(replica) : replica;
                })
                .ToArray();
            var nodes = new LedgerLocation.Nodes([.. replicas.Select(static _ => NodeId.New())]);
            await using var ledger = new Ledger(new LedgerId(1), LedgerQuorum.ForNodes(new LedgerId(1), nodes.Replicas, replicas, quorum: 2, new LedgerReplicaOptions { WindowSize = 8 }, SimulationEnvironment.Clock, localNode: null), nodes);
            using var writer = new ArcBufferWriter();
            writer.Write("value"u8);
            using var payload = writer.ConsumeSlice(writer.Length);
            var result = await ledger.WriteAsync(Slot.Zero, payload, CancellationToken.None);
            environment.Trace.Record(string.Create(CultureInfo.InvariantCulture, $"write {result}"));
        }));

        var assertion = Assert.IsType<DebugAssertionException>(failure.InnerException);
        Assert.Equal($"Debug assertion failed: {AssertingLedgerReplica.Message}", assertion.Message);
        Assert.StartsWith($"{typeof(AssertingLedgerReplica).FullName}.{nameof(AssertingLedgerReplica.WriteAsync)}+IL_", assertion.SiteKey, StringComparison.Ordinal);
        Assert.Equal(typeof(DebugAssertionException).FullName, ReplayArtifactSerializer.Read(failure.ArtifactPath!).Outcome.FailureIdentity);
    }

    /// <summary>
    /// A simulation which starts while another runs fails the one which started it: simulations run one at a time
    /// (verification §7.1).
    /// </summary>
    [Fact]
    public void ASimulationStartedWithinAnotherFailsIt()
    {
        var failure = Assert.Throws<SimulationFailedException>(static () => SeededSimulation.Run(70, static async environment =>
        {
            await Task.Yield();
            environment.Trace.Record("starting a second simulation");
            _ = SeededSimulation.Run(71, static _ => Task.CompletedTask);
        }));

        Assert.Contains("simulations run one at a time", Assert.IsType<InvalidOperationException>(failure.InnerException).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token cancelled on a thread outside the simulation, as a test framework cancels its own, runs the callback the
    /// transport registered there; Clockwork's simulation-thread check rejects the callback's completion of the caller's
    /// wait, and the run fails with the violation and is recorded as aborted (verification §7.1).
    /// </summary>
    [Fact]
    public void ACallbackRunOutsideTheSimulationAbortsTheRun()
    {
        var failure = Assert.Throws<SimulationFailedException>(static () => SeededSimulation.Run(73, static async environment =>
        {
            var client = environment.Cluster.AddNode("client");
            var server = environment.Cluster.AddNode("server");
            using var cancellation = new CancellationTokenSource();
            var call = environment.Rpc.CallAsync(client, server, static () => ValueTask.CompletedTask, TimeSpan.FromMilliseconds(20), cancellation.Token);
            CancelOutsideTheSimulation(cancellation);
            await call;
        }));

        var violation = Assert.IsType<SimulationThreadAffinityException>(failure.InnerException);
        Assert.Contains(nameof(TaskCompletionSource), violation.ApiName, StringComparison.Ordinal);
        Assert.Equal(ReplayTerminationKind.Aborted, ReplayArtifactSerializer.Read(failure.ArtifactPath!).Outcome.Kind);
        Assert.DoesNotContain("Replay the recorded schedule", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A violation which a check meets when it runs the work due at its instant, as the quiescence check does, aborts
    /// the run with the violation, rather than failing the check (verification §7.4).
    /// </summary>
    [Fact]
    public void AViolationWhichACheckMeetsAbortsTheRun()
    {
        using var cancellation = new CancellationTokenSource();
        Task? completion = null;
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(74, environment =>
        {
            completion = ClockworkConformance.CompleteOnCancellation(cancellation.Token);
            environment.AddTeardownCheck("the work due now runs", () =>
            {
                CancelOutsideTheSimulation(cancellation);
                return SimulationLeakCheck.FindLeftovers(environment.Cluster, CancellationToken.None);
            });
            return Task.CompletedTask;
        }));

        Assert.IsType<SimulationThreadAffinityException>(failure.InnerException);
        Assert.Equal(ReplayTerminationKind.Aborted, ReplayArtifactSerializer.Read(failure.ArtifactPath!).Outcome.Kind);
        Assert.False(completion!.IsCompleted);
    }

    /// <summary>
    /// A run fails when the scenario keeps a page past teardown, and passes when it releases it.
    /// </summary>
    [Fact]
    public void APageKeptPastTeardownFailsThePageCheck()
    {
        _ = SeededSimulation.Run(65, static environment =>
        {
            ArcBuffer.Wrap(new byte[1]).Dispose();
            return Task.CompletedTask;
        });

        var kept = new List<ArcBuffer>();
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(66, environment =>
        {
            kept.Add(ArcBuffer.Wrap(new byte[1]));
            return Task.CompletedTask;
        }));

        var violation = Assert.IsType<InvariantViolationException>(failure.InnerException);
        Assert.Equal(SeededSimulation.PageCheck, violation.Violation!.Invariant);
        Assert.StartsWith("1 of the 1 ArcBuffer pages", violation.Violation.Description, StringComparison.Ordinal);
        Assert.Single(kept);
    }

    /// <summary>
    /// Cancels <paramref name="source"/> on a thread of its own which carries no execution context, as a test framework
    /// cancels its token, so each callback runs there with only the context its registration captured.
    /// </summary>
    private static void CancelOutsideTheSimulation(CancellationTokenSource source)
    {
        var canceller = new Thread(() =>
        {
            try
            {
                source.Cancel();
            }
            catch (AggregateException)
            {
                // A rejected callback's exception; the scheduler reports the violation to the driver.
            }
        });
        canceller.UnsafeStart();
        canceller.Join();
    }

    /// <summary>
    /// A run fails at the step which reports a fatal error no fault injection caused: a defect, a failed local storage
    /// operation outside every node's root, or one beneath a node's root which the node did not inject
    /// (conventions.md §14, verification.md §7.4).
    /// </summary>
    /// <param name="report">Which report the scenario makes.</param>
    /// <param name="expected">What the run's failure says.</param>
    [Theory]
    [InlineData("defect", "reported a defect: Durable storage stopped the process with exit code 79: the defect WalPlacement")]
    [InlineData("outside", "reported a local storage failure outside every node's root")]
    [InlineData("uninjected", "reported a local storage failure no fault injection caused")]
    public void AFatalErrorNoFaultInjectionCausedFailsTheRun(string report, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "clog-simulated-node");
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(73, async environment =>
        {
            environment.AddStorageRoot(root, static _ => false);
            await Task.Delay(TimeSpan.FromMilliseconds(1), environment.TimeProvider);
            switch (report)
            {
                case "defect":
                    FatalErrorHandler.StopOnDefect("WalPlacement", new InvalidOperationException("A placement threw."));
                    break;
                case "outside":
                    FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleWrite, Path.Combine(Path.GetTempPath(), "elsewhere", "1.clfl"), new IOException("A write failed."));
                    break;
                default:
                    FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleWrite, Path.Combine(root, "wal", "1.clfl"), new IOException("A write failed."));
                    break;
            }
        }));

        var violation = Assert.IsType<InvariantViolationException>(failure.InnerException);
        Assert.Contains(expected, violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a counter past three, which an invariant forbids.
    /// </summary>
    private static async Task CountPastThreeAsync(SimulationEnvironment environment)
    {
        var counter = 0;
        environment.Invariants.Add("the counter stays below three", () =>
            counter < 3 ? null : string.Create(CultureInfo.InvariantCulture, $"the counter is {counter}"));
        for (var round = 0; round < 10; round++)
        {
            await Task.Delay(environment.Random.NextTimeSpan(TimeSpan.FromMilliseconds(5)) + TimeSpan.FromMilliseconds(1), environment.TimeProvider);
            counter++;
            environment.Trace.Record(string.Create(CultureInfo.InvariantCulture, $"counter {counter}"));
        }
    }

    /// <summary>
    /// A client sends requests to a server across a lossy, jittery network; each answer or loss is traced.
    /// </summary>
    private static async Task ExchangeMessagesAsync(SimulationEnvironment environment)
    {
        environment.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(1);
        environment.Network.MaxJitter = TimeSpan.FromMilliseconds(5);
        environment.Network.MessageDropRate = 0.2;
        var client = environment.Cluster.AddNode("client");
        var server = environment.Cluster.AddNode("server");
        var served = 0;

        async Task SendAsync(int message)
        {
            await Task.Delay(environment.Random.NextTimeSpan(TimeSpan.FromMilliseconds(20)), environment.TimeProvider);
            try
            {
                var answer = await environment.Rpc.CallAsync(client, server, () => ValueTask.FromResult(++served), TimeSpan.FromMilliseconds(30));
                environment.Trace.Record(string.Create(CultureInfo.InvariantCulture, $"message {message} answer {answer}"));
            }
            catch (TimeoutException)
            {
                environment.Trace.Record(string.Create(CultureInfo.InvariantCulture, $"message {message} lost"));
            }
        }

        await SimulationTasks.WhenAll(Enumerable.Range(0, Messages).Select(SendAsync));
    }

    /// <summary>
    /// A component whose disposal schedules work due at once.
    /// </summary>
    private sealed class Component(string name, List<string> events, SimulationEnvironment environment) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            events.Add(name + " disposed");
            environment.Cluster.SchedulerLane.Enqueue(() => events.Add(name + "'s due work ran"));
            return ValueTask.CompletedTask;
        }
    }
}
