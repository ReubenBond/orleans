using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// A node's answer to <see cref="ILedgerReplicaTransport.HeartbeatAsync"/> (ledger-replicas.md §2.2, §4,
/// conventions.md §15): a <see cref="ReplicaHeartbeatReply"/> or <see cref="ReplicaWriteOutcome.Backpressure"/>.
/// </summary>
[Union]
public readonly struct ReplicaHeartbeatRead : IUnion, IEquatable<ReplicaHeartbeatRead>
{
    private readonly ReplicaHeartbeatReply _reply;
    private readonly Kind _kind;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaHeartbeatRead"/> struct holding a heartbeat reply.
    /// </summary>
    public ReplicaHeartbeatRead(ReplicaHeartbeatReply value)
    {
        _reply = value;
        _kind = Kind.Reply;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaHeartbeatRead"/> struct holding
    /// <see cref="ReplicaWriteOutcome.Backpressure"/>.
    /// </summary>
    public ReplicaHeartbeatRead(ReplicaWriteOutcome.Backpressure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _kind = Kind.Backpressure;
    }

    private enum Kind : byte
    {
        None,
        Reply,
        Backpressure,
    }

    /// <inheritdoc/>
    public object? Value => _kind switch
    {
        Kind.Reply => _reply,
        Kind.Backpressure => ReplicaWriteOutcome.Backpressure.Instance,
        Kind.None => null,
        _ => throw new UnreachableException(),
    };

    /// <summary>
    /// Gets a value indicating whether the answer holds a case.
    /// </summary>
    public bool HasValue => _kind != Kind.None;

    public static bool operator ==(ReplicaHeartbeatRead left, ReplicaHeartbeatRead right) => left.Equals(right);

    public static bool operator !=(ReplicaHeartbeatRead left, ReplicaHeartbeatRead right) => !left.Equals(right);

    /// <summary>
    /// Returns the heartbeat reply, when the answer holds one.
    /// </summary>
    public bool TryGetValue(out ReplicaHeartbeatReply value)
    {
        value = _reply;
        return _kind == Kind.Reply;
    }

    /// <summary>
    /// Returns <see cref="ReplicaWriteOutcome.Backpressure"/>, when the answer holds it.
    /// </summary>
    public bool TryGetValue([NotNullWhen(true)] out ReplicaWriteOutcome.Backpressure? value)
    {
        value = _kind == Kind.Backpressure ? ReplicaWriteOutcome.Backpressure.Instance : null;
        return value is not null;
    }

    /// <inheritdoc/>
    public bool Equals(ReplicaHeartbeatRead other) =>
        _kind == other._kind && (_kind != Kind.Reply || _reply.LeaseGrant == other._reply.LeaseGrant && _reply.Outcomes.Span.SequenceEqual(other._reply.Outcomes.Span));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ReplicaHeartbeatRead other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_kind);
        if (_kind == Kind.Reply)
        {
            hash.Add(_reply.LeaseGrant);
            foreach (var status in _reply.Outcomes.Span)
            {
                hash.Add(status);
            }
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
