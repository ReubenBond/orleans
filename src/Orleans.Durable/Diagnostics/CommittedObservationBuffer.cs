using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.Durable.Diagnostics;

// Confined to its state-machine owner, like journal capture and acknowledgement.
internal sealed class CommittedObservationBuffer(DurableObservationSource source)
{
    private readonly List<DurableObservation> _pending = [];
    private Guid _epoch = Guid.NewGuid();
    private long _sequence;
    private long _writeVersion;
    private long _dropped;
    private long _capturedDropped;
    private int _capturedCount;

    public void Stage(DurableObservation observation)
    {
        if (_pending.Count >= DurableObservationSource.DefaultCapacity)
        {
            ++_dropped;
            return;
        }

        _pending.Add(Bound(observation));
    }

    public void Activity(DurableObservation observation) =>
        source.Publish(Bound(observation) with { Epoch = _epoch, Sequence = ++_sequence, IsCommitted = false });

    public void Capture()
    {
        _capturedCount = _pending.Count;
        _capturedDropped = _dropped;
    }

    public void Acknowledge()
    {
        var version = ++_writeVersion;
        for (var i = 0; i < _capturedCount; ++i)
        {
            source.Publish(_pending[i] with { Epoch = _epoch, Sequence = ++_sequence, IsCommitted = true, WriteVersion = version });
        }

        _pending.RemoveRange(0, _capturedCount);
        source.ReportDropped(_capturedDropped);
        _sequence += _capturedDropped;
        _dropped -= _capturedDropped;
        _capturedCount = 0;
        _capturedDropped = 0;
    }

    public void Reset()
    {
        _pending.Clear();
        _epoch = Guid.NewGuid();
        _sequence = _writeVersion = _dropped = _capturedDropped = 0;
        _capturedCount = 0;
    }

    // Bound retained identity text while preserving a stable correlation key for unusually long protocol identities.
    private static string? BoundIdentity(string? value) => value is { Length: > 512 }
        ? "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        : value;

    private static DurableObservation Bound(DurableObservation value) => value with
    {
        Owner = BoundIdentity(value.Owner)!,
        TaskId = BoundIdentity(value.TaskId),
        WireTaskId = BoundIdentity(value.WireTaskId),
        RelatedTaskId = BoundIdentity(value.RelatedTaskId),
        MessageId = BoundIdentity(value.MessageId),
        JobId = BoundIdentity(value.JobId),
        AttemptId = BoundIdentity(value.AttemptId),
        RelatedGrainId = BoundIdentity(value.RelatedGrainId),
        TraceParent = value.TraceParent is { Length: 55 } parent ? parent : null
    };
}
