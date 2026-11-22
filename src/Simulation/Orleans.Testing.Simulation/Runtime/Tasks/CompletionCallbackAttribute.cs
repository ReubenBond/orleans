namespace Orleans.Testing.Simulation.Runtime.Tasks;

/// <summary>
/// Marks a delegate parameter whose argument runs only through <see cref="SimulationCompletion"/>. Clockwork's analyzer
/// lets a lambda passed to such a parameter call a completion member (see <see cref="SimulationCompletion"/>), and
/// rejects a call anywhere else in the Clockwork assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class CompletionCallbackAttribute : Attribute;
