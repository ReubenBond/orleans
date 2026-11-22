// Copyright (c) .NET Foundation and Contributors.
// Licensed under the MIT license.
// Adapted from Orleans' AdmissionGate, dotnet/orleans src/Orleans.Core/Async/AdmissionGate.cs at commit
// 8c4887aba920b9250ad2914a039777a43781603f.

namespace Orleans.Durable.Storage.Engine.Threading;

/// <summary>
/// Admits concurrent operations until permanently closed, then asynchronously waits for admitted operations to finish.
/// </summary>
/// <remarks>
/// All gate methods support concurrent callers. Store each <see cref="TryEnter"/> result in a <c>using</c>
/// local and check <see cref="Admission.Entered"/> before starting work. Each admitted token has a single
/// owner which disposes it exactly once. For transferred ownership, pair each successful
/// <see cref="TryEnterUnscoped"/> with exactly one <see cref="Exit"/> call.
/// The gate supports up to <see cref="int.MaxValue"/> outstanding entry attempts.
/// </remarks>
public sealed class AdmissionGate
{
    private const int Closed = int.MinValue;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The sign bit closes admission; the remaining bits count entry attempts until rejected or completed.
    // Updating both in one atomic state orders every admission against closure.
    private int _state;

    /// <summary>
    /// Gets a value indicating whether <see cref="CloseAsync"/> has been called. Once it is <see langword="true"/>, it stays so and
    /// every later entry attempt is rejected.
    /// </summary>
    /// <remarks>
    /// A Clog addition: an owner which closes the gate under a lock of its own reads this under the same lock to
    /// order its own state against closure.
    /// </remarks>
    public bool IsClosed => Volatile.Read(ref _state) < 0;

    /// <summary>
    /// Attempts to admit an operation, ordered atomically against <see cref="CloseAsync"/>.
    /// </summary>
    /// <returns>
    /// A token whose <see cref="Admission.Entered"/> property indicates whether the operation was admitted.
    /// Dispose the token when the operation finishes, preferably with a <c>using</c> declaration.
    /// </returns>
    public Admission TryEnter() => TryEnterUnscoped() ? new(this) : default;

    /// <summary>
    /// Attempts to admit an operation whose owner will explicitly call <see cref="Exit"/> when it finishes.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when admitted, ordered atomically against <see cref="CloseAsync"/>.
    /// Each successful admission must be paired with exactly one <see cref="Exit"/>, including exception and cancellation paths.
    /// A rejected attempt owns no admission.
    /// </returns>
    public bool TryEnterUnscoped()
    {
        // Keep new rejected attempts from prolonging the drain after closure.
        if (Volatile.Read(ref _state) < 0)
        {
            return false;
        }

        if (Interlocked.Increment(ref _state) > 0)
        {
            return true;
        }

        // A rejected attempt still owns a count and may be the last one to release it.
        Exit();
        return false;
    }

    /// <summary>
    /// Releases one successful admission. Its owner calls this exactly once, after the admitted operation finishes.
    /// </summary>
    public void Exit()
    {
        if (Interlocked.Decrement(ref _state) == Closed)
        {
            _ = _drained.TrySetResult();
        }
    }

    /// <summary>
    /// Permanently closes admission before returning, and completes the returned task once all admitted operations have exited.
    /// </summary>
    /// <returns>
    /// The shared drain task, also returned by subsequent calls. Pending continuations run asynchronously.
    /// </returns>
    public Task CloseAsync()
    {
        if (Interlocked.Or(ref _state, Closed) == 0)
        {
            _ = _drained.TrySetResult();
        }

        return _drained.Task;
    }

    /// <summary>
    /// Represents scoped ownership of an admission. The default token represents a rejected entry and is safe to dispose.
    /// </summary>
    /// <remarks>
    /// Keep the token in a single <c>using</c> local. Dispose an admitted token exactly once across all copies.
    /// </remarks>
    public readonly struct Admission : IDisposable
    {
        private readonly AdmissionGate? _gate;

        internal Admission(AdmissionGate gate) => _gate = gate;

        /// <summary>
        /// Gets a value indicating whether the operation was admitted.
        /// </summary>
        public bool Entered => _gate is not null;

        /// <summary>
        /// Releases this token's admission when its operation finishes.
        /// </summary>
        public void Dispose() => _gate?.Exit();
    }
}
