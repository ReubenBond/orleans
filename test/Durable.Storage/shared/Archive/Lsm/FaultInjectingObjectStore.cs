using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// An <see cref="IObjectStore"/> over another which fails calls within the contract (object-store.md §2.1), for
/// conformance and simulation: a call may throw before it reaches the store, a write may throw after it applied, one may
/// throw while its effect stays held and lands during a later call, or never, and a create or replace which applied may
/// answer <see cref="ObjectCreateOutcome.Exists"/> or <see cref="ObjectReplaceOutcome.Mismatch"/>, as a client which
/// retried its own landed attempt does. <see cref="FaultInjectingObjectStore{TStore}"/> fails appendable calls alike.
/// </summary>
/// <remarks>
/// Every injected failure is an <see cref="ObjectStoreFaultException"/>, whose call's outcome is unknown. A held write
/// lands through the inner store, which decides it then, so a held replace applies only at its expected version. The
/// faults follow the seed, and each one which fires is an event.
/// <para>
/// A write whose outcome is unknown may land at any later point or never, so a checker which judges the history keeps
/// both possibilities for the rest of it, and its search grows exponentially with their number, about tenfold for every
/// two (#363). Every such write meets a thrown fault, on its own call or on a read its caller makes first, as a replace
/// which learns its expected version does. The store therefore throws at most <paramref name="maxUnknownOutcomes"/>
/// faults; past that, a read has no fault and a write's fault is only <see cref="Fault.Retried"/>, an answer the checker
/// decides at once.
/// </para>
/// </remarks>
internal class FaultInjectingObjectStore(IObjectStore inner, int seed, double faultRate = 0.25, int maxUnknownOutcomes = int.MaxValue) : IObjectStore, IConformanceEvents
{
    private readonly Random _random = new(seed);
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _landing = new(1, 1);
    private readonly List<HeldWrite> _held = [];
    private readonly List<string> _events = [];
    private Fault _nextCreateFault;
    private int _unknownOutcomes;

    /// <summary>A fault <see cref="FaultInjectingObjectStore"/> injects into one call.</summary>
    internal enum Fault
    {
        None,
        Before,
        After,
        Held,
        Retried,
    }

    /// <summary>The faults injected into writes, each chosen uniformly when a write faults.</summary>
    internal static readonly Fault[] WriteFaults = [Fault.Before, Fault.After, Fault.Held, Fault.Retried];

    /// <summary>Gets a value indicating whether a write whose call threw is still held, so it may land during a later call.</summary>
    public bool HasHeld
    {
        get
        {
            lock (_sync)
            {
                return _held.Count > 0;
            }
        }
    }

    /// <summary>Makes the next create fail with <paramref name="fault"/>, whatever the seed.</summary>
    public void FaultNextCreate(Fault fault) => SetForced(ref _nextCreateFault, fault);

    /// <summary>Makes the next create land and then answer <see cref="ObjectCreateOutcome.Exists"/>, as a client which retried its own landed attempt does, whatever the seed.</summary>
    public void RetryNextCreate() => FaultNextCreate(Fault.Retried);

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        var call = "Create(" + name.Value + ")";
        var fault = TakeForced(ref _nextCreateFault, call);
        if (fault == Fault.None)
        {
            fault = Choose(write: true, call);
        }
        if (fault == Fault.Held)
        {
            var bytes = content.ToArray();
            Hold(new HeldWrite(call, async token =>
            {
                using var copy = ArcBuffer.Wrap(bytes);
                _ = await inner.CreateAsync(name, copy, crc32c, token);
            }));
        }

        ThrowIf(fault is Fault.Before or Fault.Held);
        var outcome = await inner.CreateAsync(name, content, crc32c, cancellationToken);
        ThrowIf(fault == Fault.After);

