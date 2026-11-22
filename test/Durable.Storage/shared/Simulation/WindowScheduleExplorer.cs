using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// One scheduling choice inside a concurrent window: the runnable operations, by ID in the scheduler's order, and the
/// index of the one chosen.
/// </summary>
/// <param name="Candidates">The runnable operations' IDs, ascending.</param>
/// <param name="Chosen">The index of the chosen operation.</param>
internal sealed record ScheduleChoice(IReadOnlyList<long> Candidates, int Chosen);

/// <summary>
/// Chooses the next operation by a choice vector while a concurrent window is open and by the run's own strategy outside
/// every window, recording each choice it makes inside one (verification.md §4.1).
/// </summary>
/// <remarks>
/// <para>
/// A choice point is a step at which two or more operations are runnable while a window is open. At point <c>k</c> the
/// strategy takes the <c>k</c>th entry of its prefix, and past the prefix the default: the newest runnable operation, the
/// continuation of the work which ran last, so the default schedule runs each call on until it waits, as a
/// non-preemptive scheduler would, and each deviation is a preemption, as CHESS counts them. Choice <c>i</c> at a point is
/// the <c>i</c>th newest operation. Outside every window the run's seeded strategy chooses, so the rounds before and
/// after a window replay from the case's seed alone.
/// </para>
/// <para>
/// Stateless exploration rests on a run's choice points depending only on the choices before them. The strategy checks
/// it: where the prefix came from an earlier run, the candidates at each point of it must be that run's, and a run which
/// meets other candidates records the divergence, which fails the exploration rather than reporting a schedule it did not
/// take.
/// </para>
/// </remarks>
internal sealed class WindowSchedulingStrategy(
    ISimulationSchedulingStrategy outside,
    IReadOnlyList<int> prefix,
    IReadOnlyList<ScheduleChoice>? expected) : ISimulationSchedulingStrategy
{
    private readonly List<ScheduleChoice> _choices = [];
    private int _open;

    public string Name => "clog-window-schedule";

    public bool RecordsNondeterministicChoices => true;

    /// <summary>
    /// Gets the choices the run made inside its windows, in order.
    /// </summary>
    public IReadOnlyList<ScheduleChoice> Choices => _choices;

    /// <summary>
    /// Gets how the run left the schedule it was given, or <see langword="null"/> when it followed it.
    /// </summary>
    public string? Divergence { get; private set; }

    /// <summary>
    /// Gets the number of windows the run opened.
    /// </summary>
    public int Windows { get; private set; }

    /// <summary>
    /// Opens a window. Windows nest: choices are the vector's until the outermost closes.
    /// </summary>
    public void Open()
    {
        if (Volatile.Read(ref _open) == 0)
        {
            Windows++;
        }

        _ = Interlocked.Increment(ref _open);
    }

    /// <summary>
    /// Closes the innermost open window.
    /// </summary>
    public void Close() => _ = Interlocked.Decrement(ref _open);

    public SimulationOperation ChooseNext(SimulationSchedulingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Volatile.Read(ref _open) == 0 || context.Runnable.Count == 1)
        {
            return outside.ChooseNext(context);
        }

        var point = _choices.Count;
        var candidates = new long[context.Runnable.Count];
        for (var index = 0; index < candidates.Length; index++)
        {
            candidates[index] = context.Runnable[index].Id.Value;
        }

        var chosen = 0;
        if (point < prefix.Count)
        {
            chosen = prefix[point];
            if (expected is not null && point < expected.Count && !expected[point].Candidates.SequenceEqual(candidates))
            {
                Divergence ??= string.Create(
                    CultureInfo.InvariantCulture,
                    $"choice point {point} offered [{string.Join(",", candidates)}] where the run it extends offered [{string.Join(",", expected[point].Candidates)}]");
            }

            if (chosen >= candidates.Length)
            {
                Divergence ??= string.Create(CultureInfo.InvariantCulture, $"choice point {point} offered {candidates.Length} operations, fewer than choice {chosen} needs");
                chosen = 0;
            }
        }

        _choices.Add(new ScheduleChoice(candidates, chosen));
        return context.Runnable[candidates.Length - 1 - chosen];
    }
}

