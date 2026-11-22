using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Wal;

/// <summary>
/// A record submitted to <see cref="WriteAheadLog.WriteAsync"/>, which receives exactly one durable outcome.
/// </summary>
/// <remarks>
/// The record is <see cref="Header"/> followed by <see cref="Payload"/>; either may be the default value, which is
/// empty. The log holds its own leases on both from acceptance until the outcome callback returns, and the caller
/// keeps their bytes unchanged until then.
/// </remarks>
public abstract class WalWriteRequest
{
    /// <summary>
    /// Gets or sets the bytes which start the record.
    /// </summary>
    public ArcBuffer Header { get; protected set; }

    /// <summary>
    /// Gets or sets the bytes which follow <see cref="Header"/>.
    /// </summary>
    public ArcBuffer Payload { get; protected set; }

    /// <summary>
    /// Reports that every page holding the record is durable.
    /// </summary>
    /// <remarks>
    /// This runs on the completion path with a <see cref="WalDurableRecord"/> proof minted only after durable storage
    /// I/O completes (conventions.md §13). <see cref="WriteAheadLog.DurableFrontier"/> passes the record only after it
    /// returns, so the caller can publish the record here.
    /// </remarks>
    /// <param name="record">The durable record proof carrying the record's position and first-frame page.</param>
    protected internal abstract void OnDurable(WalDurableRecord record);

    /// <summary>
    /// Reports that the record could not be made durable: a local storage operation of the log failed, which stops the
    /// process (conventions.md §14), so only a test which intercepts the stop observes it. The log then refuses every later
    /// write.
    /// </summary>
    /// <param name="error">The failure.</param>
    protected internal abstract void OnFailed(Exception error);
}
