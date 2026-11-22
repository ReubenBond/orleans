using System.Diagnostics;
using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Replay;
using Orleans.Testing.Simulation.Runtime.Scheduling;

namespace Orleans.Testing.Simulation.Runtime.Exploration;

/// <summary>Configures bounded deterministic replay trace minimization.</summary>
public sealed record ReplayMinimizationOptions
{
    /// <summary>Gets the maximum number of failure-predicate attempts.</summary>
    public int MaxAttempts { get; init; } = 1000;

    /// <summary>Gets an optional wall-clock safety bound checked between predicate attempts.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Gets whether discrete scheduling and resource choices may be simplified.</summary>
    public bool MinimizeChoiceAlternatives { get; init; } = true;
}

/// <summary>The deterministic observation returned by a minimization failure predicate.</summary>
public sealed record ReplayFailureObservation
{
    /// <summary>Gets whether the candidate reproduced a terminal failure.</summary>
    public required bool Reproduced { get; init; }

    /// <summary>Gets the observed terminal category, when execution reached one.</summary>
    public ReplayTerminationKind? Kind { get; init; }

    /// <summary>Gets the observed stable failure identity, when execution reached one.</summary>
    public string? FailureIdentity { get; init; }

    /// <summary>Gets a stable rejection reason such as divergence or compatibility mismatch.</summary>
    public string? RejectionReason { get; init; }
}

/// <summary>One reproducible minimization attempt.</summary>
public sealed record ReplayMinimizationProgress
{
    /// <summary>Gets the one-based attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>Gets the attempted transformation.</summary>
    public required string Action { get; init; }

    /// <summary>Gets the candidate decision count.</summary>
    public required int CandidateDecisionCount { get; init; }

    /// <summary>Gets whether the candidate preserved the target failure.</summary>
    public required bool Accepted { get; init; }

    /// <summary>Gets the canonical candidate artifact id.</summary>
    public required string CandidateArtifactId { get; init; }
}

/// <summary>Result of bounded deterministic trace minimization.</summary>
public sealed record ReplayMinimizationResult
{
    /// <summary>Gets the original artifact.</summary>
    public required ReplayArtifact OriginalArtifact { get; init; }

    /// <summary>Gets the smallest accepted artifact.</summary>
    public required ReplayArtifact MinimizedArtifact { get; init; }

    /// <summary>Gets the original decision count.</summary>
    public int OriginalDecisionCount => OriginalArtifact.Decisions.Count;

    /// <summary>Gets the minimized decision count.</summary>
    public int MinimizedDecisionCount => MinimizedArtifact.Decisions.Count;

    /// <summary>Gets the number of predicate attempts, excluding baseline verification.</summary>
    public required int Attempts { get; init; }

    /// <summary>Gets whether the final artifact was verified against the same failure identity/category.</summary>
    public required bool Verified { get; init; }

    /// <summary>Gets stable progress records in attempt order.</summary>
    public required IReadOnlyList<ReplayMinimizationProgress> Progress { get; init; }
}

/// <summary>Thrown when minimization cannot establish a valid deterministic failure baseline.</summary>
public sealed class ReplayMinimizationException : InvalidOperationException
{
    /// <summary>Initializes a minimization exception.</summary>
    public ReplayMinimizationException(string message)
        : base(message)
    {
    }
}

/// <summary>Creates exact-replay failure predicates for scenario harnesses.</summary>
public static class ReplayFailurePredicates
{
    /// <summary>Creates a predicate which exact-replays a scenario and rejects divergence.</summary>
    public static Func<ReplayArtifact, ReplayFailureObservation> ForScenario(
        ReplayCompatibilityRequirements requirements,
        Action<SimulationScheduler> scenario,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSteps);

        return artifact =>
        {
            try
            {
                ReplayExecutionResult execution = ReplayRunner.Replay(
                    artifact,
                    requirements,
                    scenario,
                    maxSteps,
                    cancellationToken);
                ReplayOutcome outcome = execution.Artifact.Outcome;
                return new ReplayFailureObservation
                {
                    Reproduced = IsFailure(outcome.Kind),
                    Kind = outcome.Kind,
                    FailureIdentity = outcome.FailureIdentity,
                };
            }
            catch (SimulationDecisionReplayMismatchException exception)
            {
                return Rejected("divergence", exception.Message);
            }
            catch (ReplayOutcomeMismatchException exception)
            {
                return new ReplayFailureObservation
                {
                    Reproduced = false,
                    Kind = exception.Actual.Kind,
                    FailureIdentity = exception.Actual.FailureIdentity,
                    RejectionReason = "outcome-mismatch",
                };
            }
            catch (ReplayCompatibilityException exception)
            {
                return Rejected("compatibility", exception.Message);
            }
            catch (SimulationSchedulerException exception)
            {
                return Rejected("controlled-execution", exception.Message);
            }
        };
    }

    private static bool IsFailure(ReplayTerminationKind kind) =>
        kind is ReplayTerminationKind.Faulted or
            ReplayTerminationKind.Canceled or
            ReplayTerminationKind.Deadlocked or
            ReplayTerminationKind.RaceDetected or
            ReplayTerminationKind.BoundExceeded;

    private static ReplayFailureObservation Rejected(string category, string diagnostic) => new()
    {
        Reproduced = false,
        RejectionReason = string.Create(
            CultureInfo.InvariantCulture,
            $"{category}:{diagnostic}"),
    };
}