/// <summary>
/// The bound of an exploration: the most schedules it runs per case, and the most choice points of a schedule which
/// leave the default order.
/// </summary>
/// <param name="MaxSchedules">The most schedules per case.</param>
/// <param name="MaxDeviations">The most deviations from the default order per schedule.</param>
internal sealed record ScheduleBound(int MaxSchedules, int MaxDeviations)
{
    /// <summary>
    /// The environment variable which selects the bound: <c>deep</c> for <see cref="Deep"/>, at milestones and in the
    /// nightly window (verification.md §12); otherwise <see cref="Gate"/>.
    /// </summary>
    internal const string Variable = "DURABLE_STORAGE_SCHEDULE_BOUND";

    /// <summary>
    /// Gets the bound of a gate run: every schedule of a case's windows with at most one preemption, up to 128 of them.
    /// </summary>
    public static ScheduleBound Gate { get; } = new(128, 1);

    /// <summary>
    /// Gets the bound of a milestone or nightly run: up to two preemptions, up to 2,000 schedules per case.
    /// </summary>
    public static ScheduleBound Deep { get; } = new(2_000, 2);

    /// <summary>
    /// Gets the bound <see cref="Variable"/> selects.
    /// </summary>
    public static ScheduleBound Current => IsDeep ? Deep : Gate;

    /// <summary>
    /// Gets a value indicating whether <see cref="Variable"/> selects the deep bound, under which a test also explores the
    /// cases its gate run leaves to sampling.
    /// </summary>
    public static bool IsDeep =>
        string.Equals(Environment.GetEnvironmentVariable(Variable), "deep", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One schedule an exploration ran: its prefix of choices, the choices the run made, its failure, and how it left the
/// schedule it was given.
/// </summary>
internal sealed record ScheduleRun(IReadOnlyList<int> Prefix, IReadOnlyList<ScheduleChoice> Choices, Exception? Failure, string? Divergence);

/// <summary>
/// How one case's exploration ended.
/// </summary>
/// <param name="Schedules">The schedules it ran.</param>
/// <param name="ChoicePoints">The most choice points any of them met.</param>
/// <param name="Level">The most deviations up to which it ran every schedule.</param>
/// <param name="Complete">Whether it ran every schedule of the case's windows, so no bound cut any off.</param>
/// <param name="Failure">The first failing schedule, or <see langword="null"/>.</param>
internal sealed record ScheduleExploration(int Schedules, int ChoicePoints, int Level, bool Complete, ScheduleRun? Failure)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Schedules} schedule{(Schedules == 1 ? string.Empty : "s")} over up to {ChoicePoints} choice points, {(Complete ? "every schedule" : $"every schedule within {Level} deviation{(Level == 1 ? string.Empty : "s")}")}");
}

/// <summary>
/// Enumerates the schedules of a case's concurrent windows by re-execution, the fewest deviations from the default order
/// first: stateless model checking with a delay bound, as CHESS and Coyote explore (verification.md §4.1).
/// </summary>
/// <remarks>
/// <para>
/// Level 0 is the default schedule. Each schedule with <c>d</c> deviations has one parent with <c>d − 1</c>, the schedule
/// without its last deviation, which ran to the default order past it; so level <c>d</c> is every parent at level
/// <c>d − 1</c> with one more deviation at a choice point after its last, and each schedule runs once. A level which adds
/// none ends the exploration complete: every schedule of the windows ran. Otherwise the bound ends it, at
/// <see cref="ScheduleBound.MaxDeviations"/> or at <see cref="ScheduleBound.MaxSchedules"/>.
/// </para>
/// <para>
/// A choice is a step's runnable operation, so the explored interleavings are those of Clockwork's controlled scheduling:
/// at every <c>await</c>, yield, task start, lock, and timer, not between memory accesses.
/// </para>
/// </remarks>
internal static class WindowScheduleExplorer
{
    /// <summary>
    /// The environment variable which replays one schedule of each explored case: its deviations, as
    /// <c>point:choice</c> pairs separated by commas, or <c>default</c>.
    /// </summary>
    internal const string ScheduleVariable = "DURABLE_STORAGE_SCHEDULE";

