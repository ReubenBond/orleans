using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// The invariants a simulation checks after every step (test/Durable.Storage/docs/spec/verification.md §3.2, §7).
/// </summary>
/// <remarks>
/// <para>
/// A check is a global predicate over the state the system publishes, and holds no model of its own. It returns
/// <see langword="null"/> when its invariant holds and a description of the violation otherwise.
/// </para>
/// <para>
/// The driver runs every check between two steps, outside any simulated operation, where Clockwork's controlled APIs
/// are unavailable. A check therefore reads published state synchronously: it takes no lock, awaits nothing, and calls
/// nothing which the build rewrites. Kit components publish what their checks read as they change it.
/// </para>
/// <para>
/// The driver runs the checks in the order they were added, and the first which fails is the violation. A check which
/// throws cannot vouch for its invariant, so its exception is its violation. A check which drives the cluster, as the
/// driver's quiescence check runs the work due at its instant, can meet a
/// <see cref="SimulationThreadAffinityException"/> which work outside the scheduler raised. That exception belongs to
/// the run: it propagates, so Clockwork aborts the run (test/Durable.Storage/docs/spec/verification.md §7.4).
/// </para>
/// </remarks>
public sealed class InvariantChecks
{
    private readonly List<(string Name, Func<string?> Check)> _checks = [];

    /// <summary>
    /// Gets the number of checks.
    /// </summary>
    public int Count => _checks.Count;

    /// <summary>
    /// Gets the checks in the order they were added.
    /// </summary>
    public IReadOnlyList<(string Name, Func<string?> Check)> Checks => _checks;

    /// <summary>
    /// Adds a check.
    /// </summary>
    /// <param name="name">The invariant's name, such as its spec identifier.</param>
    /// <param name="check">The predicate, returning <see langword="null"/> when the invariant holds.</param>
    public void Add(string name, Func<string?> check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);
        _checks.Add((name, check));
    }
}

/// <summary>
/// An invariant which failed after a step.
/// </summary>
/// <param name="Invariant">The invariant's name.</param>
/// <param name="Description">What the check observed.</param>
/// <param name="Step">The step after which the check failed.</param>
/// <param name="Elapsed">The virtual time since the run began.</param>
public sealed record InvariantViolation(string Invariant, string Description, long Step, TimeSpan Elapsed)
{
    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Invariant} failed after step {Step} at {Elapsed.TotalMilliseconds:0.###} ms: {Description}");
}

/// <summary>
/// Fails a simulation whose invariant failed after a step.
/// </summary>
public sealed class InvariantViolationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InvariantViolationException"/> class.
    /// </summary>
    public InvariantViolationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvariantViolationException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public InvariantViolationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvariantViolationException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public InvariantViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvariantViolationException"/> class for <paramref name="violation"/>.
    /// </summary>
    /// <param name="violation">The violation.</param>
    public InvariantViolationException(InvariantViolation violation)
        : base(violation?.ToString())
    {
        Violation = violation;
    }

    /// <summary>
    /// Gets the violation, when the exception reports one.
    /// </summary>
    public InvariantViolation? Violation { get; }
}

/// <summary>
/// Fails a simulation which can make no further progress, or which exceeds its budget of steps or virtual time.
/// </summary>
public sealed class SimulationStalledException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationStalledException"/> class.
    /// </summary>
    public SimulationStalledException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationStalledException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public SimulationStalledException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationStalledException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SimulationStalledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Fails a simulation whose scenario and checks passed but which Clockwork recorded with another outcome than a
/// completed run: a fault, race, deadlock, or bound which Clockwork found and the driver did not.
/// </summary>
/// <param name="kind">The outcome Clockwork recorded.</param>
/// <param name="failureIdentity">Clockwork's identity of the failure, or <see langword="null"/>.</param>
/// <param name="diagnostic">Clockwork's description of the failure, or <see langword="null"/>.</param>
public sealed class SimulationOutcomeException(string kind, string? failureIdentity, string? diagnostic)
    : Exception(string.Create(CultureInfo.InvariantCulture, $"The scenario completed, but Clockwork recorded the run as {kind} ({failureIdentity}): {diagnostic}"))
{
    /// <summary>
    /// Gets the outcome Clockwork recorded.
    /// </summary>
    public string Kind { get; } = kind;

    /// <summary>
    /// Gets Clockwork's identity of the failure, or <see langword="null"/>.
    /// </summary>
    public string? FailureIdentity { get; } = failureIdentity;
}
