namespace Orleans.Durable.Storage.Engine.Buffers;

/// <summary>
/// Observes the pages rented within an execution context, so a test can establish that every page it rented returned
/// when its last owner released it (BUF-I1, BUF-I2).
/// </summary>
/// <remarks>
/// <para>
/// A page is attributed to the accounting which is <see cref="Current"/> in the execution context that rents it from
/// the pool or wraps it around an array. When its last pin is released, on whatever thread, the page reports its return
/// to that accounting. Work started from that context inherits the accounting.
/// </para>
/// <para>
/// Only tests create an accounting, so until one is created, renting a page reads one static flag.
/// </para>
/// </remarks>
internal abstract class ArcBufferPageAccounting
{
    private static readonly AsyncLocal<ArcBufferPageAccounting?> CurrentAccounting = new();
    private static bool s_accountingCreated;

    protected ArcBufferPageAccounting() => MarkCreated();

    /// <summary>
    /// Gets or sets the accounting which pages rented in the current execution context are attributed to.
    /// </summary>
    internal static ArcBufferPageAccounting? Current
    {
        get => s_accountingCreated ? CurrentAccounting.Value : null;
        set => CurrentAccounting.Value = value;
    }

    /// <summary>
    /// Records that <paramref name="page"/> left the pool, or wrapped an array, under this accounting.
    /// </summary>
    internal abstract void OnRented(ArcBufferPage page);

    /// <summary>
    /// Records that <paramref name="page"/>, rented under this accounting, returned.
    /// </summary>
    internal abstract void OnReturned(ArcBufferPage page);

    // The flag only ever turns on, and a plain write keeps it free of the shims Clockwork substitutes for Volatile and
    // Interlocked, which throw outside a simulation.
    private static void MarkCreated() => s_accountingCreated = true;
}
