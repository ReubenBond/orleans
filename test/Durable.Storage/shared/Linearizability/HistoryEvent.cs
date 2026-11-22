// Ported from Porcupine (https://github.com/anishathalye/porcupine, commit 97cd067), Copyright (c) Anish Athalye,
// under the MIT License in PORCUPINE-LICENSE.md.

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Whether a <see cref="HistoryEvent{TInput, TOutput}"/> is an invocation or a response.
/// </summary>
internal enum HistoryEventKind
{
    /// <summary>An invocation, carrying the operation's input.</summary>
    Call,

    /// <summary>A response, carrying the operation's output.</summary>
    Return,
}

/// <summary>
/// One element of a history recorded as an ordered sequence of invocations and responses without timestamps. A
/// response matches the pending invocation with the same <see cref="Id"/>; an identifier may be reused once its
/// operation has returned.
/// </summary>
internal readonly record struct HistoryEvent<TInput, TOutput>
{
    /// <summary>Gets whether this event is an invocation or a response.</summary>
    public required HistoryEventKind Kind { get; init; }

    /// <summary>Gets the identifier which matches a response to its invocation.</summary>
    public required int Id { get; init; }

    /// <summary>Gets the zero-based client of an invocation; a response's is ignored.</summary>
    public int ClientId { get; init; }

    /// <summary>Gets an invocation's input.</summary>
    public TInput Input { get; init; }

    /// <summary>Gets a response's output.</summary>
    public TOutput Output { get; init; }

    /// <summary>
    /// Gets information shown with the operation in the visualization. A response's metadata replaces its invocation's.
    /// </summary>
    public object? Metadata { get; init; }
}
