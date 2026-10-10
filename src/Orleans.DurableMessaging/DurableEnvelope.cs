using System;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

/// <summary>
/// Carries independently allocated application bytes between durable grain inboxes and outboxes.
/// </summary>
/// <remarks>
/// The application-supplied message identifier is the deduplication key within the receiving inbox.
/// Applications define their payload format, dispatch, and reply routing. Message identities are exact ordinal keys,
/// scoped to the receiving inbox across immediate senders and subjects. Admission permits up to 1,024 UTF-8
/// bytes and 32 segments per canonical identity, and 256 UTF-8 bytes per nonempty subject.
/// Payload arrays are garbage-collected. Struct assignment shares the array; Orleans deep copying and
/// deserialization produce independent arrays. Applications keep admitted command bytes stable.
/// </remarks>
[GenerateSerializer, Alias("Orleans.DurableMessaging.DurableEnvelope")]
public readonly struct DurableEnvelope
{
    /// <summary>
    /// Gets the application-defined command identity, preserved across retries and resubmissions.
    /// </summary>
    [Id(0)]
    public required HierarchicalKey MessageId { get; init; }

    /// <summary>
    /// Gets the nondefault sending grain identity, which records the immediate sender for provenance.
    /// </summary>
    [Id(1)]
    public required GrainId SenderId { get; init; }

    /// <summary>
    /// Gets the destination grain identity.
    /// </summary>
    [Id(2)]
    public required GrainId ReceiverId { get; init; }

    /// <summary>
    /// Gets the application payload bytes, decoded by the receiving handler.
    /// </summary>
    /// <remarks>
    /// Non-null empty payloads are valid. Application decoding takes place during handler preparation,
    /// before shared mutations. Create the payload locally before staging the envelope.
    /// </remarks>
    [Id(3)]
    public required byte[] Payload { get; init; }

    /// <summary>Gets the exact ordinal application protocol subject.</summary>
    /// <remarks>Applications select decoding and handling using this nonempty subject and keep it stable for a command identity.</remarks>
    [Id(4)]
    public required string Subject { get; init; }

}
