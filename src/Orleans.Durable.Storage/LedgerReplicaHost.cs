using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;

namespace Orleans.Durable.Storage;

/// <summary>
/// The host side of <see cref="ILedgerStoreGrain"/> on a silo (orleans.md §5.12): it runs each replica call which enters
/// through the grain on the silo's node's LedgerStore, exactly as called, once the node's serving gate admits it (OR-I2,
/// OR-I14), within the host's bounds (OR-I17).
/// </summary>
/// <remarks>
/// <para>
/// A call is checked in order, each check before the store, so each refusal has no effect: its protocol version, refused
/// <see cref="ReplicaRefusal.UnsupportedVersion"/> (OR-I15); the node's serving gate (<see cref="LocalNode.ServingAsync"/>),
/// which answers <see cref="NotThisNode"/> for another node's key, waits for the registration, and refuses
/// <see cref="ReplicaRefusal.Stopping"/> once the node stops; a payload or reply above the payload limit, refused
/// <see cref="ReplicaRefusal.Argument"/>; and admission, answered <c>Backpressure</c> beyond
/// <see cref="ReplicaCallLimits.MaxReplicaCalls"/> calls or <see cref="ReplicaCallLimits.MaxReplicaCallBytes"/> bytes, a
/// read reserving the reply its caller reserved. The store's own argument refusals and its corruption answer as values too,
/// so every exception the host lets escape is an unknown outcome to its caller.
/// </para>
/// <para>
/// The caller's cancellation ends only the wait at the serving gate: the store operation runs to its end, so an admission
/// lasts as long as the store holds the call's bytes. A write or a contiguous-tail read holds its admission until its store
/// operation ends; a read whose batch fits its reservation holds it until Orleans first converts its DATA reply to its
/// surrogate (<see cref="RemoteReplicaReadConverter"/>), which Orleans' copy of the response does as the grain method
/// returns, or, never converted, until the call's deadline passes. Both a write's payload and a read's DATA cross in a
/// zero-copy consume-on-serialize <see cref="WireArcBuffer"/> directly over Durable Storage's pooled
/// <see cref="ArcBuffer"/> (OR-I18).
/// </para>
/// </remarks>
internal sealed class LedgerReplicaHost : IDisposable
{
    private static readonly ReplicaCallRefused StoppingRefusal = new(ReplicaRefusal.Stopping, "The callee's node is stopping and admits no new call.");
    private static readonly ReplicaCallRefused DisklessRefusal = new(ReplicaRefusal.Argument, "The callee's node is diskless and holds no LedgerStore.");

    private readonly LocalNode _local;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Queue<WireArcBuffer> _pendingUnconsumed = new();
    private int _calls;
    private long _bytes;

    /// <summary>Initializes a new instance of the <see cref="LedgerReplicaHost"/> class.</summary>
    public LedgerReplicaHost(LocalNode local, ReplicaCallLimits limits, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _local = local;
        MaxCalls = limits.MaxReplicaCalls;
        MaxBytes = limits.MaxReplicaCallBytes;
        PayloadLimit = limits.PayloadLimit;
        ReplyDeadline = limits.Deadline;
        _timeProvider = timeProvider;
    }

    /// <summary>Gets the most calls the host admits at once.</summary>
    public int MaxCalls { get; }

    /// <summary>Gets the payload bytes the host holds at most.</summary>
    public long MaxBytes { get; }

    /// <summary>Gets the payload limit: the longest payload a call carries, and the longest reply a read reserves.</summary>
    public int PayloadLimit { get; }

    /// <summary>Gets how long a read's reply holds its admission at most, a call's deadline.</summary>
    public TimeSpan ReplyDeadline { get; }

    /// <summary>Gets the calls the host holds admitted.</summary>
    public int Calls
    {
        get
        {
            lock (_lock)
            {
                return _calls;
            }
        }
    }

    /// <summary>Gets the payload bytes the host holds admitted, reads' reserved replies included.</summary>
    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Runs a write on the replica it names, as the grain keyed by <paramref name="key"/> received it.</summary>
    /// <remarks>The host owns the request's payload, and releases it on every path on which it receives it (OR-I18).</remarks>
    public Task<RemoteReplicaWriteOutcome> WriteAsync(NodeId key, LedgerReplicaWriteRequest request, CancellationToken cancellationToken) =>
        WriteCoreAsync(grain: null, scheduler: null, key, request, cancellationToken);

