using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCheck;
using Microsoft.Accordant;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Checks a system against an Accordant spec over operation sequences and concurrent groups which CsCheck generates and
/// shrinks, or which Accordant generates (test/Durable.Storage/docs/spec/verification.md).
/// </summary>
/// <typeparam name="TState">The spec's state.</typeparam>
/// <typeparam name="TSystem">The system under test, created afresh for every case.</typeparam>
/// <remarks>
/// <para>
/// The spec is the oracle, and the generators only choose what to run. Each case starts from a fresh system and the
/// spec's initial state. Every observed response folds through the spec's <c>Allows</c> over the state profile, or a
/// concurrent group's responses through <c>AllowsConcurrent</c>, so ambiguous outcomes (<c>Expect.OneOf</c>) and
/// pending background work (step functions) carry forward as sets of possible states.
/// </para>
/// <para>
/// A request runs only when the caller's preconditions hold in every possible state, so a generator draws requests
/// without knowing the state; the trace records the calls it skips. A harness action, such as fault injection or a
/// restart, changes the system without a spec operation, and calls registered with <see cref="Finally"/> end every
/// case. A system which implements <see cref="IConformanceEvents"/> adds its events, such as injected faults which
/// fired, to the trace. Each case carries a seed, which seeds its system.
/// </para>
/// <para>
/// Each case runs inside the kit's scope, such as a leak check's <c>InScopeAsync</c>, so a case which leaks fails like
/// one the spec rejects. A failing case reports its trace, the failing round, the spec's explanation, and the possible
/// states before the round. CsCheck shrinks a failure to the smallest failing case it finds, and the report names the seed which replays
/// it through <c>CLOG_TEST_SEED</c>. A concurrent group runs on the thread pool, so its interleaving does not replay,
/// unless an <see cref="IConformanceScheduler"/> runs each case in a simulation of its own and starts the group's calls
/// on its scheduler, where the case's seed, or the schedule an exploration chose, replays it (§4.1, §7.1).
/// </para>
/// </remarks>
internal sealed class ConformanceKit<TState, TSystem>
    where TState : class, IState
    where TSystem : IAsyncDisposable
{
    private readonly Microsoft.Accordant.Spec<TState> _spec;
    private readonly Func<TState> _initialState;
    private readonly Func<int, Task<TSystem>> _createSystem;
    private readonly Func<object, TState, bool> _precondition;
    private readonly Func<object, object, bool> _conflicts;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<Func<Task>, Task> _scope;
    private readonly IConformanceScheduler? _scheduler;
    private readonly Dictionary<string, Entry> _entries = [with(StringComparer.Ordinal)];
    private readonly List<ConformanceRound> _finally = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConformanceKit{TState, TSystem}"/> class.
    /// </summary>
    /// <param name="spec">The spec whose operations the kit runs.</param>
    /// <param name="initialState">Creates the state of a fresh system.</param>
    /// <param name="createSystem">Creates a fresh system for one case from the case's seed.</param>
    /// <param name="precondition">Whether the caller may issue a request in a state.</param>
    /// <param name="cancellationToken">Stops the kit between rounds.</param>
    /// <param name="scope">
    /// Runs each case, from its system's creation to its disposal, and fails the case if what it checks does not hold
    /// afterwards: for example, a leak check's <c>InScopeAsync</c>.
    /// </param>
    /// <param name="conflicts">
    /// Whether the caller may not issue the second request concurrently with the first, because a precondition of one
    /// rests on the other not running: for example, two writes of one value to one name, where the caller writes each
    /// value at most once.
    /// </param>
    /// <param name="scheduler">
    /// Runs each case and starts each call of a concurrent group, or <see langword="null"/> to run each case on the
    /// calling thread and start each call on the thread pool: a simulation runs each case in a simulation of its own and
    /// starts each call on its scheduler, so the group's interleaving replays.
    /// </param>
    internal ConformanceKit(
        Microsoft.Accordant.Spec<TState> spec,
        Func<TState> initialState,
        Func<int, Task<TSystem>> createSystem,
        Func<object, TState, bool> precondition,
        CancellationToken cancellationToken,
        Func<Func<Task>, Task>? scope = null,
        Func<object, object, bool>? conflicts = null,
        IConformanceScheduler? scheduler = null)
    {
        _spec = spec;
        _initialState = initialState;
        _createSystem = createSystem;
        _precondition = precondition;
        _cancellationToken = cancellationToken;
        _scope = scope ?? (static body => body());
        _conflicts = conflicts ?? (static (_, _) => false);
        _scheduler = scheduler;
    }

    /// <summary>
    /// Registers the spec operation <paramref name="name"/>, the requests to draw for it, and how the system executes it.
    /// </summary>
    /// <returns>This kit.</returns>
    internal ConformanceKit<TState, TSystem> Operation<TRequest, TResponse>(
        string name,
        Gen<TRequest> requests,
        Func<TSystem, TRequest, Task<TResponse>> execute,
        int weight = 1)
        where TRequest : notnull
        where TResponse : notnull
    {
        _entries.Add(name, new Entry(
            _spec.GetOperation(name),
            requests.Select(static request => (object)request),
            async (system, request) => await execute(system, (TRequest)request),
            weight));
        return this;
    }

    /// <summary>
    /// Registers a harness action, which changes the system without a spec operation, and the arguments to draw for it.
    /// An action of <paramref name="weight"/> 0 is never drawn, and runs only as a call of <see cref="Finally"/>.
    /// </summary>
    /// <returns>This kit.</returns>
    internal ConformanceKit<TState, TSystem> Action<TArgument>(
        string name,
        Gen<TArgument> arguments,
        Func<TSystem, TArgument, Task> act,
        int weight = 1)
        where TArgument : notnull
    {
        _entries.Add(name, new Entry(
            Operation: null,
            arguments.Select(static argument => (object)argument),
            async (system, argument) =>
            {
                await act(system, (TArgument)argument);
                return argument;
            },
            weight));
        return this;
    }

    /// <summary>
    /// Ends every case with <paramref name="calls"/>, one per round, after its generated rounds: for example, a recovery
    /// after injected faults, whose operations must then succeed.
    /// </summary>
    /// <returns>This kit.</returns>
    internal ConformanceKit<TState, TSystem> Finally(params ConformanceCall[] calls)
    {
        _finally.AddRange(calls.Select(static call => new ConformanceRound([call])));
        return this;
    }

    /// <summary>
    /// Generates sequences of up to <paramref name="maxLength"/> operations and actions.
    /// </summary>
    /// <returns>The generator.</returns>
    internal Gen<ConformanceCase> Sequences(int maxLength) =>
        Cases(Calls(actions: true).Select(static call => new ConformanceRound([call])).Array[1, maxLength]);

    /// <summary>
    /// Generates up to <paramref name="maxRounds"/> rounds: each one operation or action, or a group of up to
    /// <paramref name="maxGroupSize"/> operations which run concurrently.
    /// </summary>
    /// <returns>The generator.</returns>
    internal Gen<ConformanceCase> ConcurrentSequences(int maxRounds, int maxGroupSize = 3) =>
        Cases(Gen.Frequency(
                (2, Calls(actions: true).Select(static call => new ConformanceRound([call]))),
                (1, Calls(actions: false).Array[2, maxGroupSize].Select(static calls => new ConformanceRound(calls))))
            .Array[1, maxRounds]);

    /// <summary>
    /// Runs <paramref name="iterations"/> generated cases, starting from the CsCheck seed in <c>CLOG_TEST_SEED</c> when it
    /// is set. A failure is shrunk by CsCheck to the smallest failing case it finds, whose seed replays it, and then
    /// minimized by removing its calls one at a time while it still fails.
    /// </summary>
    /// <returns>A task which fails with the minimal case's report, the shrunk case, and its replay seed.</returns>
    internal async Task SampleAsync(Gen<ConformanceCase> cases, long iterations, int threads = -1)
    {
        ConformanceCase? shrunk = null;
        try
        {
            await cases.SampleAsync(
                RunAsync,
                seed: Environment.GetEnvironmentVariable(ConformanceReplay.SeedVariable) is { Length: > 0 } seed ? seed : null,
                iter: iterations,
                threads: threads,
                print: testCase =>
                {
                    shrunk = testCase;
                    return testCase.ToString();
                });
        }
        catch (CsCheckException failure) when (shrunk is not null)
        {
            var (minimal, report) = await MinimizeAsync(shrunk);
            var replay = ConformanceReplay.Instruction(failure.Message);
            Assert.Fail(report is null
                ? Invariant($"{failure.Message}{Environment.NewLine}{replay}")
                : Invariant($"{report}{Environment.NewLine}Minimized to {minimal.Rounds.Count} rounds from CsCheck's shrunk case:{Environment.NewLine}{minimal}{Environment.NewLine}{replay}{Environment.NewLine}{failure.Message}"));
        }
    }

    /// <summary>
    /// Returns Accordant generation options which explore up to <paramref name="maxDepth"/> operations and apply a request
    /// only where the caller's preconditions hold.
    /// </summary>
    /// <returns>The options.</returns>
    internal TestGenerationOptions GenerationOptions(int maxDepth) => new()
    {
        MaxDepth = maxDepth,
        ShouldApply = (input, state) => _precondition(input.Request, (TState)state),
    };

    /// <summary>
    /// Runs Accordant's generated sequential cases.
    /// </summary>
    /// <returns>A task which fails with the report of the first failing case.</returns>
    internal async Task RunAsync(IEnumerable<SequentialTestCase> testCases)
    {
        foreach (var testCase in testCases)
        {
            await RunAsync(new ConformanceCase(
                [.. testCase.OperationCalls.Select(static call => new ConformanceRound([Call(call)]))],
                testCase.Description,
                Seed(testCase.Description)));
        }
    }

    /// <summary>
    /// Runs Accordant's generated concurrent cases.
    /// </summary>
    /// <returns>A task which fails with the report of the first failing case.</returns>
    internal async Task RunAsync(IEnumerable<ConcurrentTestCase> testCases)
    {
        foreach (var testCase in testCases)
        {
            await RunAsync(new ConformanceCase(
                [.. testCase.Segments.Select(static segment => new ConformanceRound([.. segment.OperationCalls.Select(Call)]))],
                testCase.Description,
                Seed(testCase.Description)));
        }
    }

    /// <summary>
    /// Runs one case against a fresh system, inside the kit's scope.
    /// </summary>
    /// <returns>
    /// A task which fails with a report at the first round whose responses the spec does not allow, or with the scope's
    /// failure.
    /// </returns>
    internal Task RunAsync(ConformanceCase testCase) =>
        _scheduler is { } scheduler
            ? scheduler.RunCaseAsync(testCase, () => _scope(() => RunCaseAsync(testCase)))
            : _scope(() => RunCaseAsync(testCase));

    private async Task RunCaseAsync(ConformanceCase testCase)
    {
        List<ConformanceRound> rounds = [.. testCase.Rounds, .. _finally];
        var trace = new List<string>(rounds.Count);
        var profile = new StateProfile(_initialState());
        var system = await _createSystem(testCase.Seed);
        try
        {
            AddEvents(system, trace);
            for (var index = 0; index < rounds.Count; index++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                profile = await RunRoundAsync(system, testCase, rounds, index, profile, trace);
            }
        }
        finally
        {
            await system.DisposeAsync();
        }
    }

    private static ConformanceCall Call(OperationCall call) =>
        new(call.OperationInput.Operation.Name, call.OperationInput.Request);

    /// <summary>
    /// Returns a seed for an Accordant case which depends on its description alone, so a case replays with its seed.
    /// </summary>
    private static int Seed(string? description)
    {
        var hash = 2166136261u;
        foreach (var character in description ?? string.Empty)
        {
            hash = unchecked((hash ^ character) * 16777619u);
        }

        return (int)(hash & int.MaxValue);
    }

    private static Gen<ConformanceCase> Cases(Gen<ConformanceRound[]> rounds) =>
        Gen.Select(rounds, Gen.Int[0, int.MaxValue], static (rounds, seed) => new ConformanceCase(rounds, Seed: seed));

    private static void AddEvents(TSystem system, List<string> trace)
    {
        if (system is IConformanceEvents events)
        {
            trace.AddRange(events.TakeEvents().Select(static text => "   · " + text));
        }
    }

    /// <summary>
    /// Returns the cases with one call of <paramref name="testCase"/> removed, last call first.
    /// </summary>
    private static IEnumerable<ConformanceCase> WithoutOneCall(ConformanceCase testCase)
    {
        for (var round = testCase.Rounds.Count - 1; round >= 0; round--)
        {
            var calls = testCase.Rounds[round].Calls;
            for (var call = calls.Count - 1; call >= 0; call--)
            {
                List<ConformanceRound> rounds = [.. testCase.Rounds];
                if (calls.Count == 1)
                {
                    rounds.RemoveAt(round);
                }
                else
                {
                    rounds[round] = new ConformanceRound([.. calls.Where((_, position) => position != call)]);
                }

                if (rounds.Count > 0)
                {
                    yield return testCase with { Rounds = rounds };
                }
            }
        }
    }

    private static IEnumerable<TState> States(StateProfile profile) =>
        profile.StatesAndStepFunctions.Select(static possible => (TState)possible.State);

    private string Report(
        ConformanceCase testCase,
        IReadOnlyList<ConformanceRound> rounds,
        int index,
        List<string> trace,
        string round,
        List<string> events,
        string explanation,
        StateProfile profile)
    {
        var report = new StringBuilder();
        var description = testCase.Description is { } text ? Invariant($" of '{text}'") : string.Empty;
        _ = report.AppendLine(Invariant($"The spec does not allow round {index + 1} of {rounds.Count}{description}, seed {testCase.Seed}:"));
        foreach (var line in trace)
        {
            _ = report.AppendLine("  " + line);
        }

        _ = report.AppendLine(Invariant($"> {index + 1}. {round}"));
        foreach (var line in events)
        {
            _ = report.AppendLine("  " + line);
        }

        _ = report.AppendLine(Indent(explanation));
        _ = report.AppendLine("Possible states before the round:");
        foreach (var (state, pending) in profile.StatesAndStepFunctions)
        {
            var work = pending.Count == 0
                ? string.Empty
                : Invariant($"; pending {string.Join(", ", pending.Select(static step => step.StepFunctionId))}");
            _ = report.AppendLine(Indent(state + work));
        }

        if (_scheduler is null && rounds.Any(static candidate => candidate.Calls.Count > 1))
        {
            _ = report.AppendLine(
                "A concurrent group runs on the thread pool, so its interleaving does not replay unless the kit runs under Clockwork (verification §4.1, §7.1).");
        }

        return report.ToString();
    }

    private static string Indent(string text) =>
        "    " + text.Trim().ReplaceLineEndings(Environment.NewLine + "    ");

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private Gen<ConformanceCall> Calls(bool actions) =>
        Gen.Frequency(
        [
            .. _entries
                .Where(entry => entry.Value.Weight > 0 && (actions || entry.Value.Operation is not null))
                .Select(entry => (entry.Value.Weight, entry.Value.Requests.Select(request => new ConformanceCall(entry.Key, request)))),
        ]);

    /// <summary>
    /// Removes calls from a failing case one at a time while it still fails, until removing any call makes it pass.
    /// </summary>
    /// <returns>The minimal case and its report, which is <see langword="null"/> when no smaller case fails.</returns>
    private async Task<(ConformanceCase Case, string? Report)> MinimizeAsync(ConformanceCase testCase)
    {
        string? report = null;
        var smaller = true;
        while (smaller)
        {
            smaller = false;
            foreach (var candidate in WithoutOneCall(testCase))
            {
                if (await FailureAsync(candidate) is { } failure)
                {
                    (testCase, report, smaller) = (candidate, failure, true);
                    break;
                }
            }
        }

        return (testCase, report);
    }

    private async Task<string?> FailureAsync(ConformanceCase testCase)
    {
        try
        {
            await RunAsync(testCase);
            return null;
        }
#pragma warning disable CA1031 // Any failure keeps the smaller case; its report replaces the larger one's.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return exception.Message;
        }
    }

    private async Task<StateProfile> RunRoundAsync(
        TSystem system,
        ConformanceCase testCase,
        IReadOnlyList<ConformanceRound> rounds,
        int index,
        StateProfile profile,
        List<string> trace)
    {
        var round = rounds[index];
        var label = Invariant($"{index + 1}. ");
        if (round.Calls is [var only] && _entries[only.Name].Operation is null)
        {
            _ = await _entries[only.Name].Execute(system, only.Request);
            trace.Add(Invariant($"{label}[{only}]"));
            AddEvents(system, trace);
            return profile;
        }

        // The caller issues a request only when its preconditions hold in every state the system may be in, and none
        // conflicts with a request it issued earlier in the round.
        var calls = new List<ConformanceCall>();
        var skippedCalls = new List<string>();
        foreach (var call in round.Calls)
        {
            if (!States(profile).All(state => _precondition(call.Request, state)))
            {
                skippedCalls.Add(Invariant($"(skipped {call}: a precondition does not hold)"));
            }
            else if (calls.Exists(earlier => _conflicts(earlier.Request, call.Request)))
            {
                skippedCalls.Add(Invariant($"(skipped {call}: it conflicts with a concurrent call)"));
            }
            else
            {
                calls.Add(call);
            }
        }

        var skipped = skippedCalls;
        if (calls.Count == 0)
        {
            trace.Add(label + string.Join(" ‖ ", skipped));
            return profile;
        }

        object[] responses;
        if (calls.Count == 1)
        {
            responses = [await ExecuteAsync(system, calls[0])];
        }
        else
        {
            // Awaited one by one rather than through Task.WhenAll, which a simulation test assembly does not call
            // (test/Durable.Storage/eng/BannedSymbols.Simulation.txt): every call ends before the first fault, in call order, is thrown.
            _scheduler?.GroupStarting();
            var started = calls.Select(call => _scheduler is { } scheduler
                ? scheduler.Start(() => ExecuteAsync(system, call))
                : Task.Run(() => ExecuteAsync(system, call), CancellationToken.None)).ToList();
            foreach (var call in started)
            {
                await ((Task)call).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }

            _scheduler?.GroupCompleted();
            responses = [.. started.Select(static call => call.GetAwaiter().GetResult())];
        }
        var observed = string.Join(
            " ‖ ",
            calls.Select((call, position) => Invariant($"{call} → {responses[position]}")).Concat(skipped));
        var events = new List<string>();
        AddEvents(system, events);
        var (isValid, explanation, next) = calls.Count == 1
            ? _spec.Allows(_entries[calls[0].Name].Operation, calls[0].Request, responses[0], profile)
            : _spec.AllowsConcurrent(
                profile,
                [.. calls.Select((call, position) => (_entries[call.Name].Operation, call.Request, responses[position]))]);
        if (!isValid)
        {
            Assert.Fail(Report(testCase, rounds, index, trace, observed, events, explanation, profile));
        }

        trace.Add(label + observed);
        trace.AddRange(events);
        return next;
    }

    private async Task<object> ExecuteAsync(TSystem system, ConformanceCall call)
    {
        try
        {
            return await _entries[call.Name].Execute(system, call.Request);
        }
#pragma warning disable CA1031 // An exception is the system's response; the spec decides whether it is allowed.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return exception;
        }
    }

    private sealed record Entry(
        IOperation? Operation,
        Gen<object> Requests,
        Func<TSystem, object, Task<object>> Execute,
        int Weight);
}

