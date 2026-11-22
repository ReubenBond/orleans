using System.Globalization;
using Microsoft.Accordant;
using Orleans.Durable.Storage.FileSystem.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// Crash-state enumeration of the chunk layout over the file-system object store (verification.md §7.5,
/// archive-logs.md §7.4), judged by the Accordant <see cref="ArchiveLogStoreSpec"/>: a key's first append run, appends,
/// openers, reads, a listing, and the sweeper's deletion run once over a recorded file system, and in every state an
/// operating-system crash can leave, a fresh store's properties, read, and listing are what the spec allows after every
/// answer given by the crash point, so no crash revokes an answer (conventions.md §13). A call unanswered at the crash
/// point has an unknown outcome, which may still take effect once.
/// </summary>
public sealed class ChunkArchiveLogCrashStateTests : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-chunk-logs"));

    private static readonly object[] Workload =
    [
        new LogAppend(0, 0, 0, [1, 1]),
        LogRead.Instance,
        new LogOpen(1, 2, 2, SpecOpener.Replace, 1),
        new LogAppend(2, 0, 0, [2]),
        LogProperties.Instance,
        new LogOpen(2, 2, 1, SpecOpener.DeleteWithTombstone, 0),
        LogList.Instance,
        new LogDeleteBase(1),
        new LogAppend(3, 0, 0, [1]),
        LogRead.Instance,
    ];

    private static readonly object[] Recovery = [LogProperties.Instance, LogRead.Instance, LogList.Instance];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly Spec<ArchiveLogStoreState> _spec = ArchiveLogStoreSpec.Create(unknownOutcomes: true, chunkLayout: true);
    private readonly List<Call> _calls = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public Task EveryAnswerHoldsInEveryCrashState() => SeededSimulation.RunAsync(0x0c1a, async run =>
    {
        var recording = new RecordingFileSystem(Root);
        var executor = Executor(recording);
        foreach (var request in Workload)
        {
            var start = recording.OperationCount;
            var outcome = await executor.RunAsync(request, Token);
            _calls.Add(new Call(start, recording.OperationCount, request, outcome));
            _ = AllowedStates(recording.OperationCount);
        }

        Assert.Equal(
            [LogOutcomeKind.Applied, LogOutcomeKind.Read, LogOutcomeKind.Applied, LogOutcomeKind.Applied, LogOutcomeKind.Properties, LogOutcomeKind.Applied, LogOutcomeKind.Listed, LogOutcomeKind.Deleted, LogOutcomeKind.Applied, LogOutcomeKind.Read],
            _calls.Select(static call => call.Outcome.Kind));

        var enumerator = CrashStateEnumerator.Of(recording, run.Seed);
        run.UseHistory(() => enumerator.Operations.Select(static (operation, index) => string.Create(CultureInfo.InvariantCulture, $"#{index} {operation}")));
        var states = enumerator.Enumerate(0, enumerator.Operations.Count, int.MaxValue);
        Assert.Equal(0, states.SampledPoints);
        var violations = new List<string>();
        foreach (var state in states.States)
        {
            if (await JudgeAsync(enumerator, state) is { } violation)
            {
                violations.Add(violation);
            }
        }

        TestDiagnostics.WriteLine(states.ToString());
        Assert.True(violations.Count == 0, string.Join('\n', violations));
    });

    private static ArchiveLogStoreExecutor Executor(RecordingFileSystem fileSystem) =>
        new(new ChunkArchiveLogStore(new FileSystemObjectStore(Root, TimeProvider.System, fileSystem)));

    private async Task<string?> JudgeAsync(CrashStateEnumerator enumerator, CrashState state)
    {
        var executor = Executor(new RecordingFileSystem(Root, enumerator.Build(state)));
        var profile = AllowedStates(state.Point);
        foreach (var request in Recovery)
        {
            LogOutcome outcome;
            try
            {
                outcome = await executor.RunAsync(request, Token);
            }
            catch (ArchiveLogCorruptException exception)
            {
                return string.Create(CultureInfo.InvariantCulture, $"At point {state.Point} the store recovered {request} as {exception.Message}\n{enumerator.Describe(state)}");
            }

            var (allowed, explanation, next) = _spec.Allows(_spec.GetOperation(ArchiveLogStoreSpec.OperationOf(request)), request, outcome, profile);
            if (!allowed)
            {
                return string.Create(CultureInfo.InvariantCulture, $"At point {state.Point} the store recovered {request} → {outcome}: {explanation}\n{enumerator.Describe(state)}");
            }

            profile = next;
        }

        return null;
    }

    /// <summary>
    /// Returns the states the spec allows after the first <paramref name="point"/> operations: each call answered by then
    /// has its answer, and each begun but unanswered call an unknown outcome.
    /// </summary>
    private StateProfile AllowedStates(int point)
    {
        var profile = new StateProfile(new ArchiveLogStoreState());
        foreach (var call in _calls.Where(call => call.Start < point || call.Start == point && call.End == point))
        {
            var outcome = call.End <= point ? call.Outcome : LogOutcome.Unknown;
            var (allowed, explanation, next) = _spec.Allows(_spec.GetOperation(ArchiveLogStoreSpec.OperationOf(call.Request)), call.Request, outcome, profile);
            Assert.True(allowed, string.Create(CultureInfo.InvariantCulture, $"At point {point}, {call.Request} → {outcome}: {explanation}"));
            profile = next;
        }

        return profile;
    }

    private sealed record Call(int Start, int End, object Request, LogOutcome Outcome);
}