    /// <summary>Runs a write called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal Task<RemoteReplicaWriteOutcome> WriteFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, LedgerReplicaWriteRequest request, CancellationToken cancellationToken) =>
        WriteCoreAsync(grain, scheduler, key, request, cancellationToken);

    private async Task<RemoteReplicaWriteOutcome> WriteCoreAsync(IGrainBase? grain, TaskScheduler? scheduler, NodeId key, LedgerReplicaWriteRequest request, CancellationToken cancellationToken)
    {
        var received = request.Payload ?? WireArcBuffer.Empty;
        try
        {
            if (scheduler is not null)
            {
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }

            if (Unsupported(request.Version) is { } unsupported)
            {
                return unsupported;
            }

            var serving = await _local.ServingAsync(key, cancellationToken).ConfigureAwait(false);
            if (serving.Value is NotThisNode)
            {
                if (grain is not null)
                {
                    await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler!).ConfigureAwait(false);
                }

                return NotThisNode.Instance;
            }

            if (serving.Value is LocalNodeServing.Diskless)
            {
                return DisklessRefusal;
            }

            if (serving.Value is not LedgerStore store)
            {
                return StoppingRefusal;
            }

            var length = received.Length;
            if (AbovePayloadLimit(length, "payload") is { } above)
            {
                return above;
            }

            if (!TryAdmitDirect(length))
            {
                return ReplicaWriteOutcome.Backpressure.Instance;
            }

            try
            {
                using var payload = received.TakeBuffer();
                var outcome = await store.WriteReplicaAsync(
                    new LedgerId(request.Ledger),
                    request.WindowSize,
                    new Slot(request.Slot),
                    payload,
                    new Slot(request.ContiguousTail),
                    request.Recovery,
                    CancellationToken.None).ConfigureAwait(false);
                Debug.Assert(outcome.Value is not null, "A replica answers with a case.");
                Debug.Assert(!request.Recovery || outcome.Value is not ReplicaWriteOutcome.Sealed, "A recovery write never answers Sealed (OR-I15's reply check).");
                return outcome switch
                {
                    ReplicaWriteOutcome.Written written => written,
                    ReplicaWriteOutcome.Sealed @sealed => @sealed,
                    ReplicaWriteOutcome.Dropped dropped => dropped,
                    ReplicaWriteOutcome.Backpressure backpressure => backpressure,
                    null => throw new UnreachableException(),
                };
            }
            catch (ArgumentException refused)
            {
                return Refused(refused);
            }
            catch (LedgerReplicaCorruptException corrupt)
            {
                return Corrupt(corrupt);
            }
            finally
            {
                Return(length);
            }
        }
        finally
        {
            received.Dispose();
        }
    }

    /// <summary>Runs a read, whole or ranged, on the replica it names, as the grain keyed by <paramref name="key"/> received it.</summary>
    public Task<RemoteReplicaRead> ReadAsync(NodeId key, LedgerReplicaReadRequest request, CancellationToken cancellationToken) =>
        ReadCoreAsync(grain: null, scheduler: null, key, request, cancellationToken);

    /// <summary>Runs a read called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal Task<RemoteReplicaRead> ReadFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, LedgerReplicaReadRequest request, CancellationToken cancellationToken) =>
        ReadCoreAsync(grain, scheduler, key, request, cancellationToken);

    private async Task<RemoteReplicaRead> ReadCoreAsync(IGrainBase? grain, TaskScheduler? scheduler, NodeId key, LedgerReplicaReadRequest request, CancellationToken cancellationToken)
    {
        if (scheduler is not null)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        if (Unsupported(request.Version) is { } unsupported)
        {
            return unsupported;
        }

        var serving = await _local.ServingAsync(key, cancellationToken).ConfigureAwait(false);
        if (serving.Value is NotThisNode)
        {
            if (grain is not null)
            {
                await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler!).ConfigureAwait(false);
            }

            return NotThisNode.Instance;
        }

        if (serving.Value is LocalNodeServing.Diskless)
        {
            return DisklessRefusal;
        }

        if (serving.Value is not LedgerStore store)
        {
            return StoppingRefusal;
        }

        if (request.Length < 0 || request.MaxReplyBytes <= 0 || request.IsRange && request.MaxReplyBytes != request.Length)
        {
            return new ReplicaCallRefused(
                ReplicaRefusal.Argument,
                string.Create(CultureInfo.InvariantCulture, $"A read reserves a positive reply, a ranged read its length: not {request.MaxReplyBytes} bytes for a length of {request.Length}."));
        }

        if (AbovePayloadLimit(request.MaxReplyBytes, "reply") is { } above)
        {
            return above;
        }

        // A read reserves the reply its caller reserved, so the bytes the host holds stay within its bound.
        LedgerReplicaHostAdmission? admission = null;
        WireArcBuffer? wire = null;
        try
        {
            admission = TryAdmit(request.MaxReplyBytes);
            if (admission is null)
            {
                return ReplicaWriteOutcome.Backpressure.Instance;
            }

            ReplicaRead read;
            try
            {
                var ledger = new LedgerId(request.Ledger);
                var slot = new Slot(request.Slot);
                var tail = new Slot(request.ContiguousTail);
                read = request.IsRange
                    ? await store.ReadReplicaAsync(ledger, request.WindowSize, slot, request.Offset, request.Length, tail, CancellationToken.None).ConfigureAwait(false)
                    : await store.ReadReplicaAsync(ledger, request.WindowSize, slot, tail, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ArgumentException refused)
            {
                return Refused(refused);
            }
            catch (LedgerReplicaCorruptException corrupt)
            {
                return Corrupt(corrupt);
            }

            Debug.Assert(read.HasValue, "A replica answers with a case.");
            if (read.TryGetValue(out ArcBuffer batch))
            {
                if (batch.Length > request.MaxReplyBytes)
                {
                    // The host returns none of a batch longer than the reply reserved; the caller reads again reserving its length.
                    var length = batch.Length;
                    batch.Dispose();
                    return new RemoteReplicaRead.TooLarge(length);
                }

                // Transfer ownership of the store's pooled buffer directly into WireArcBuffer (zero-copy; OR-I18).
                // The admission holds host call/byte bounds until first surrogate conversion, and the deadline timer
                // guards against drop-before-serialization until WireArcBufferCodec or a local caller consumes it.
                wire = TakeReadBuffer(batch, request);
                var data = new RemoteReplicaRead.Data(wire) { Admission = admission };
                admission.HoldUntil(_timeProvider, ReplyDeadline, wire);
                admission = null;
                wire = null;
                return data;
            }

            return read switch
            {
                ReplicaRead.Missing missing => missing,
                ReplicaRead.OutsideBatch outside => outside,
                ReplicaWriteOutcome.Dropped dropped => dropped,
                ReplicaWriteOutcome.Backpressure backpressure => backpressure,
                ArcBuffer => throw new UnreachableException("DATA is answered above."),
                null => throw new UnreachableException(),
            };
        }
        finally
        {
            wire?.Dispose();
            admission?.Dispose();
        }
    }

    /// <summary>Runs a batched range read on the replica it names, as the grain keyed by <paramref name="key"/> received it.</summary>
    public Task<RemoteReplicaRangeRead> ReadRangeAsync(NodeId key, LedgerReplicaReadRangeRequest request, CancellationToken cancellationToken) =>
        ReadRangeCoreAsync(grain: null, scheduler: null, key, request, cancellationToken);

    /// <summary>Runs a batched range read called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal Task<RemoteReplicaRangeRead> ReadRangeFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, LedgerReplicaReadRangeRequest request, CancellationToken cancellationToken) =>
        ReadRangeCoreAsync(grain, scheduler, key, request, cancellationToken);

    private async Task<RemoteReplicaRangeRead> ReadRangeCoreAsync(IGrainBase? grain, TaskScheduler? scheduler, NodeId key, LedgerReplicaReadRangeRequest request, CancellationToken cancellationToken)
    {
        if (scheduler is not null)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        if (Unsupported(request.Version) is { } unsupported)
        {
            return unsupported;
        }

        var serving = await _local.ServingAsync(key, cancellationToken).ConfigureAwait(false);
        if (serving.Value is NotThisNode)
        {
            if (grain is not null)
            {
                await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler!).ConfigureAwait(false);
            }

            return NotThisNode.Instance;
        }

        if (serving.Value is LocalNodeServing.Diskless)
        {
            return DisklessRefusal;
        }

        if (serving.Value is not LedgerStore store)
        {
            return StoppingRefusal;
        }

        if (request.MaxSlots <= 0 || request.MaxBytes <= 0 || request.From < 0 || request.Below < request.From)
        {
            return new ReplicaCallRefused(
                ReplicaRefusal.Argument,
                string.Create(CultureInfo.InvariantCulture, $"Invalid range read [{request.From}, {request.Below}) with maxSlots={request.MaxSlots}, maxBytes={request.MaxBytes}."));
        }

        if (AbovePayloadLimit(request.MaxBytes, "reply") is { } above)
        {
            return above;
        }

        LedgerReplicaHostAdmission? admission = null;
        WireArcBuffer? wire = null;
        try
        {
            admission = TryAdmit(request.MaxBytes);
            if (admission is null)
            {
                return ReplicaWriteOutcome.Backpressure.Instance;
            }

            ReplicaRangeRead read;
            try
            {
                read = await store.ReadReplicaRangeAsync(
                    new LedgerId(request.Ledger),
                    request.WindowSize,
                    new Slot(request.From),
                    new Slot(request.Below),
                    new Slot(request.ContiguousTail),
                    request.MaxSlots,
                    request.MaxBytes,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (ArgumentException refused)
            {
                return Refused(refused);
            }
            catch (LedgerReplicaCorruptException corrupt)
            {
                return Corrupt(corrupt);
            }

            if (read.TryGetValue(out ReplicaRangeBatch batch))
            {
                if (batch.FirstSlotTooLargeLength > 0)
                {
                    batch.Dispose();
                    return new RemoteReplicaRangeRead.TooLarge(batch.FirstSlotTooLargeLength);
                }

                if (batch.Count == 0)
                {
                    batch.Dispose();
                    return new RemoteReplicaRangeRead.Batch(batch.FirstSlot.Value, 0, WireArcBuffer.Empty);
                }

                // Retain the packed pooled buffer before disposing the struct wrapper: zero copy!
                var packed = batch.Packed.Retain();
                var firstSlot = batch.FirstSlot.Value;
                var count = batch.Count;
                batch.Dispose();

                wire = WireArcBuffer.Take(packed);
                var reply = new RemoteReplicaRangeRead.Batch(firstSlot, count, wire) { Admission = admission };
                admission.HoldUntil(_timeProvider, ReplyDeadline, wire);
                admission = null;
                wire = null;
                return reply;
            }

            return read switch
            {
                ReplicaWriteOutcome.Dropped dropped => dropped,
                ReplicaWriteOutcome.Backpressure backpressure => backpressure,
                _ => throw new UnreachableException(),
            };
        }
        finally
        {
            wire?.Dispose();
            admission?.Dispose();
        }
    }

    /// <summary>Runs a batched scattered-slot read on the replica it names, as the grain keyed by <paramref name="key"/> received it.</summary>
    public Task<RemoteReplicaSlotsRead> ReadSlotsAsync(NodeId key, LedgerReplicaReadSlotsRequest request, CancellationToken cancellationToken) =>
        ReadSlotsCoreAsync(grain: null, scheduler: null, key, request, cancellationToken);

    /// <summary>Runs a batched scattered-slot read called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal Task<RemoteReplicaSlotsRead> ReadSlotsFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, LedgerReplicaReadSlotsRequest request, CancellationToken cancellationToken) =>
        ReadSlotsCoreAsync(grain, scheduler, key, request, cancellationToken);

    private async Task<RemoteReplicaSlotsRead> ReadSlotsCoreAsync(IGrainBase? grain, TaskScheduler? scheduler, NodeId key, LedgerReplicaReadSlotsRequest request, CancellationToken cancellationToken)
    {
        if (scheduler is not null)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        if (Unsupported(request.Version) is { } unsupported)
        {
            return unsupported;
        }

        var serving = await _local.ServingAsync(key, cancellationToken).ConfigureAwait(false);
        if (serving.Value is NotThisNode)
        {
            if (grain is not null)
            {
                await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler!).ConfigureAwait(false);
            }

            return NotThisNode.Instance;
        }

        if (serving.Value is LocalNodeServing.Diskless)
        {
            return DisklessRefusal;
        }

        if (serving.Value is not LedgerStore store)
        {
            return StoppingRefusal;
        }

        var rawSlots = request.Slots;
        if (rawSlots is null || request.MaxBytes <= 0)
        {
            return new ReplicaCallRefused(
                ReplicaRefusal.Argument,
                string.Create(CultureInfo.InvariantCulture, $"Invalid slots read with slots={rawSlots?.Length ?? -1}, maxBytes={request.MaxBytes}."));
        }

        if (rawSlots.Length > PayloadLimit / sizeof(long))
        {
            return new ReplicaCallRefused(
                ReplicaRefusal.Argument,
                string.Create(CultureInfo.InvariantCulture, $"A slots read of {rawSlots.Length} slots passes the replica payload limit of {PayloadLimit}."));
        }

        if (AbovePayloadLimit(request.MaxBytes, "reply") is { } above)
        {
            return above;
        }

        var slots = new Slot[rawSlots.Length];
        for (var i = 0; i < rawSlots.Length; i++)
        {
            if (rawSlots[i] < 0)
            {
                return new ReplicaCallRefused(
                    ReplicaRefusal.ArgumentOutOfRange,
                    string.Create(CultureInfo.InvariantCulture, $"Invalid negative slot {rawSlots[i]} in slots read."));
            }

            slots[i] = new Slot(rawSlots[i]);
        }

        LedgerReplicaHostAdmission? admission = null;
        WireArcBuffer? wire = null;
        try
        {
            admission = TryAdmit(request.MaxBytes);
            if (admission is null)
            {
                return ReplicaWriteOutcome.Backpressure.Instance;
            }

            ReplicaSlotsRead read;
            try
            {
                read = await store.ReadReplicaSlotsAsync(
                    new LedgerId(request.Ledger),
                    request.WindowSize,
                    slots,
                    new Slot(request.ContiguousTail),
                    request.MaxBytes,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (ArgumentException refused)
            {
                return Refused(refused);
            }
            catch (LedgerReplicaCorruptException corrupt)
            {
                return Corrupt(corrupt);
            }

            if (read.TryGetValue(out ReplicaSlotsBatch batch))
            {
                if (batch.Count == 0)
                {
                    batch.Dispose();
                    return new RemoteReplicaSlotsRead.Batch(0, WireArcBuffer.Empty);
                }

                // Retain the packed pooled buffer before disposing the struct wrapper: zero copy!
                var packed = batch.Packed.Retain();
                var count = batch.Count;
                batch.Dispose();

                wire = WireArcBuffer.Take(packed);
                var reply = new RemoteReplicaSlotsRead.Batch(count, wire) { Admission = admission };
                admission.HoldUntil(_timeProvider, ReplyDeadline, wire);
                admission = null;
                wire = null;
                return reply;
            }

            return read switch
            {
                ReplicaWriteOutcome.Dropped dropped => dropped,
                ReplicaWriteOutcome.Backpressure backpressure => backpressure,
                _ => throw new UnreachableException(),
            };
        }
        finally
        {
            wire?.Dispose();
            admission?.Dispose();
        }
    }

    /// <summary>Runs a contiguous-tail read or a seal on the replica it names, as the grain keyed by <paramref name="key"/> received it.</summary>
    public Task<RemoteReplicaTailRead> ReadTailAsync(NodeId key, LedgerReplicaTailRequest request, CancellationToken cancellationToken) =>
        ReadTailCoreAsync(grain: null, scheduler: null, key, request, cancellationToken);

    /// <summary>Runs a contiguous-tail read or seal called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal Task<RemoteReplicaTailRead> ReadTailFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, LedgerReplicaTailRequest request, CancellationToken cancellationToken) =>
        ReadTailCoreAsync(grain, scheduler, key, request, cancellationToken);

    private async Task<RemoteReplicaTailRead> ReadTailCoreAsync(IGrainBase? grain, TaskScheduler? scheduler, NodeId key, LedgerReplicaTailRequest request, CancellationToken cancellationToken)
    {
        if (scheduler is not null)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        if (Unsupported(request.Version) is { } unsupported)
        {
            return unsupported;
        }

        var serving = await _local.ServingAsync(key, cancellationToken).ConfigureAwait(false);
        if (serving.Value is NotThisNode)
        {
            if (grain is not null)
            {
                await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler!).ConfigureAwait(false);
            }

            return NotThisNode.Instance;
        }

        if (serving.Value is LocalNodeServing.Diskless)
        {
            return DisklessRefusal;
        }

        if (serving.Value is not LedgerStore store)
        {
            return StoppingRefusal;
        }

        if (!TryAdmitDirect(0))
        {
            return ReplicaWriteOutcome.Backpressure.Instance;
        }

        try
        {
            var answer = await store.ReadReplicaTailAsync(
                new LedgerId(request.Ledger),
                request.WindowSize,
                new Slot(request.ContiguousTail),
                request.Seal,
                CancellationToken.None).ConfigureAwait(false);
            Debug.Assert(answer.HasValue, "A replica answers with a case.");
            Debug.Assert(
                !answer.TryGetValue(out LedgerReplicaTail reported) || reported.ContiguousTail.Value >= request.ContiguousTail,
                "A contiguous tail is never below the one the call carried (OR-I15's reply check).");
            return answer switch
            {
                LedgerReplicaTail tail => new RemoteReplicaTailRead.Tail(tail.ContiguousTail.Value, tail.IsSealed, tail.LeaseRemaining.Ticks),
                ReplicaWriteOutcome.Dropped dropped => dropped,
                ReplicaWriteOutcome.Backpressure backpressure => backpressure,
                null => throw new UnreachableException(),
            };
        }
        catch (ArgumentException refused)
        {
            return Refused(refused);
        }
        catch (LedgerReplicaCorruptException corrupt)
        {
            return Corrupt(corrupt);
        }
        finally
        {
            Return(0);
        }
    }

    /// <summary>Runs a batched read-lease heartbeat on the node's store, as the grain keyed by <paramref name="key"/> received it.</summary>
    public Task<RemoteReplicaHeartbeatRead> HeartbeatAsync(NodeId key, LedgerReplicaHeartbeatRequest request, CancellationToken cancellationToken) =>
        HeartbeatCoreAsync(grain: null, scheduler: null, key, request, cancellationToken);

    /// <summary>Runs a batched read-lease heartbeat called on <paramref name="grain"/> off its activation's <paramref name="scheduler"/>, deactivating it on <see cref="NotThisNode"/> (OR-I14).</summary>
    internal Task<RemoteReplicaHeartbeatRead> HeartbeatFromGrainAsync(IGrainBase grain, NodeId key, TaskScheduler scheduler, LedgerReplicaHeartbeatRequest request, CancellationToken cancellationToken) =>
        HeartbeatCoreAsync(grain, scheduler, key, request, cancellationToken);

    private async Task<RemoteReplicaHeartbeatRead> HeartbeatCoreAsync(IGrainBase? grain, TaskScheduler? scheduler, NodeId key, LedgerReplicaHeartbeatRequest request, CancellationToken cancellationToken)
    {
        if (scheduler is not null)
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        if (Unsupported(request.Version) is { } unsupported)
        {
            return unsupported;
        }

        var serving = await _local.ServingAsync(key, cancellationToken).ConfigureAwait(false);
        if (serving.Value is NotThisNode)
        {
            if (grain is not null)
            {
                await NotThisNode.DeactivateOnSchedulerAsync(grain, scheduler!).ConfigureAwait(false);
            }

            return NotThisNode.Instance;
        }

        if (serving.Value is LocalNodeServing.Diskless)
        {
            return DisklessRefusal;
        }

        if (serving.Value is not LedgerStore store)
        {
            return StoppingRefusal;
        }

        if (request.Ledgers is null || request.Ledgers.Length == 0)
        {
            return new ReplicaCallRefused(ReplicaRefusal.Argument, "A heartbeat names at least one ledger.");
        }

        if (request.Ledgers.Length > PayloadLimit / sizeof(ulong))
        {
            return new ReplicaCallRefused(
                ReplicaRefusal.Argument,
                string.Create(CultureInfo.InvariantCulture, $"A replica call's heartbeat of {request.Ledgers.Length} ledgers passes the payload limit of {PayloadLimit}."));
        }

        if (!TryAdmitDirect(0))
        {
            return ReplicaWriteOutcome.Backpressure.Instance;
        }

        try
        {
            var ids = new LedgerId[request.Ledgers.Length];
            for (var i = 0; i < request.Ledgers.Length; i++)
            {
                ids[i] = new LedgerId(request.Ledgers[i]);
            }

            var reply = await store.HeartbeatReplicasAsync(ids, CancellationToken.None).ConfigureAwait(false);
            var statuses = reply.Outcomes.Span;
            var bytes = new byte[statuses.Length];
            for (var i = 0; i < statuses.Length; i++)
            {
                bytes[i] = (byte)statuses[i];
            }

            return new RemoteReplicaHeartbeatRead.Reply(reply.LeaseGrant.Ticks, bytes);
        }
        catch (ArgumentException refused)
        {
            return Refused(refused);
        }
        finally
        {
            Return(0);
        }
    }

    /// <summary>Admits a call holding <paramref name="bytes"/> without allocating a timed admission handle.</summary>
    private bool TryAdmitDirect(long bytes)
    {
        lock (_lock)
        {
            if (_calls >= MaxCalls || _bytes + bytes > MaxBytes)
            {
                return false;
            }

            _calls++;
            _bytes += bytes;
            return true;
        }
    }

    /// <summary>Admits a call holding <paramref name="bytes"/>, or returns <see langword="null"/> beyond either bound.</summary>
    internal LedgerReplicaHostAdmission? TryAdmit(long bytes) =>
        TryAdmitDirect(bytes) ? new LedgerReplicaHostAdmission(this, bytes) : null;

    /// <summary>Returns <paramref name="bytes"/> and a call to the host's bounds, tracking any still-unconsumed reply payload for shutdown cleanup.</summary>
    internal void Return(long bytes, WireArcBuffer? unconsumedPayload = null)
    {
        lock (_lock)
        {
            _calls--;
            _bytes -= bytes;
            Debug.Assert(_calls >= 0 && _bytes >= 0, "The host returns no more than it admitted.");
            while (_pendingUnconsumed.TryPeek(out var head) && head.IsConsumed)
            {
                _ = _pendingUnconsumed.Dequeue();
            }

            if (unconsumedPayload is not null && !unconsumedPayload.IsConsumed)
            {
                _pendingUnconsumed.Enqueue(unconsumedPayload);
            }
        }
    }

    /// <summary>Releases any reply buffers whose responses were dropped before serialization when the silo stops.</summary>
    public void Dispose()
    {
        WireArcBuffer[]? remaining = null;
        lock (_lock)
        {
            if (_pendingUnconsumed.Count > 0)
            {
                remaining = [.. _pendingUnconsumed];
                _pendingUnconsumed.Clear();
            }
        }

        if (remaining is not null)
        {
            foreach (var wire in remaining)
            {
                wire.Dispose();
            }
        }
    }

    /// <summary>Refuses a call of a protocol version this silo does not speak, before anything else (OR-I15).</summary>
    private static ReplicaCallRefused? Unsupported(int version) =>
        version == LedgerReplicaProtocol.Version
            ? null
            : new ReplicaCallRefused(
                ReplicaRefusal.UnsupportedVersion,
                string.Create(CultureInfo.InvariantCulture, $"The replica call speaks protocol version {version}; this silo speaks version {LedgerReplicaProtocol.Version}."));

    private static ReplicaCallRefused Refused(ArgumentException refused) =>
        new(refused is ArgumentOutOfRangeException ? ReplicaRefusal.ArgumentOutOfRange : ReplicaRefusal.Argument, refused.Message);

    private static ReplicaCorrupt Corrupt(LedgerReplicaCorruptException corrupt) => new(corrupt.Slot?.Value, corrupt.Message);

    /// <summary>Wraps a read's pooled <see cref="ArcBuffer"/> directly in a zero-copy consume-on-serialize <see cref="WireArcBuffer"/> (OR-I18).</summary>
    private static WireArcBuffer TakeReadBuffer(ArcBuffer batch, LedgerReplicaReadRequest request)
    {
        Debug.Assert(batch.Length > 0, "DATA is never empty (OR-I15's reply check).");
        Debug.Assert(!request.IsRange || batch.Length == request.Length, "A ranged read answers the length it asked for (OR-I15's reply check).");
        return WireArcBuffer.Take(batch);
    }

    private ReplicaCallRefused? AbovePayloadLimit(int length, string what) =>
        length > PayloadLimit
            ? new ReplicaCallRefused(ReplicaRefusal.Argument, string.Create(CultureInfo.InvariantCulture, $"A replica call's {what} of {length} bytes passes the payload limit of {PayloadLimit}."))
            : null;
}

