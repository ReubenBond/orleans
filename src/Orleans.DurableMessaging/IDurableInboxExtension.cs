using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableMessaging;

/// <summary>
/// Non-generic grain extension for durable inbox message delivery.
/// </summary>
[Alias("IDurableInboxExtension")]
public interface IDurableInboxExtension : IGrainExtension
{
    /// <summary>
    /// Delivers a message to this grain's durable inbox.
    /// </summary>
    /// <param name="envelope">The command envelope with an application identity and ordinal subject.</param>
    /// <param name="cancellationToken">Cancels the caller's wait for delivery.</param>
    /// <remarks>
    /// Direct admission shares the supplied GC-owned array under the immutable-publication contract.
    /// RPC copying and deserialization isolate payload arrays using ordinary Orleans serialization.
    /// Once delivery owns inbox admission, it retains its gate and ownership reservation until
    /// its operation completes. Caller cancellation leaves that operation running to its durable outcome.
    /// The grain owner keeps delivery quiescent during journal deletion and resumes delivery
    /// after the deletion task completes successfully.
    /// </remarks>
    /// <returns>Result indicating delivery/processing status.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="envelope"/> has an unset message ID, an invalid subject, a default sender,
    /// exceeds identity or subject admission limits,
    /// or identifies a receiver other than the grain handling the call.
    /// </exception>
    [Alias("DeliverAsync")]
    ValueTask<DeliveryResult> DeliverAsync(DurableEnvelope envelope, CancellationToken cancellationToken = default);
}