/// <summary>
/// Delta-debugs deterministic decision traces. Every candidate is replayed by the supplied predicate;
/// divergence and compatibility rejection are ordinary failed attempts, never accepted shortcuts.
/// </summary>
public static class ReplayTraceMinimizer
{
    /// <summary>Minimizes a replay artifact against a deterministic failure predicate.</summary>
    public static ReplayMinimizationResult Minimize(
        ReplayArtifact artifact,
        ReplayMinimizationOptions configuration,
        Func<ReplayArtifact, ReplayFailureObservation> failurePredicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(failurePredicate);
        ValidateConfiguration(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        if (artifact.Outcome.Kind == ReplayTerminationKind.Aborted)
        {
            throw new ReplayMinimizationException("Replay artifacts with an Aborted outcome cannot be minimized.");
        }

        ReplayFailureObservation baseline = failurePredicate(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        if (!MatchesTarget(artifact.Outcome, baseline))
        {
            throw new ReplayMinimizationException(
                "The original artifact did not reproduce its recorded failure identity and category.");
        }

        var stopwatch = Stopwatch.StartNew();
        var progress = new List<ReplayMinimizationProgress>();
        ReplayArtifact current = artifact;
        var attempts = 0;

        current = MinimizeSubsequences(
            current,
            artifact.Outcome,
            configuration,
            failurePredicate,
            stopwatch,
            progress,
            ref attempts,
            cancellationToken);
        if (configuration.MinimizeChoiceAlternatives && CanAttempt(configuration, stopwatch, attempts))
        {
            current = MinimizeAlternatives(
                current,
                artifact.Outcome,
                configuration,
                failurePredicate,
                stopwatch,
                progress,
                ref attempts,
                cancellationToken);
            current = MinimizeSubsequences(
                current,
                artifact.Outcome,
                configuration,
                failurePredicate,
                stopwatch,
                progress,
                ref attempts,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReplayFailureObservation final = failurePredicate(current);
        cancellationToken.ThrowIfCancellationRequested();
        bool verified = MatchesTarget(artifact.Outcome, final);
        if (!verified)
        {
            throw new ReplayMinimizationException(
                "The final minimized artifact no longer reproduces the recorded failure identity and category.");
        }

        return new ReplayMinimizationResult
        {
            OriginalArtifact = artifact,
            MinimizedArtifact = current,
            Attempts = attempts,
            Verified = true,
            Progress = progress,
        };
    }

    private static ReplayArtifact MinimizeSubsequences(
        ReplayArtifact current,
        ReplayOutcome target,
        ReplayMinimizationOptions configuration,
        Func<ReplayArtifact, ReplayFailureObservation> predicate,
        Stopwatch stopwatch,
        List<ReplayMinimizationProgress> progress,
        ref int attempts,
        CancellationToken cancellationToken)
    {
        var granularity = 2;
        while (current.Decisions.Count > 1 && CanAttempt(configuration, stopwatch, attempts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = current.Decisions.Count;
            int chunkSize = (count + granularity - 1) / granularity;
            var reduced = false;
            for (var start = 0; start < count && CanAttempt(configuration, stopwatch, attempts); start += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(chunkSize, count - start);
                ReplayArtifact candidate = WithDecisions(
                    current,
                    current.Decisions.Take(start).Concat(current.Decisions.Skip(start + length)));
                if (TryCandidate(
                    candidate,
                    target,
                    $"remove[{start.ToString(CultureInfo.InvariantCulture)}..{(start + length).ToString(CultureInfo.InvariantCulture)})",
                    predicate,
                    progress,
                    ref attempts,
                    cancellationToken))
                {
                    current = candidate;
                    granularity = Math.Max(2, granularity - 1);
                    reduced = true;
                    break;
                }
            }

            if (reduced)
            {
                continue;
            }

            if (granularity >= count)
            {
                break;
            }

            granularity = Math.Min(count, granularity * 2);
        }

        return current;
    }

    private static ReplayArtifact MinimizeAlternatives(
        ReplayArtifact current,
        ReplayOutcome target,
        ReplayMinimizationOptions configuration,
        Func<ReplayArtifact, ReplayFailureObservation> predicate,
        Stopwatch stopwatch,
        List<ReplayMinimizationProgress> progress,
        ref int attempts,
        CancellationToken cancellationToken)
    {
        for (var index = 0;
             index < current.Decisions.Count && CanAttempt(configuration, stopwatch, attempts);
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayDecision decision = current.Decisions[index];
            string[] alternatives = ParseAlternatives(decision);
            foreach (string alternative in alternatives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(alternative, decision.SelectedResult, StringComparison.Ordinal) ||
                    !CanAttempt(configuration, stopwatch, attempts))
                {
                    continue;
                }

                ReplayDecision replacement = decision with { SelectedResult = alternative };
                ReplayArtifact candidate = ReplaceDecision(current, index, replacement, truncateAfter: false);
                if (TryCandidate(
                    candidate,
                    target,
                    $"choice[{index.ToString(CultureInfo.InvariantCulture)}]={alternative}",
                    predicate,
                    progress,
                    ref attempts,
                    cancellationToken))
                {
                    current = candidate;
                    decision = replacement;
                    break;
                }

                if (index + 1 < current.Decisions.Count &&
                    CanAttempt(configuration, stopwatch, attempts))
                {
                    candidate = ReplaceDecision(current, index, replacement, truncateAfter: true);
                    if (TryCandidate(
                        candidate,
                        target,
                        $"choice[{index.ToString(CultureInfo.InvariantCulture)}]={alternative};truncate",
                        predicate,
                        progress,
                        ref attempts,
                        cancellationToken))
                    {
                        return candidate;
                    }
                }
            }
        }

        return current;
    }

    private static bool TryCandidate(
        ReplayArtifact candidate,
        ReplayOutcome target,
        string action,
        Func<ReplayArtifact, ReplayFailureObservation> predicate,
        List<ReplayMinimizationProgress> progress,
        ref int attempts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        attempts++;
        ReplayFailureObservation observation = predicate(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        bool accepted = MatchesTarget(target, observation);
        progress.Add(new ReplayMinimizationProgress
        {
            Attempt = attempts,
            Action = action,
            CandidateDecisionCount = candidate.Decisions.Count,
            Accepted = accepted,
            CandidateArtifactId = ReplayArtifactSerializer.ComputeId(candidate),
        });
        return accepted;
    }

    private static ReplayArtifact ReplaceDecision(
        ReplayArtifact artifact,
        int index,
        ReplayDecision replacement,
        bool truncateAfter)
    {
        IEnumerable<ReplayDecision> decisions = truncateAfter
            ? artifact.Decisions.Take(index).Append(replacement)
            : artifact.Decisions.Select((decision, decisionIndex) =>
                decisionIndex == index ? replacement : decision);
        return WithDecisions(artifact, decisions);
    }

    private static ReplayArtifact WithDecisions(
        ReplayArtifact artifact,
        IEnumerable<ReplayDecision> decisions)
    {
        ReplayDecision[] normalized = decisions
            .Select(static (decision, index) => decision with { Sequence = index })
            .ToArray();
        return artifact with { Decisions = normalized };
    }

    private static string[] ParseAlternatives(ReplayDecision decision)
    {
        if (decision.Kind == SimulationDecisionKind.SchedulingOrder)
        {
            return SplitCandidates(decision.InputMetadata);
        }

        if (decision.Kind == SimulationDecisionKind.ResourceWinner &&
            decision.InputMetadata is { } resourceInput)
        {
            int separator = resourceInput.IndexOf(";waiters=", StringComparison.Ordinal);
            return separator < 0
                ? []
                : SplitCandidates(resourceInput[(separator + ";waiters=".Length)..]);
        }

        return [];
    }

    private static string[] SplitCandidates(string? candidates) =>
        string.IsNullOrEmpty(candidates)
            ? []
            : candidates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MatchesTarget(ReplayOutcome target, ReplayFailureObservation observation) =>
        observation.Reproduced &&
        observation.Kind == target.Kind &&
        string.Equals(observation.FailureIdentity, target.FailureIdentity, StringComparison.Ordinal);

    private static bool CanAttempt(
        ReplayMinimizationOptions configuration,
        Stopwatch stopwatch,
        int attempts) =>
        attempts < configuration.MaxAttempts &&
        (configuration.TimeLimit is null || stopwatch.Elapsed < configuration.TimeLimit.Value);

    private static void ValidateConfiguration(ReplayMinimizationOptions configuration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.MaxAttempts);
        if (configuration.TimeLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "TimeLimit must be positive when specified.");
        }
    }
}