/// <summary>
/// One call's admission to a <see cref="LedgerReplicaHost"/>: a call and its bytes, which it returns once, when it is
/// released or disposed: when its store operation ends, when its reply is first converted to its surrogate, or at its
/// reply's deadline (OR-I17).
/// </summary>
internal sealed class LedgerReplicaHostAdmission : IDisposable
{
    private readonly LedgerReplicaHost _host;
    private readonly long _bytes;
    private WireArcBuffer? _payload;
    private ITimer? _deadline;
    private int _released;

    /// <summary>Initializes a new instance of the <see cref="LedgerReplicaHostAdmission"/> class.</summary>
    internal LedgerReplicaHostAdmission(LedgerReplicaHost host, long bytes)
    {
        _host = host;
        _bytes = bytes;
    }

    /// <summary>Gets a value indicating whether the admission was returned.</summary>
    internal bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>
    /// Releases the host's call/byte admission on first surrogate conversion without disposing the reply's
    /// <see cref="WireArcBuffer"/>, transferring any deadline safety timer to the buffer so it cancels immediately when
    /// serialized or locally consumed and still releases pages if dropped before serialization.
    /// </summary>
    internal void ReleaseAdmission()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            var timer = Interlocked.Exchange(ref _deadline, null);
            var payload = _payload;
            if (payload is not null)
            {
                if (timer is not null)
                {
                    payload.AttachDeadlineTimer(timer);
                }
            }
            else
            {
                timer?.Dispose();
            }

