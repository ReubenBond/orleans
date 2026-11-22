// Ported from Porcupine (https://github.com/anishathalye/porcupine, commit 97cd067), Copyright (c) Anish Athalye,
// under the MIT License in PORCUPINE-LICENSE.md.

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// One operation of a history: a client's invocation with its input and the response with its output, timestamped on
/// one clock.
/// </summary>
/// <remarks>
/// <para>
/// The interval <c>[Call, Return]</c> is closed, so operations whose intervals share an endpoint are concurrent.
/// Porcupine's <c>model.go</c> gives the reason: with a monotonic clock, "successive calls to the clock return values
/// that are always greater than <i>or equal to</i> previously returned values", and half-open intervals would order
/// operations which were in fact concurrent.
/// </para>
/// <para>
/// An operation whose outcome is unknown has <see cref="HistoryOperation.Unbounded"/> as its response time: it is
/// concurrent with every operation invoked after it, so it may take effect at any later point or at none.
/// </para>
/// </remarks>
/// <param name="ClientId">The zero-based client which invoked the operation; the visualization draws one row per client.</param>
/// <param name="Input">The operation and its arguments.</param>
/// <param name="Call">The invocation timestamp.</param>
/// <param name="Output">The response.</param>
/// <param name="Return">The response timestamp, at least <paramref name="Call"/>.</param>
/// <param name="Metadata">Information shown with the operation in the visualization; the check ignores it.</param>
internal readonly record struct HistoryOperation<TInput, TOutput>(
    int ClientId,
    TInput Input,
    long Call,
    TOutput Output,
    long Return,
    object? Metadata = null);

/// <summary>
/// Constants of <see cref="HistoryOperation{TInput, TOutput}"/>.
/// </summary>
internal static class HistoryOperation
{
    /// <summary>
    /// The response time of an operation whose outcome is unknown, such as an ambiguous write.
    /// </summary>
    public const long Unbounded = long.MaxValue;
}