/// <summary>
/// One spec operation and its request, or one harness action and its argument.
/// </summary>
internal sealed record ConformanceCall(string Name, object Request)
{
    public override string ToString() => Request.ToString() ?? Name;
}

/// <summary>
/// One operation or action, or a group of operations which run concurrently.
/// </summary>
internal sealed record ConformanceRound(IReadOnlyList<ConformanceCall> Calls)
{
    public override string ToString() => string.Join(" ‖ ", Calls);
}

/// <summary>
/// Replays a CsCheck case through <c>CLOG_TEST_SEED</c>, as a seeded simulation replays (verification §4.1).
/// </summary>
internal static partial class ConformanceReplay
{
    internal const string SeedVariable = "CLOG_TEST_SEED";

    /// <summary>
    /// Returns how to replay the case of a CsCheck failure whose message is <paramref name="message"/>.
    /// </summary>
    internal static string Instruction(string message) =>
        CsCheckSeed().Match(message) is { Success: true } match
            ? string.Create(CultureInfo.InvariantCulture, $"Replay the shrunk case with {SeedVariable}={match.Groups["seed"].Value}")
            : "CsCheck reported no seed.";

    [GeneratedRegex("CsCheck_Seed\\s*=\\s*\"?(?<seed>[^\\s\"]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CsCheckSeed();
}

/// <summary>
/// Runs a kit's cases and starts the calls of their concurrent groups: a simulation runs each case in a simulation of
/// its own, seeded by the case, and starts each call on its scheduler, so the case's operations and their interleaving
/// replay and shrink together; an exploration runs each case under every schedule of its concurrent windows within a
/// bound (verification.md §4.1).
/// </summary>
internal interface IConformanceScheduler
{
    /// <summary>
    /// Runs <paramref name="body"/>, which runs <paramref name="testCase"/> against a fresh system, once or under each
    /// schedule it explores.
    /// </summary>
    /// <returns>A task which fails with the first failing run's failure.</returns>
    Task RunCaseAsync(ConformanceCase testCase, Func<Task> body);

    /// <summary>
    /// Starts one call of a concurrent group as an operation of its own.
    /// </summary>
    /// <returns>The call's response.</returns>
    Task<object> Start(Func<Task<object>> call);

    /// <summary>
    /// Marks the start of a concurrent group, before its first call starts: its window of scheduling choices opens.
    /// </summary>
    void GroupStarting();

    /// <summary>
    /// Marks the end of a concurrent group, once every call has answered: its window closes.
    /// </summary>
    void GroupCompleted();
}

/// <summary>
/// A system under conformance test which reports events, such as injected faults which fired, for the kit to add to a
/// case's trace after each round.
/// </summary>
internal interface IConformanceEvents
{
    /// <summary>
    /// Returns the events since the last call, oldest first.
    /// </summary>
    IReadOnlyList<string> TakeEvents();
}

/// <summary>
/// A sequence of rounds which runs against one fresh system, created from <paramref name="Seed"/>.
/// </summary>
internal sealed record ConformanceCase(IReadOnlyList<ConformanceRound> Rounds, string? Description = null, int Seed = 0)
{
    public override string ToString() =>
        string.Join(
            Environment.NewLine,
            Rounds.Select(static (round, index) => string.Create(CultureInfo.InvariantCulture, $"  {index + 1}. {round}"))
                .Prepend(string.Create(CultureInfo.InvariantCulture, $"  seed {Seed}")));
}
