// Ported from Porcupine (https://github.com/anishathalye/porcupine, commit 97cd067), Copyright (c) Anish Athalye,
// under the MIT License in PORCUPINE-LICENSE.md.

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The result of <see cref="LinearizabilityChecker.CheckVerbose{TState, TInput, TOutput}(LinearizabilityModel{TState, TInput, TOutput}, IReadOnlyList{HistoryOperation{TInput, TOutput}}, CancellationToken)"/>:
/// the verdict, each partition's operations and (partial) linearizations, and annotations for the visualization.
/// </summary>
internal sealed class LinearizabilityAnalysis<TState, TInput, TOutput>
    where TInput : notnull
{
    private readonly List<LinearizabilityAnnotation> _annotations = [];

    internal LinearizabilityAnalysis(
        LinearizabilityModel<TState, TInput, TOutput> model,
        LinearizabilityVerdict verdict,
        IReadOnlyList<LinearizabilityPartition<TInput, TOutput>> partitions)
    {
        Model = model;
        Verdict = verdict;
        Partitions = partitions;
    }

    /// <summary>Gets the model the history was checked against.</summary>
    public LinearizabilityModel<TState, TInput, TOutput> Model { get; }

    /// <summary>Gets the verdict over every partition.</summary>
    public LinearizabilityVerdict Verdict { get; }

    /// <summary>Gets the partitions in the order of their first operation in the history.</summary>
    public IReadOnlyList<LinearizabilityPartition<TInput, TOutput>> Partitions { get; }

    /// <summary>Gets the annotations the visualization draws beside the history.</summary>
    public IReadOnlyList<LinearizabilityAnnotation> Annotations => _annotations;

    /// <summary>
    /// Adds annotations, such as injected faults or server events, to the visualization.
    /// </summary>
    /// <param name="annotations">The annotations.</param>
    public void AddAnnotations(IEnumerable<LinearizabilityAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        _annotations.AddRange(annotations);
    }
}

/// <summary>
/// One partition of a checked history.
/// </summary>
/// <param name="Verdict">The partition's verdict.</param>
/// <param name="Operations">The partition's operations in history order; an operation's index is its identifier.</param>
/// <param name="PartialLinearizations">
/// The longest linearizable prefixes the search found, as operation identifiers in linearization order, longest first.
/// An <see cref="LinearizabilityVerdict.Ok"/> partition has one, a complete linearization. Otherwise each operation which
/// some prefix linearizes appears in the longest prefix found which includes it, and an operation in none was never
/// linearized.
/// </param>
internal sealed record LinearizabilityPartition<TInput, TOutput>(
    LinearizabilityVerdict Verdict,
    IReadOnlyList<HistoryOperation<TInput, TOutput>> Operations,
    IReadOnlyList<IReadOnlyList<int>> PartialLinearizations);

/// <summary>
/// A note drawn beside a history in the visualization: on a client's row, or on a row of its own named by
/// <see cref="Tag"/>, such as a server or the test harness.
/// </summary>
/// <param name="Start">The timestamp at which the annotation starts, on the history's clock.</param>
/// <param name="Description">The text drawn in the annotation.</param>
internal sealed record LinearizabilityAnnotation(long Start, string Description)
{
    /// <summary>Gets the client whose row the annotation is drawn on when it has no <see cref="Tag"/>.</summary>
    public int ClientId { get; init; }

    /// <summary>Gets the name of the row the annotation is drawn on, or <see langword="null"/> for a client's row.</summary>
    public string? Tag { get; init; }

    /// <summary>Gets the timestamp at which the annotation ends; a point in time when omitted or before the start.</summary>
    public long? End { get; init; }

    /// <summary>Gets the tooltip text.</summary>
    public string? Details { get; init; }

    /// <summary>Gets the CSS text color, such as <c>#ff0000</c>.</summary>
    public string? TextColor { get; init; }

    /// <summary>Gets the CSS background color, such as <c>#efaefc</c>.</summary>
    public string? BackgroundColor { get; init; }
}
