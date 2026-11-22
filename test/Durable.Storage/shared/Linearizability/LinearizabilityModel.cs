// Ported from Porcupine (https://github.com/anishathalye/porcupine, commit 97cd067), Copyright (c) Anish Athalye,
// under the MIT License in PORCUPINE-LICENSE.md.

using System.Globalization;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A sequential specification of an object, against which <see cref="LinearizabilityChecker"/> judges histories.
/// </summary>
/// <remarks>
/// <para>
/// A model is purely functional: <see cref="Step"/> returns a new state and never mutates its arguments, so the checker
/// can keep earlier states for backtracking and caching. States which <see cref="StateComparer"/> equates must be
/// interchangeable for every later step.
/// </para>
/// <para>
/// A model whose operations act on independent objects, such as journals with distinct keys, declares
/// <see cref="PartitionComparer"/>. A history is linearizable iff each partition is (P-compositionality, Horn and
/// Kroening, FORTE 2015), so the checker searches each partition alone and <see cref="Init"/> and <see cref="Step"/>
/// describe one partition's object.
/// </para>
/// </remarks>
internal abstract class LinearizabilityModel<TState, TInput, TOutput>
    where TInput : notnull
{
    /// <summary>
    /// Gets the comparer which equates the inputs of one partition, or <see langword="null"/> to check the history as one
    /// partition.
    /// </summary>
    public virtual IEqualityComparer<TInput>? PartitionComparer => null;

    /// <summary>
    /// Gets the equality of states; equal states must have equal hash codes.
    /// </summary>
    public virtual IEqualityComparer<TState> StateComparer => EqualityComparer<TState>.Default;

    /// <summary>
    /// Returns the object's initial state.
    /// </summary>
    /// <returns>The initial state.</returns>
    public abstract TState Init();

    /// <summary>
    /// Decides whether the object, in <paramref name="state"/>, can perform <paramref name="input"/> and respond with
    /// <paramref name="output"/>.
    /// </summary>
    /// <param name="state">The state before the operation, which the step must not mutate.</param>
    /// <param name="input">The operation and its arguments.</param>
    /// <param name="output">The observed response.</param>
    /// <param name="next">The state after the operation, when it is allowed.</param>
    /// <returns>Whether the specification allows the operation with this response.</returns>
    public abstract bool Step(TState state, TInput input, TOutput output, out TState next);

    /// <summary>
    /// Describes an operation for the visualization, such as <c>get('x') -> 'y'</c>.
    /// </summary>
    /// <param name="input">The operation and its arguments.</param>
    /// <param name="output">The response.</param>
    /// <returns>The description.</returns>
    public virtual string DescribeOperation(TInput input, TOutput output) =>
        string.Create(CultureInfo.InvariantCulture, $"{input} -> {output}");

    /// <summary>
    /// Describes a state for the visualization.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The description.</returns>
    public virtual string DescribeState(TState state) => Convert.ToString(state, CultureInfo.InvariantCulture) ?? "null";

    /// <summary>
    /// Describes an operation's metadata for the visualization.
    /// </summary>
    /// <param name="metadata">The metadata, or <see langword="null"/>.</param>
    /// <returns>The description, empty for no metadata.</returns>
    public virtual string DescribeMetadata(object? metadata) => Convert.ToString(metadata, CultureInfo.InvariantCulture) ?? string.Empty;
}