        // A retrying client meets its own landed attempt as an existing object (archive §3).
        return fault == Fault.Retried ? ObjectCreateOutcome.Exists.Instance : outcome;
    }

    /// <inheritdoc/>
    public async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        ThrowIf(Choose(write: false, "Read(" + name.Value + ")") != Fault.None);
        return await inner.ReadAsync(name, offset, length, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        ThrowIf(Choose(write: false, "Properties(" + name.Value + ")") != Fault.None);
        return await inner.GetPropertiesAsync(name, cancellationToken);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        ThrowIf(Choose(write: false, "List('" + prefix + "')") != Fault.None);
        await foreach (var name in inner.ListAsync(prefix, startAfter, cancellationToken))
        {
            yield return name;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        var call = "Delete(" + name.Value + ")";
        var fault = Choose(write: true, call);
        if (fault == Fault.Held)
        {
            Hold(new HeldWrite(call, token => inner.DeleteAsync(name, token)));
        }

        ThrowIf(fault is Fault.Before or Fault.Held);
        await inner.DeleteAsync(name, cancellationToken);
        ThrowIf(fault == Fault.After);
    }

    /// <inheritdoc/>
    public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        ThrowIf(Choose(write: false, "ReadVersioned(" + name.Value + ")") != Fault.None);
        return await inner.ReadVersionedAsync(name, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        var call = "Replace(" + name.Value + ")";
        var fault = Choose(write: true, call);
        if (fault == Fault.Held)
        {
            var bytes = content.ToArray();
            Hold(new HeldWrite(call, async token =>
            {
                using var copy = ArcBuffer.Wrap(bytes);
                _ = await inner.ReplaceAsync(name, expectedVersion, copy, crc32c, token);
            }));
        }

        ThrowIf(fault is Fault.Before or Fault.Held);
        var outcome = await inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
        ThrowIf(fault == Fault.After);

        // A retrying client meets its own landed attempt as another version (archive §3).
        return fault == Fault.Retried ? ObjectReplaceOutcome.Mismatch.Instance : outcome;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> TakeEvents()
    {
        lock (_sync)
        {
            List<string> events = [.. _events];
            _events.Clear();
            return events;
        }
    }

    private protected static void ThrowIf(bool fault)
    {
        if (fault)
        {
            throw new ObjectStoreFaultException();
        }
    }

    private protected void SetForced(ref Fault slot, Fault fault)
    {
        lock (_sync)
        {
            slot = fault;
        }
    }

    private protected Fault TakeForced(ref Fault forced, string call)
    {
        lock (_sync)
        {
            if (forced == Fault.None)
            {
                return Fault.None;
            }

            var fault = forced;
            forced = Fault.None;
            _events.Add(string.Create(CultureInfo.InvariantCulture, $"{call}: fault {fault}"));
            return fault;
        }
    }

    private protected Fault Choose(bool write, string call)
    {
        lock (_sync)
        {
            if (_random.NextDouble() >= faultRate)
            {
                return Fault.None;
            }

            var fault = write ? WriteFaults[_random.Next(WriteFaults.Length)] : Fault.Before;
            if (fault != Fault.Retried && _unknownOutcomes++ >= maxUnknownOutcomes)
            {
                fault = write ? Fault.Retried : Fault.None;
            }

            if (fault == Fault.None)
            {
                return fault;
            }

            _events.Add(string.Create(CultureInfo.InvariantCulture, $"{call}: fault {fault}"));
            return fault;
        }
    }

    private protected void Hold(HeldWrite write)
    {
        lock (_sync)
        {
            _held.Add(write);
        }
    }

    /// <summary>
    /// Lands each held write with even odds, in the order they were held.
    /// </summary>
    private protected async ValueTask LandHeldAsync(CancellationToken cancellationToken)
    {
        await _landing.WaitAsync(cancellationToken);
        try
        {
            List<HeldWrite> landing;
            lock (_sync)
            {
                landing = [.. _held.Where(_ => _random.Next(2) == 0)];
                foreach (var write in landing)
                {
                    _ = _held.Remove(write);
                    _events.Add("landed held " + write.Call);
                }
            }

            foreach (var write in landing)
            {
                await write.Land(cancellationToken);
            }
        }
        finally
        {
            _ = _landing.Release();
        }
    }

    private protected sealed record HeldWrite(string Call, Func<CancellationToken, ValueTask> Land);
}

/// <summary>
/// A <see cref="FaultInjectingObjectStore"/> over one store which is also appendable, whose appendable calls it fails
/// alike (object-store.md §2.2): an append which applied may answer as a retrying client does, by asking again, so it
/// answers <see cref="ObjectAppendOutcome.LengthMismatch"/> while the object is open, and, when a seal landed between its
/// landing and the retry, <see cref="ObjectAppendOutcome.Appended"/>, since the object holds its bytes.
/// </summary>
internal sealed class FaultInjectingObjectStore<TStore>(TStore inner, int seed, double faultRate = 0.25, int maxUnknownOutcomes = int.MaxValue)
    : FaultInjectingObjectStore(inner, seed, faultRate, maxUnknownOutcomes), IAppendableObjectStore
    where TStore : IObjectStore, IAppendableObjectStore
{
    private Fault _nextAppendFault;

    /// <summary>
    /// Gets or sets what happens between an append's landing and its retry, when the append answers as a retrying client
    /// does: a seal of its object, for example, as a concurrent seal lands there.
    /// </summary>
    public Func<ObjectName, ValueTask>? BeforeAppendRetry { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a retried append whose object a seal closed before the retry answers
    /// sealed, though its bytes are there: a mutant, which the conformance oracles must flag.
    /// </summary>
    public bool AnswersSealedForALandedAppendMutant { get; set; }

    /// <summary>Makes the next append land and then answer as a client which retried its own landed attempt does, whatever the seed.</summary>
    public void RetryNextAppend() => SetForced(ref _nextAppendFault, Fault.Retried);

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        var call = "CreateAppendable(" + name.Value + ")";
        var fault = Choose(write: true, call);
        if (fault == Fault.Held)
        {
            Hold(new HeldWrite(call, async token => _ = await inner.CreateAppendableAsync(name, token)));
        }

        ThrowIf(fault is Fault.Before or Fault.Held);
        var outcome = await inner.CreateAppendableAsync(name, cancellationToken);
        ThrowIf(fault == Fault.After);
        return fault == Fault.Retried ? ObjectCreateOutcome.Exists.Instance : outcome;
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        var call = "Append(" + name.Value + ")";
        var fault = TakeForced(ref _nextAppendFault, call);
        if (fault == Fault.None)
        {
            fault = Choose(write: true, call);
        }
        if (fault == Fault.Held)
        {
            var bytes = content.ToArray();
            Hold(new HeldWrite(call, async token =>
            {
                using var copy = ArcBuffer.Wrap(bytes);
                _ = await inner.AppendAsync(name, expectedLength, copy, crc32c, token);
            }));
        }

        ThrowIf(fault is Fault.Before or Fault.Held);
        var outcome = await inner.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);
        ThrowIf(fault == Fault.After);
        if (fault != Fault.Retried || outcome.Value is not ObjectAppendOutcome.Appended)
        {
            return outcome;
        }

        // A retrying client meets its own landed append: it asks again, after whatever landed in between.
        if (BeforeAppendRetry is { } between)
        {
            await between(name);
            if (AnswersSealedForALandedAppendMutant)
            {
                return ObjectAppendOutcome.Sealed.Instance;
            }
        }

        return await inner.AppendAsync(name, expectedLength, content, crc32c, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        var call = "Seal(" + name.Value + ")";
        var fault = Choose(write: true, call);
        if (fault == Fault.Held)
        {
            Hold(new HeldWrite(call, async token => _ = await inner.SealAsync(name, token)));
        }

        ThrowIf(fault is Fault.Before or Fault.Held);
        var outcome = await inner.SealAsync(name, cancellationToken);
        ThrowIf(fault == Fault.After);
        return outcome;
    }

    /// <inheritdoc/>
    public async ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        await LandHeldAsync(cancellationToken);
        var call = "Tombstone(" + name.Value + ")";
        var fault = Choose(write: true, call);
        if (fault == Fault.Held)
        {
            var bytes = content.ToArray();
            Hold(new HeldWrite(call, async token =>
            {
                using var copy = ArcBuffer.Wrap(bytes);
                await inner.ReplaceWithTombstoneAsync(name, copy, crc32c, token);
            }));
        }

        ThrowIf(fault is Fault.Before or Fault.Held);
        await inner.ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken);
        ThrowIf(fault == Fault.After);
    }
}

/// <summary>
/// A failure <see cref="FaultInjectingObjectStore"/> injected: the call's outcome is unknown.
/// </summary>
internal sealed class ObjectStoreFaultException : IOException
{
    public ObjectStoreFaultException()
        : base("An injected object-store failure: the call's outcome is unknown.")
    {
    }

    public ObjectStoreFaultException(string message)
        : base(message)
    {
    }

    public ObjectStoreFaultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