    /// <summary>
    /// Runs the schedules of a case within <paramref name="bound"/>, stopping at the first which fails.
    /// </summary>
    /// <param name="bound">The bound.</param>
    /// <param name="run">Runs one schedule from its prefix, checked against the candidates of the run it extends.</param>
    /// <returns>How the exploration ended.</returns>
    internal static ScheduleExploration Explore(ScheduleBound bound, Func<IReadOnlyList<int>, IReadOnlyList<ScheduleChoice>?, ScheduleRun> run)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(run);
        if (Environment.GetEnvironmentVariable(ScheduleVariable) is { Length: > 0 } replayed)
        {
            var only = run(ParsePrefix(replayed), null);
            return new ScheduleExploration(1, only.Choices.Count, Deviations(only.Prefix), Complete: false, Failed(only));
        }

        var first = run([], null);
        var schedules = 1;
        var points = first.Choices.Count;
        if (Failed(first) is { } failure)
        {
            return new ScheduleExploration(schedules, points, 0, Complete: false, failure);
        }

        List<(ScheduleRun Run, int Last)> frontier = [(first, -1)];
        for (var level = 1; level <= bound.MaxDeviations; level++)
        {
            List<(ScheduleRun Run, int Last)> next = [];
            foreach (var (parent, last) in frontier)
            {
                for (var point = last + 1; point < parent.Choices.Count; point++)
                {
                    for (var choice = 1; choice < parent.Choices[point].Candidates.Count; choice++)
                    {
                        if (schedules >= bound.MaxSchedules)
                        {
                            return new ScheduleExploration(schedules, points, level - 1, Complete: false, null);
                        }

                        List<int> prefix = [.. parent.Choices.Take(point).Select(static made => made.Chosen), choice];
                        var child = run(prefix, [.. parent.Choices.Take(point + 1)]);
                        schedules++;
                        points = Math.Max(points, child.Choices.Count);
                        if (Failed(child) is { } failed)
                        {
                            return new ScheduleExploration(schedules, points, level - 1, Complete: false, failed);
                        }

                        next.Add((child, point));
                    }
                }
            }

            if (next.Count == 0)
            {
                return new ScheduleExploration(schedules, points, level - 1, Complete: true, null);
            }

            frontier = next;
        }

        var complete = frontier.TrueForAll(static entry => !HasChildren(entry.Run, entry.Last));
        return new ScheduleExploration(schedules, points, bound.MaxDeviations, complete, null);
    }

    /// <summary>
    /// Formats a prefix as the deviations <see cref="ScheduleVariable"/> replays.
    /// </summary>
    internal static string Format(IReadOnlyList<int> prefix)
    {
        var deviations = prefix.Select(static (choice, point) => (choice, point)).Where(static entry => entry.choice != 0).ToList();
        return deviations.Count == 0
            ? "default"
            : string.Join(",", deviations.Select(static entry => string.Create(CultureInfo.InvariantCulture, $"{entry.point}:{entry.choice}")));
    }

    /// <summary>
    /// Parses the deviations <see cref="Format"/> writes into a prefix.
    /// </summary>
    internal static IReadOnlyList<int> ParsePrefix(string deviations)
    {
        if (string.Equals(deviations.Trim(), "default", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var pairs = deviations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(pair =>
        {
            var parts = pair.Split(':');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var point)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var choice)
                ? (point, choice)
                : throw new FormatException($"{ScheduleVariable} holds '{deviations}'; expected 'default' or point:choice pairs such as '3:1,7:2'.");
        }).ToList();
        var prefix = new int[pairs.Count == 0 ? 0 : pairs.Max(static pair => pair.point) + 1];
        foreach (var (point, choice) in pairs)
        {
            prefix[point] = choice;
        }

        return prefix;
    }

    private static int Deviations(IReadOnlyList<int> prefix) => prefix.Count(static choice => choice != 0);

    private static ScheduleRun? Failed(ScheduleRun run) => run.Failure is not null || run.Divergence is not null ? run : null;

    private static bool HasChildren(ScheduleRun run, int last)
    {
        for (var point = last + 1; point < run.Choices.Count; point++)
        {
            if (run.Choices[point].Candidates.Count > 1)
            {
                return true;
            }
        }

        return false;
    }
}
