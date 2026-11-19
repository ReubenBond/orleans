using System;

namespace Orleans.Durable.Diagnostics;

/// <summary>An immutable, payload-free observation of a durable owner or an execution attempt.</summary>
/// <remarks>
/// Identities and causation come from the owning protocol. Sequence numbers order observations within one
/// component, owner, and epoch. A new recovered owner has a new epoch. Committed observations describe acknowledged
/// writes; activity observations describe the current attempt. Delivery is best effort.
/// </remarks>
public sealed record DurableObservation
{
    /// <summary>Gets the grain identity or provider-qualified job shard identity.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets the owning component: task, inbox, outbox, or job.</summary>
    public required string Component { get; init; }

    /// <summary>Gets the transition name.</summary>
    public required string Transition { get; init; }

    /// <summary>Gets whether storage acknowledged the containing write.</summary>
    public bool IsCommitted { get; init; }

    /// <summary>Gets the local observation epoch, renewed on recovery or journal reset.</summary>
    public Guid Epoch { get; internal init; }

    /// <summary>Gets the owner-local sequence, including positions lost to buffering overflow.</summary>
    public long Sequence { get; internal init; }

    /// <summary>Gets the owner-local acknowledged write number, or null for attempt activity.</summary>
    /// <remarks>This number identifies acknowledged writes in this observation epoch. Durable inspection supplies journal positions.</remarks>
    public long? WriteVersion { get; internal init; }

    /// <summary>Gets the time at which the owner recorded the transition, on its subsystem clock.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets the task's canonical hierarchical identity, when applicable.</summary>
    public string? TaskId { get; init; }

    /// <summary>Gets the protocol task identity used to correlate the caller and callee across recovery.</summary>
    public string? WireTaskId { get; init; }

    /// <summary>Gets the initiating or awaited task's canonical identity, when applicable.</summary>
    public string? RelatedTaskId { get; init; }

    /// <summary>Gets the task tree's persisted incarnation, when applicable.</summary>
    public Guid Incarnation { get; init; }

    /// <summary>Gets the persisted message identity, when applicable.</summary>
    public string? MessageId { get; init; }

    /// <summary>Gets the persisted job identity, when applicable.</summary>
    public string? JobId { get; init; }

    /// <summary>Gets the execution attempt identity, when applicable.</summary>
    public string? AttemptId { get; init; }

    /// <summary>Gets the initiating, sending, or receiving grain identity, when available.</summary>
    public string? RelatedGrainId { get; init; }

    /// <summary>Gets the next responsible action's due time, on the Durable Jobs clock, when applicable.</summary>
    public DateTimeOffset? DueTime { get; init; }

    /// <summary>Gets a runtime-defined outcome, record kind, or stall reason.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets the persisted W3C trace parent, when available.</summary>
    public string? TraceParent { get; init; }
}