            _host.Return(_bytes, payload);
        }
    }

    /// <summary>Returns the call and its bytes to the host and disposes any unconsumed reply payload, once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            Interlocked.Exchange(ref _deadline, null)?.Dispose();
            _payload?.Dispose();
            _host.Return(_bytes);
        }
    }

    /// <summary>Holds the admission past its call, until <see cref="ReleaseAdmission"/>, <see cref="Dispose"/>, or until <paramref name="deadline"/> passes.</summary>
    internal void HoldUntil(TimeProvider timeProvider, TimeSpan deadline, WireArcBuffer? payload = null)
    {
        _payload = payload;
        using (ExecutionContext.SuppressFlow())
        {
            var timer = timeProvider.CreateTimer(static state => ((LedgerReplicaHostAdmission)state!).OnDeadline(), this, deadline, Timeout.InfiniteTimeSpan);
            var previous = Interlocked.Exchange(ref _deadline, timer);
            previous?.Dispose();
            if (Volatile.Read(ref _released) != 0)
            {
                var raced = Interlocked.Exchange(ref _deadline, null);
                if (raced is not null)
                {
                    if (payload is not null)
                    {
                        payload.AttachDeadlineTimer(raced);
                    }
                    else
                    {
                        raced.Dispose();
                    }
                }
            }
        }
    }

    private void OnDeadline()
    {
        _payload?.Dispose();
        Dispose();
    }
}
