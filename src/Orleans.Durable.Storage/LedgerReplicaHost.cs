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
/// returns, or, never converted, until the call's deadline passes. The host owns a write's payload, which Orleans decoded
/// or, on a call within one silo, its proxy pinned: it moves the payload into Durable Storage's page pool for the store and releases
/// it on every path. A read's DATA is copied out of the store's buffer, which the host releases, into an Orleans buffer
/// the reply carries, which the host never releases: across silos Orleans' codec writes it without taking ownership and
/// may write it after the grain method returns, so the GC reclaims its pages; on a call within one silo the reply reaches
/// the caller as it is, and the caller is its one releaser (OR-I18).
/// </para>
/// </remarks>
internal sealed class LedgerReplicaHost
{
    private static readonly ReplicaCallRefused StoppingRefusal = new(ReplicaRefusal.Stopping, "The callee's node is stopping and admits no new call.");
    private static readonly ReplicaCallRefused DisklessRefusal = new(ReplicaRefusal.Argument, "The callee's node is diskless and holds no LedgerStore.");

    private readonly LocalNode _local;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
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
        var received = request.Payload;
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
                using var payload = WireBuffers.MoveToArcBuffer(ref received);
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

                // The reply holds the admission until Orleans first copies it or its deadline passes.
                var data = new RemoteReplicaRead.Data(CopyAndRelease(batch, request)) { Admission = admission };
                admission.HoldUntil(_timeProvider, ReplyDeadline);
                admission = null;
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

    /// <summary>Returns <paramref name="bytes"/> and a call to the host's bounds.</summary>
    internal void Return(long bytes)
    {
        lock (_lock)
        {
            _calls--;
            _bytes -= bytes;
            Debug.Assert(_calls >= 0 && _bytes >= 0, "The host returns no more than it admitted.");
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

    /// <summary>
    /// Copies a read's DATA out of the store's buffer into an Orleans buffer, and releases the store's buffer; the reply
    /// carries the copy, which the host never releases; within one silo the caller releases it (OR-I18).
    /// </summary>
    private static OrleansBuffer CopyAndRelease(ArcBuffer batch, LedgerReplicaReadRequest request)
    {
        using (batch)
        {
            Debug.Assert(batch.Length > 0, "DATA is never empty (OR-I15's reply check).");
            Debug.Assert(!request.IsRange || batch.Length == request.Length, "A ranged read answers the length it asked for (OR-I15's reply check).");
            return WireBuffers.CopyToOrleans(batch);
        }
    }

    private ReplicaCallRefused? AbovePayloadLimit(int length, string what) =>
        length > PayloadLimit
            ? new ReplicaCallRefused(ReplicaRefusal.Argument, string.Create(CultureInfo.InvariantCulture, $"A replica call's {what} of {length} bytes passes the payload limit of {PayloadLimit}."))
            : null;
}

/// <summary>
/// One call's admission to a <see cref="LedgerReplicaHost"/>: a call and its bytes, which it returns once, when it is
/// disposed: when its store operation ends, when its reply is serialized, or at its reply's deadline (OR-I17).
/// </summary>
internal sealed class LedgerReplicaHostAdmission : IDisposable
{
    private readonly LedgerReplicaHost _host;
    private readonly long _bytes;
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

    /// <summary>Returns the call and its bytes to the host, once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _deadline?.Dispose();
            _host.Return(_bytes);
        }
    }

    /// <summary>Holds the admission past its call, until <see cref="Dispose"/> or until <paramref name="deadline"/> passes.</summary>
    internal void HoldUntil(TimeProvider timeProvider, TimeSpan deadline)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _deadline = timeProvider.CreateTimer(static state => ((LedgerReplicaHostAdmission)state!).Dispose(), this, deadline, Timeout.InfiniteTimeSpan);
        }
    }
}
