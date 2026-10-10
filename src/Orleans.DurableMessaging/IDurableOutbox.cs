using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

/// <summary>
/// Stages opaque durable messages alongside a grain's journaled business state.
/// </summary>
/// <remarks>
/// The journal capture hook establishes a durable self-wakeup before capturing pending state.
/// Dispatch begins after persistence acknowledgement. Messages remain pending until the destination
/// acknowledges durable acceptance, recognizes a duplicate, or reports a terminal delivery outcome.
/// Applications namespace command identities and define ordering in their protocols.
/// </remarks>
public interface IDurableOutbox
{
    /// <summary>
    /// Gets the grain identity which owns this outbox and sends its messages.
    /// </summary>
    GrainId SenderId { get; }

    /// <summary>
    /// Gets the number of pending outbound messages.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets pending messages in unspecified order.
    /// </summary>
    /// <remarks>Values expose stored payload arrays. Keep published command contents unchanged.</remarks>
    IEnumerable<DurableEnvelope> Messages { get; }

    /// <summary>
    /// Synchronously stages an immutable envelope for the grain's next journal write.
    /// </summary>
    /// <param name="envelope">The fully prepared outgoing envelope.</param>
    /// <remarks>
    /// Direct sends share the caller's GC-owned array. Keep its bytes unchanged after publication.
    /// The sender identity must match this outbox's grain. Equivalent repeated identities retain
    /// the original intent; subject, destination, sender, and body must match that intent. Conflicts fail explicitly.
    /// Inbox handlers stage outgoing
    /// messages in their synchronous final block before calling <see cref="IInboxHandlerContext.Complete"/>
    /// or <see cref="IInboxHandlerContext.Fail"/>.
    /// Ordinary callers persist staged messages using their journaled state manager.
    /// An explicit write retry retains pending business changes and messages after a scheduling failure.
    /// </remarks>
    /// <exception cref="ArgumentException">The envelope has an unset identity, invalid subject or destination, or exceeds identity/subject limits.</exception>
    /// <exception cref="InvalidOperationException">
    /// The sender differs from the owning grain, the identity conflicts, or the outbox is unavailable.
    /// </exception>
    void Send(DurableEnvelope envelope);

    /// <summary>
    /// Looks up a pending outbound message.
    /// </summary>
    /// <param name="messageId">The message identifier.</param>
    /// <param name="envelope">The matching stored envelope when found.</param>
    /// <returns>Whether the message is pending.</returns>
    bool TryGetMessage(HierarchicalKey messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope);
}
