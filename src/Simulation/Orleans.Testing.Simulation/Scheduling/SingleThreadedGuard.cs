using System.Diagnostics;
using System.Globalization;

namespace Orleans.Testing.Simulation;

/// <summary>
/// A debug guard that detects accidental concurrent access in single-threaded code.
/// Unlike a real lock, this throws immediately if concurrent access is detected
/// rather than blocking. Allows reentrant access by the same thread.
/// Use this for simulation code that must be single-threaded.
/// </summary>
/// <remarks>
/// <para>
/// By default the guard's notion of "who is inside" is the physical
/// <see cref="Environment.CurrentManagedThreadId"/>. When the simulation is driven by the
/// simulation scheduler (see <c>SimulationScheduler</c>), one logical simulation
/// thread is carried by several physical threads over its lifetime - the controlling thread plus a
/// dedicated thread per operation - even though the permission baton guarantees only one of them
/// ever executes at a time. Supplying a <paramref name="logicalOwnerProvider"/> lets the guard key
/// on that single logical owner instead, so a legitimate baton handoff is reentrant while genuinely
/// escaped async work (running on an unrelated thread) is still detected. When no provider is
/// supplied the guard behaves exactly as it always has.
/// </para>
/// </remarks>
/// <param name="logicalOwnerProvider">
/// An optional delegate returning a stable identifier for the current logical owner. When
/// <see langword="null"/> (the default), the physical managed thread id is used.
/// </param>
public sealed class SingleThreadedGuard(Func<int>? logicalOwnerProvider = null)
{
    private readonly Func<int>? _logicalOwnerProvider = logicalOwnerProvider;
    private int _ownerThreadId;
    private int _entryCount;
    private string? _ownerStackTrace;

    /// <summary>
    /// Enters the guarded section. Throws if another thread is already inside.
    /// Allows reentrant access by the same thread.
    /// </summary>
    /// <returns>A disposable scope that exits the guard when disposed.</returns>
    public Scope Enter()
    {
        var currentThreadId = _logicalOwnerProvider?.Invoke() ?? Environment.CurrentManagedThreadId;
        var existingOwner = Interlocked.CompareExchange(ref _ownerThreadId, currentThreadId, 0);

        if (existingOwner != 0 && existingOwner != currentThreadId)
        {
            var ownerStack = _ownerStackTrace ?? "(unknown)";
            throw new InvalidOperationException(
                "Concurrent access detected in single-threaded simulation code. " +
                string.Create(CultureInfo.InvariantCulture, $"Thread {currentThreadId} attempted to enter while thread {existingOwner} is inside. ") +
                "This indicates a bug - simulation code must not be accessed concurrently.\n" +
                $"Owner thread stack trace:\n{ownerStack}");
        }

        if (_entryCount == 0)
        {
            _ownerStackTrace = new StackTrace(fNeedFileInfo: true).ToString();
        }

        Interlocked.Increment(ref _entryCount);
        return new Scope(this);
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref _entryCount) == 0)
        {
            Interlocked.Exchange(ref _ownerThreadId, 0);
        }
    }

    /// <summary>
    /// A disposable scope that exits the guard when disposed.
    /// </summary>
    /// <remarks>
    /// This struct is only used as a disposable scope and is not intended for comparison.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "Scope is intentionally public for using pattern")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Struct is only used as disposable scope, not for comparison")]
    public readonly struct Scope(SingleThreadedGuard guard) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => guard.Exit();
    }
}
