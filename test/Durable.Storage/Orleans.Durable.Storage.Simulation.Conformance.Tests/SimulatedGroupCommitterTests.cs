using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.GroupCommit;
using Orleans.Durable.Storage.Testing;
using static System.FormattableString;

namespace Orleans.Durable.Storage.Simulation.Conformance.Tests;

/// <summary>
/// <see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}"/> under Clockwork and
/// <see cref="WindowScheduleExplorer"/> (conventions.md §6, verification.md §4.1, §7.1): concurrent enqueue and merging,
/// leader handoff across batch-loop exits, caller cancellation and disposal races, failure propagation (read failures,
/// corruption, unknown write outcomes with held-write fencing, mismatches followed by refusal, and rules defects), and a
/// race mutant (<see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}.RecordsSequenceAfterIoMutant"/>)
/// caught by schedule exploration.
/// </summary>
public sealed class SimulatedGroupCommitterTests
{
    private const int BaseSeed = 1;

    /// <summary>
    /// Concurrent submissions of distinct and identical keys batch, fold identical pending changes once, and answer each
    /// caller from a store value read or written after that caller submitted its change.
    /// </summary>
    [Fact]
    public async Task ConcurrentEnqueueAndMergingConformUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        await RunExploredAsync(conformance, "concurrent-enqueue-and-merging", BaseSeed, async () =>
        {
            var store = new SimulatedSetStore();
            await using var committer = CreateCommitter(store);

            // Seed one key so a no-op / already-present submission mixes with mutations.
            store.WriteExternal("seed");

            var answers = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("a")),
                    () => SubmitAsync(committer, store, Change.Add("b")),
                    () => SubmitAsync(committer, store, Change.Add("a")),
                    () => SubmitAsync(committer, store, Change.Probe("seed")),
                    () => ExternalThenSubmitAsync(committer, store, "ext", Change.Probe("ext")),
                ]);

            foreach (var answer in answers)
            {
                AssertFreshOutcome(store, answer);
            }

            Assert.True(store.Contains("a"));
            Assert.True(store.Contains("b"));
            Assert.True(store.Contains("ext"));
            Assert.True(store.Contains("seed"));
        });

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// Submissions arriving as the batch loop drains <c>_pending</c>, clears <c>_running</c>, and exits are never lost or
    /// orphaned, both with a zero write interval and with a positive write interval.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task LeaderHandoffConformsUnderEveryExploredSchedule(int writeIntervalMs)
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);
        var interval = TimeSpan.FromMilliseconds(writeIntervalMs);
        await RunExploredAsync(conformance, Invariant($"leader-handoff-{writeIntervalMs}ms"), BaseSeed + writeIntervalMs, async () =>
        {
            var store = new SimulatedSetStore();
            await using var committer = CreateCommitter(store, writeInterval: interval);

            // First wave starts the batch loop; second wave races with the first wave's drain and loop exit.
            var first = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("w1-a")),
                    async () =>
                    {
                        await SimulationTasks.Yield();
                        return await SubmitAsync(committer, store, Change.Add("w1-b"));
                    },
                    async () =>
                    {
                        await SimulationTasks.Yield();
                        await SimulationTasks.Yield();
                        return await SubmitAsync(committer, store, Change.Add("w2-a"));
                    },
                ]);

            // Third wave starts after the earlier loop may have exited or may still be handing off.
            var second = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("w3-a")),
                    () => SubmitAsync(committer, store, Change.Probe("w2-a")),
                ]);

            foreach (var answer in first.Concat(second))
            {
                AssertFreshOutcome(store, answer);
            }

            foreach (var key in new[] { "w1-a", "w1-b", "w2-a", "w3-a" })
            {
                Assert.True(store.Contains(key), $"Expected {key} to be durable after leader handoff.");
            }
        });

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// Cancelling one caller's token ends only that caller's wait while a merged peer and concurrent callers still settle;
    /// disposing concurrently with submissions answers undecided callers with <see cref="ObjectDisposedException"/> and
    /// drains without leaking buffers or leaving work running.
    /// </summary>
    [Fact]
    public async Task CancellationAndDisposalConformUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);

        // Window 1: cancellation of one of two merged callers alongside an uncancelled caller.
        await RunExploredAsync(conformance, "caller-cancellation", BaseSeed + 10, async () =>
        {
            var store = new SimulatedSetStore();
            await using var committer = CreateCommitter(store);
            using var cts = new CancellationTokenSource();

            var answers = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("shared"), cts.Token),
                    () => SubmitAsync(committer, store, Change.Add("shared")),
                    () => SubmitAsync(committer, store, Change.Add("other")),
                    async () =>
                    {
                        await SimulationTasks.Yield();
                        await cts.CancelAsync();
                        return SubmissionResult.Control();
                    },
                ]);

            var cancelled = answers[0];
            Assert.True(
                cancelled.Failure is OperationCanceledException || cancelled.Outcome is not null,
                $"Cancelled caller observed unexpected failure: {cancelled.Failure}");
            if (cancelled.Outcome is not null)
            {
                AssertFreshOutcome(store, cancelled);
            }

            AssertFreshOutcome(store, answers[1]);
            AssertFreshOutcome(store, answers[2]);
            Assert.True(store.Contains("shared"));
            Assert.True(store.Contains("other"));
        });

        // Window 2: disposal racing with concurrent submissions.
        await RunExploredAsync(conformance, "concurrent-disposal", BaseSeed + 11, async () =>
        {
            var store = new SimulatedSetStore();
            var committer = CreateCommitter(store);

            var answers = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("a")),
                    () => SubmitAsync(committer, store, Change.Add("b")),
                    async () =>
                    {
                        await SimulationTasks.Yield();
                        await committer.DisposeAsync();
                        return SubmissionResult.Control();
                    },
                ]);

            foreach (var answer in answers.Take(2))
            {
                if (answer.Failure is not null)
                {
                    _ = Assert.IsType<ObjectDisposedException>(answer.Failure);
                }
                else
                {
                    AssertFreshOutcome(store, answer);
                }
            }

            // A submission after disposal must fail with ObjectDisposedException.
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => committer.CommitAsync(Change.Add("after"), CancellationToken.None));
        });

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// Concurrent submissions observe exact failure propagation under every explored schedule: read failures, corrupt
    /// reads, unknown write outcomes (landed and unlanded with held-write fencing), mismatches followed by refusal, and
    /// rules defects.
    /// </summary>
    [Fact]
    public async Task FailurePropagationConformsUnderEveryExploredSchedule()
    {
        var conformance = new SimulatedConformance(ScheduleBound.Current);

        // Scenario A: unknown write outcome (unlanded, may still land on same version) fences a probe against answering Absent.
        await RunExploredAsync(conformance, "unknown-write-unlanded-fence", BaseSeed + 20, async () =>
        {
            var store = new SimulatedSetStore();
            store.EnqueueWriteFault(WriteFault.UnknownUnlanded);
            await using var committer = CreateCommitter(store);

            var answers = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("x")),
                    () => SubmitAsync(committer, store, Change.Probe("x")),
                ]);

            foreach (var answer in answers)
            {
                if (answer.Failure is not null)
                {
                    _ = Assert.IsType<IOException>(answer.Failure);
                }
                else
                {
                    AssertFreshOutcome(store, answer);
                }
            }

            // A follow-up probe when a held write may still land triggers a fence write (advancing the version so the
            // unlanded write can never land afterward) before answering Absent.
            var fenced = await SubmitAsync(committer, store, Change.Probe("x"));
            AssertFreshOutcome(store, fenced);
            Assert.Equal(ChangeOutcome.Absent, fenced.Outcome);
        });

        // Scenario B: unknown write outcome that actually landed is answered applied on the recovery read.
        await RunExploredAsync(conformance, "unknown-write-landed", BaseSeed + 21, async () =>
        {
            var store = new SimulatedSetStore();
            store.EnqueueWriteFault(WriteFault.UnknownLanded);
            await using var committer = CreateCommitter(store);

            var answers = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("x")),
                    () => SubmitAsync(committer, store, Change.Probe("x")),
                ]);

            foreach (var answer in answers)
            {
                AssertFreshOutcome(store, answer);
            }

            Assert.True(store.Contains("x"));
        });

        // Scenario C: mismatch on first write, followed by refusal on retry — earlier-carried changes fail with OutcomeUnknown
        // while a non-carried probe or freshly joined change gets the refusal directly or succeeds cleanly.
        await RunExploredAsync(conformance, "mismatch-then-refusal", BaseSeed + 22, async () =>
        {
            var store = new SimulatedSetStore();
            store.EnqueueWriteFault(WriteFault.AppliedMismatch);
            store.EnqueueWriteFault(WriteFault.Refused);
            await using var committer = CreateCommitter(store);

            var answers = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("a")),
                    () => SubmitAsync(committer, store, Change.Add("b")),
                ]);

            foreach (var answer in answers)
            {
                if (answer.Failure is not null)
                {
                    Assert.True(
                        answer.Failure is OutcomeUnknownException or InvalidOperationException or IOException,
                        $"Unexpected failure type: {answer.Failure.GetType().Name}");
                }
                else
                {
                    AssertFreshOutcome(store, answer);
                }
            }
        });

        // Scenario D: read failure, corrupt read, and rules defect leave the committer healthy for subsequent batches.
        await RunExploredAsync(conformance, "read-failure-corrupt-and-defect", BaseSeed + 23, async () =>
        {
            var store = new SimulatedSetStore();
            var rules = new SetRules(store);
            await using var committer = new GroupCommitter<SetState, SetVersion, Change, ChangeOutcome, SetWriteReply>(rules, "SimulatedSet", SimulationEnvironment.Clock, TimeSpan.Zero);

            store.EnqueueReadFault(ReadFault.Fail);
            var failedWindow = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("a")),
                    () => SubmitAsync(committer, store, Change.Add("b")),
                ]);

            foreach (var answer in failedWindow)
            {
                if (answer.Failure is not null)
                {
                    _ = Assert.IsType<IOException>(answer.Failure);
                }
                else
                {
                    AssertFreshOutcome(store, answer);
                }
            }

            store.EnqueueReadFault(ReadFault.Corrupt);
            var corruptWindow = await RunWindowAsync(
                conformance,
                [
                    () => SubmitAsync(committer, store, Change.Add("c")),
                    () => SubmitAsync(committer, store, Change.Add("d")),
                ]);

            foreach (var answer in corruptWindow)
            {
                if (answer.Failure is not null)
                {
                    _ = Assert.IsType<InvalidDataException>(answer.Failure);
                }
                else
                {
                    AssertFreshOutcome(store, answer);
                }
            }

            // Rules defect before write fails with InvalidOperationException and writes nothing.
            rules.NextViolation = "SET-I1 violated";
            var defect = await Assert.ThrowsAsync<InvalidOperationException>(() => committer.CommitAsync(Change.Add("bad"), CancellationToken.None));
            Assert.Contains("SET-I1 violated", defect.Message, StringComparison.Ordinal);
            Assert.False(store.Contains("bad"));

            // Committer recovers cleanly on the next batch.
            var after = await SubmitAsync(committer, store, Change.Add("recovered"));
            AssertFreshOutcome(store, after);
            Assert.True(store.Contains("recovered"));
        });

        TestDiagnostics.WriteLine(conformance.Summary());
        Assert.Contains(conformance.Explorations, static exploration => exploration.Schedules > 1);
    }

    /// <summary>
    /// A committer which snapshots its submission sequence after a read completes instead of before
    /// (<see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}.RecordsSequenceAfterIoMutant"/>) treats a
    /// stale read started before an external write and a subsequent probe as fresh for that probe, answering
    /// <see cref="ChangeOutcome.Absent"/> even though the key was durable before the probe was submitted (conventions.md
    /// §6). The default schedule runs the external write and probe first and passes; <see cref="WindowScheduleExplorer"/>
    /// catches the mutant by one preemption.
    /// </summary>
    [Fact]
    public async Task ExploredSchedulesCatchSequenceRecordedAfterIo()
    {
        Task Body(SimulatedConformance conformance, bool mutant) =>
            RunExploredAsync(conformance, "records-sequence-after-io", BaseSeed + 30, async () =>
            {
                var store = new SimulatedSetStore();
                await using var committer = CreateCommitter(store, recordsSequenceAfterIoMutant: mutant);

                // Op 1 (newest, runs first on the default schedule) triggers a read of v0 (without "ext").
                // Op 0 externally writes "ext" (v1) and submits Probe("ext").
                // Under the default schedule, Op 1's read finishes at sequence 1 before Op 0 writes "ext" and submits
                // Probe("ext") at sequence 2, so Batch 2 re-reads v1 and answers Present. Under one preemption while
                // Op 1's read response is in flight, Op 0 writes "ext" and submits Probe("ext") at sequence 2 before
                // ReadAsync returns, so the mutant records sequenceBefore = 2 and answers Probe("ext") with stale Absent.
                var answers = await RunWindowAsync(
                    conformance,
                    [
                        () => ExternalThenSubmitAsync(committer, store, "ext", Change.Probe("ext")),
                        () => SubmitAsync(committer, store, Change.Probe("other")),
                    ]);

                foreach (var answer in answers)
                {
                    AssertFreshOutcome(store, answer);
                }
            });

        // Without the mutant, every explored schedule holds.
        var clean = new SimulatedConformance(ScheduleBound.Gate);
        await Body(clean, mutant: false);
        Assert.Contains(clean.Explorations, static exploration => exploration.Schedules > 1);

        // With the mutant, the default schedule (zero preemptions) passes.
        var defaultOnly = new SimulatedConformance(new ScheduleBound(1, 0));
        await Body(defaultOnly, mutant: true);
        Assert.Equal(1, defaultOnly.Explorations.Single().Schedules);

        // With the mutant, schedule exploration catches the stale answer via a preemption.
        var explored = new SimulatedConformance(ScheduleBound.Gate);
        var caught = await Assert.ThrowsAsync<ConformanceScheduleException>(() => Body(explored, mutant: true));
        Assert.Contains("stale", caught.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(explored.Explorations.Single().Schedules > 1, "Expected preemption schedule to catch the mutant.");
        TestDiagnostics.WriteLine(Invariant($"Mutant caught in {explored.Explorations.Single().Schedules} schedules: {caught.Message}"));
    }

    private static GroupCommitter<SetState, SetVersion, Change, ChangeOutcome, SetWriteReply> CreateCommitter(
        SimulatedSetStore store,
        TimeSpan writeInterval = default,
        bool recordsSequenceAfterIoMutant = false) =>
        new(new SetRules(store), "SimulatedSet", SimulationEnvironment.Clock, writeInterval)
        {
            RecordsSequenceAfterIoMutant = recordsSequenceAfterIoMutant,
        };

    private static Task RunExploredAsync(SimulatedConformance conformance, string name, int seed, Func<Task> body) =>
        conformance.RunCaseAsync(new ConformanceCase([], name, seed), body);

    private static async Task<List<SubmissionResult>> RunWindowAsync(SimulatedConformance conformance, IReadOnlyList<Func<Task<SubmissionResult>>> calls)
    {
        conformance.GroupStarting();
        var started = calls.Select(call => conformance.Start(async () => await call())).ToList();
        var results = new List<SubmissionResult>(started.Count);
        foreach (var task in started)
        {
            results.Add((SubmissionResult)await task);
        }

        conformance.GroupCompleted();
        return results;
    }

    private static async Task<SubmissionResult> SubmitAsync(
        GroupCommitter<SetState, SetVersion, Change, ChangeOutcome, SetWriteReply> committer,
        SimulatedSetStore store,
        Change change,
        CancellationToken cancellationToken = default)
    {
        var submittedVersion = store.Version;
        try
        {
            var outcome = await committer.CommitAsync(change, cancellationToken);
            return new(change, submittedVersion, store.Version, outcome, Failure: null);
        }
        catch (Exception failure)
        {
            return new(change, submittedVersion, store.Version, Outcome: null, Failure: failure);
        }
    }

    private static async Task<SubmissionResult> ExternalThenSubmitAsync(
        GroupCommitter<SetState, SetVersion, Change, ChangeOutcome, SetWriteReply> committer,
        SimulatedSetStore store,
        string externalKey,
        Change change)
    {
        store.WriteExternal(externalKey);
        return await SubmitAsync(committer, store, change);
    }

    /// <summary>
    /// Verifies the conventions.md §6 freshness invariant: the outcome must match at least one durable store state whose
    /// version is <c>&gt;= result.SubmittedVersion</c> and <c>&lt;= result.AnsweredVersion</c>.
    /// </summary>
    private static void AssertFreshOutcome(SimulatedSetStore store, SubmissionResult result)
    {
        Assert.Null(result.Failure);
        Assert.NotNull(result.Outcome);

        var matching = false;
        for (var version = result.SubmittedVersion; version <= result.AnsweredVersion; version++)
        {
            var snapshot = store.SnapshotAt(version);
            if (Matches(result.Change, result.Outcome.Value, snapshot))
            {
                matching = true;
                break;
            }
        }

        Assert.True(
            matching,
            Invariant($"Stale or inconsistent outcome {result.Outcome} for {result.Change}: submitted at v{result.SubmittedVersion}, answered at v{result.AnsweredVersion}."));
    }

    private static bool Matches(Change change, ChangeOutcome outcome, ImmutableSortedSet<string> snapshot) =>
        (change.Kind, outcome) switch
        {
            (ChangeKind.Add, ChangeOutcome.Added) => snapshot.Contains(change.Key),
            (ChangeKind.Add, ChangeOutcome.AlreadyPresent) => snapshot.Contains(change.Key),
            (ChangeKind.Probe, ChangeOutcome.Present) => snapshot.Contains(change.Key),
            (ChangeKind.Probe, ChangeOutcome.Absent) => !snapshot.Contains(change.Key),
            _ => false,
        };

    private enum ChangeKind
    {
        Add,
        Probe,
    }

    private enum ChangeOutcome
    {
        Added,
        AlreadyPresent,
        Present,
        Absent,
    }

    private enum ReadFault
    {
        Fail,
        Corrupt,
    }

    private enum WriteFault
    {
        UnknownUnlanded,
        UnknownLanded,
        AppliedMismatch,
        Refused,
    }

    private readonly record struct Change(ChangeKind Kind, string Key)
    {
        public static Change Add(string key) => new(ChangeKind.Add, key);

        public static Change Probe(string key) => new(ChangeKind.Probe, key);

        public override string ToString() => Invariant($"{Kind}({Key})");
    }

    private sealed record SubmissionResult(Change Change, int SubmittedVersion, int AnsweredVersion, ChangeOutcome? Outcome, Exception? Failure)
    {
        public static SubmissionResult Control() => new(default, 0, 0, null, null);
    }

    private sealed record SetState(ImmutableSortedSet<string> Keys)
    {
        public static SetState Empty { get; } = new(ImmutableSortedSet.Create<string>(StringComparer.Ordinal));
    }

    private sealed record SetVersion(int Value);

    private sealed record SetWriteReply(GroupCommitWrite<SetVersion> Outcome);

    private sealed class OutcomeUnknownException(string message, Exception? inner) : IOException(message, inner);

    /// <summary>
    /// Yielding in-memory versioned set store whose reads and writes yield before and after taking effect so Clockwork's
    /// <see cref="WindowScheduleExplorer"/> enumerates every interleaving with concurrent <see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}"/>
    /// callers.
    /// </summary>
    private sealed class SimulatedSetStore
    {
        private readonly List<ImmutableSortedSet<string>> _history = [SetState.Empty.Keys];
        private readonly Queue<ReadFault> _readFaults = [];
        private readonly Queue<WriteFault> _writeFaults = [];

        public int Version => _history.Count - 1;

        public bool Contains(string key) => _history[^1].Contains(key);

        public ImmutableSortedSet<string> SnapshotAt(int version) => _history[version];

        public void EnqueueReadFault(ReadFault fault) => _readFaults.Enqueue(fault);

        public void EnqueueWriteFault(WriteFault fault) => _writeFaults.Enqueue(fault);

        public void WriteExternal(string key)
        {
            var next = _history[^1].Add(key);
            _history.Add(next);
        }

        public async ValueTask<GroupCommitRead<SetState, SetVersion>> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_readFaults.TryDequeue(out var fault))
            {
                await SimulationTasks.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return fault switch
                {
                    ReadFault.Fail => throw new IOException("Simulated read failure."),
                    ReadFault.Corrupt => new GroupCommitRead<SetState, SetVersion>.Corrupt(new InvalidDataException("Simulated corrupt value.")),
                    _ => throw new InvalidOperationException(),
                };
            }

            var version = Version;
            var snapshot = _history[version];

            // Yield after taking the snapshot so concurrent external writes and submissions can interleave before the read returns.
            await SimulationTasks.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return new GroupCommitRead<SetState, SetVersion>.Found(new SetState(snapshot), version == 0 ? null : new SetVersion(version));
        }

        public async ValueTask<SetWriteReply> WriteAsync(SetVersion? expectedVersion, byte[] encoded, CancellationToken cancellationToken)
        {
            await SimulationTasks.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            var fault = _writeFaults.TryDequeue(out var nextFault) ? nextFault : (WriteFault?)null;
            if (fault == WriteFault.Refused)
            {
                return new(new GroupCommitWrite<SetVersion>.Refused(new IOException("Simulated store refusal.")));
            }

            if (fault == WriteFault.UnknownUnlanded)
            {
                await SimulationTasks.Yield();
                throw new IOException("Simulated write unknown outcome (unlanded).");
            }

            var expected = expectedVersion?.Value ?? 0;
            if (expected != Version)
            {
                await SimulationTasks.Yield();
                return new(new GroupCommitWrite<SetVersion>.Mismatch());
            }

            var decoded = Decode(encoded);
            _history.Add(decoded.Keys);
            var appliedVersion = new SetVersion(Version);

            await SimulationTasks.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            return fault switch
            {
                WriteFault.UnknownLanded => throw new IOException("Simulated write unknown outcome (landed)."),
                WriteFault.AppliedMismatch => new(new GroupCommitWrite<SetVersion>.Mismatch()),
                WriteFault.UnknownUnlanded or WriteFault.Refused or null => new(new GroupCommitWrite<SetVersion>.Applied(appliedVersion)),
                _ => throw new InvalidOperationException(),
            };
        }

        public static SetState Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return SetState.Empty;
            }

            var text = Encoding.UTF8.GetString(bytes);
            var keys = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return new(ImmutableSortedSet.CreateRange(StringComparer.Ordinal, keys));
        }

        public static byte[] Encode(SetState state) =>
            Encoding.UTF8.GetBytes(string.Join('\n', state.Keys));
    }

    private sealed class SetRules(SimulatedSetStore store) : IGroupCommitRules<SetState, SetVersion, Change, ChangeOutcome, SetWriteReply>
    {
        public string? NextViolation { get; set; }

        public ValueTask<GroupCommitRead<SetState, SetVersion>> ReadAsync(CancellationToken cancellationToken) =>
            store.ReadAsync(cancellationToken);

        public IGroupCommitBatch<SetState, SetVersion, ChangeOutcome> Fold(SetState basis, bool heldWriteMayLand, IReadOnlyList<Change> changes)
        {
            var next = basis.Keys;
            var carries = new bool[changes.Count];
            var outcomes = new ChangeOutcome[changes.Count];
            var any = false;
            var needsFence = false;

            for (var index = 0; index < changes.Count; index++)
            {
                var change = changes[index];
                if (change.Kind == ChangeKind.Add)
                {
                    if (next.Contains(change.Key))
                    {
                        outcomes[index] = ChangeOutcome.AlreadyPresent;
                    }
                    else
                    {
                        next = next.Add(change.Key);
                        carries[index] = true;
                        outcomes[index] = ChangeOutcome.Added;
                        any = true;
                    }
                }
                else
                {
                    if (next.Contains(change.Key))
                    {
                        outcomes[index] = ChangeOutcome.Present;
                    }
                    else
                    {
                        outcomes[index] = ChangeOutcome.Absent;
                        if (heldWriteMayLand)
                        {
                            needsFence = true;
                        }
                    }
                }
            }

            var violation = NextViolation;
            NextViolation = null;
            var written = any ? new SetState(next) : (needsFence ? basis : null);
            return new SetBatch(changes, carries, outcomes, written, violation);
        }

        public GroupCommitEncoding Encode(SetState value) =>
            new(ArcBuffer.Wrap(SimulatedSetStore.Encode(value)), Crc32C: null);

        public bool DecodesAs(ReadOnlySequence<byte> encoded, SetState value)
        {
            var decoded = SimulatedSetStore.Decode(encoded.ToArray());
            return decoded.Keys.SetEquals(value.Keys);
        }

        public ValueTask<SetWriteReply> WriteAsync(SetVersion? expectedVersion, GroupCommitEncoding encoded, CancellationToken cancellationToken) =>
            store.WriteAsync(expectedVersion, encoded.Content.ToArray(), cancellationToken);

        public GroupCommitWrite<SetVersion> WriteOutcome(SetWriteReply reply) => reply.Outcome;

        public Exception OutcomeUnknown(string message, Exception cause) => new OutcomeUnknownException(message, cause);

        private sealed class SetBatch(
            IReadOnlyList<Change> changes,
            bool[] carries,
            ChangeOutcome[] outcomes,
            SetState? written,
            string? violation) : IGroupCommitBatch<SetState, SetVersion, ChangeOutcome>
        {
            public SetState? Written => written;

            public bool Carries(int index) => carries[index];

            public string? Violation(SetState before) => violation;

            public GroupCommitAnswer<ChangeOutcome> Settle(int index, SetState value) =>
                new GroupCommitAnswer<ChangeOutcome>.Answered(outcomes[index]);

            public GroupCommitAnswer<ChangeOutcome>? Applied(int index, SetState read) =>
                read.Keys.Contains(changes[index].Key)
                    ? new GroupCommitAnswer<ChangeOutcome>.Answered(ChangeOutcome.Added)
                    : (GroupCommitAnswer<ChangeOutcome>?)null;

            public GroupCommitAnswer<ChangeOutcome>? AfterUnknownOutcome(int index, SetState read, bool heldWriteMayLand)
            {
                var change = changes[index];
                if (read.Keys.Contains(change.Key))
                {
                    return new GroupCommitAnswer<ChangeOutcome>.Answered(change.Kind == ChangeKind.Add ? ChangeOutcome.AlreadyPresent : ChangeOutcome.Present);
                }

                return heldWriteMayLand ? (GroupCommitAnswer<ChangeOutcome>?)null : new GroupCommitAnswer<ChangeOutcome>.Answered(ChangeOutcome.Absent);
            }

            public void OnApplied(SetVersion version)
            {
            }
        }
    }
}
