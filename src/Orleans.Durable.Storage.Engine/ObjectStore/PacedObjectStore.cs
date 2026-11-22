using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.ObjectStore;

/// <summary>
/// Paces object-store requests against a dynamic per-node share of requests per second and reports when more than one
/// second of request debt is queued (orleans.md §5.14).
/// </summary>
public sealed class ObjectStoreRequestPacer
{
    /// <summary>The largest per-request span, so an extreme low rate never overflows tick arithmetic.</summary>
    private const double MaxSpanSeconds = 10.0;

    /// <summary>The most queued debt reserved ahead of now, so cancelled callers or transient low rates never wedge throttling.</summary>
    private const double MaxDebtSeconds = 2.0;

    /// <summary>How many listed items are treated as one page for pacing a multi-page listing.</summary>
    internal const int ListPageSize = 1000;

    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private Func<double>? _rateSource;
    private long _nextRequestAt;

    /// <summary>Initializes a new instance of the <see cref="ObjectStoreRequestPacer"/> class.</summary>
    /// <param name="timeProvider">The clock used to measure timestamps and delays.</param>
    public ObjectStoreRequestPacer(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _nextRequestAt = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Gets or sets the source of the node's current request rate in requests per second, or <see langword="null"/> when
    /// requests are unthrottled.
    /// </summary>
    public Func<double>? RateSource
    {
        get => Volatile.Read(ref _rateSource);
        set => Volatile.Write(ref _rateSource, value);
    }

    /// <summary>
    /// Gets a value indicating whether an active rate is configured and at least one second of request debt is queued,
    /// signalling admission to answer <c>Throttled</c> (orleans.md §5.14).
    /// </summary>
    public bool IsBudgetExhausted
    {
        get
        {
            if (!TryGetActiveRate(out _))
            {
                return false;
            }

            var now = _timeProvider.GetTimestamp();
            var next = Volatile.Read(ref _nextRequestAt);
            return next - now >= _timeProvider.TimestampFrequency;
        }
    }

    /// <summary>
    /// Paces one object-store request against <see cref="RateSource"/>. When the reserved debt window is full, waits a
    /// bounded slice without advancing <see cref="_nextRequestAt"/> and retries so concurrent callers never burst past
    /// the configured rate and cancelled callers leave no phantom debt.
    /// </summary>
    public ValueTask PaceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetActiveRate(out var rate))
        {
            return ValueTask.CompletedTask;
        }

        var frequency = (double)_timeProvider.TimestampFrequency;
        if (TryReserveSlot(rate, frequency, out var delay, out var retrySlice))
        {
            return delay > TimeSpan.Zero ? new ValueTask(Task.Delay(delay, _timeProvider, cancellationToken)) : ValueTask.CompletedTask;
        }

        return PaceSlowAsync(retrySlice, cancellationToken);
    }

    private bool TryReserveSlot(double rate, double frequency, out TimeSpan delay, out TimeSpan retrySlice)
    {
        var span = (long)Math.Clamp(Math.Ceiling(frequency / rate), 1.0, frequency * MaxSpanSeconds);
        var maxDebt = Math.Max(span, (long)(frequency * MaxDebtSeconds));

        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            var currentNext = Volatile.Read(ref _nextRequestAt);
            var limit = now + maxDebt;
            if (currentNext < limit)
            {
                var start = Math.Max(now, currentNext);
                Volatile.Write(ref _nextRequestAt, start + span);
                delay = start > now ? _timeProvider.GetElapsedTime(now, start) : TimeSpan.Zero;
                retrySlice = TimeSpan.Zero;
                return true;
            }

            // Debt window is full: do not advance _nextRequestAt or grant a clamped slot.
            // Sleep a bounded slice (at most half the debt window) and re-read RateSource on wakeup.
            var halfWindow = Math.Max(1L, maxDebt / 2);
            var waitTicks = Math.Min(Math.Max(span, currentNext - limit), halfWindow);
            retrySlice = _timeProvider.GetElapsedTime(now, now + waitTicks);
            if (retrySlice <= TimeSpan.Zero)
            {
                retrySlice = TimeSpan.FromTicks(1);
            }

            delay = TimeSpan.Zero;
            return false;
        }
    }

    private async ValueTask PaceSlowAsync(TimeSpan retrySlice, CancellationToken cancellationToken)
    {
        var frequency = (double)_timeProvider.TimestampFrequency;
        while (true)
        {
            await Task.Delay(retrySlice, _timeProvider, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetActiveRate(out var rate))
            {
                return;
            }

            if (TryReserveSlot(rate, frequency, out var delay, out retrySlice))
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
        }
    }

    private bool TryGetActiveRate(out double rate)
    {
        if (Volatile.Read(ref _rateSource) is not { } source)
        {
            rate = 0;
            return false;
        }

        rate = source();
        return double.IsFinite(rate) && rate > 0;
    }
}

/// <summary>
/// An <see cref="IObjectStore"/> decorator which paces every RPC through an <see cref="ObjectStoreRequestPacer"/> before
/// forwarding to the inner store (orleans.md §5.14).
/// </summary>
public sealed class PacedObjectStore : IObjectStore
{
    private readonly IObjectStore _inner;
    private readonly ObjectStoreRequestPacer _pacer;

    /// <summary>Initializes a new instance of the <see cref="PacedObjectStore"/> class.</summary>
    public PacedObjectStore(IObjectStore inner, ObjectStoreRequestPacer pacer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(pacer);
        _inner = inner;
        _pacer = pacer;
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.CreateAsync(name, content, crc32c, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.ReadAsync(name, offset, length, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GetPropertiesAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        await foreach (var item in _inner.ListAsync(prefix, startAfter, cancellationToken).ConfigureAwait(false))
        {
            if (count > 0 && count % ObjectStoreRequestPacer.ListPageSize == 0)
            {
                await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
            }

            count++;
            yield return item;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        await _inner.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.ReadVersionedAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A decorator implementing both <see cref="IObjectStore"/> and <see cref="IAppendableObjectStore"/> over one provider
/// instance, pacing every plain and appendable RPC through an <see cref="ObjectStoreRequestPacer"/> (orleans.md §5.14).
/// </summary>
public sealed class PacedAppendableObjectStore : IObjectStore, IAppendableObjectStore
{
    private readonly PacedObjectStore _objects;
    private readonly IAppendableObjectStore _appendable;
    private readonly ObjectStoreRequestPacer _pacer;

    /// <summary>Initializes a new instance of the <see cref="PacedAppendableObjectStore"/> class.</summary>
    public PacedAppendableObjectStore(IObjectStore objects, IAppendableObjectStore appendable, ObjectStoreRequestPacer pacer)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(appendable);
        ArgumentNullException.ThrowIfNull(pacer);
        if (!ReferenceEquals(objects, appendable))
        {
            throw new ArgumentException("The IObjectStore and IAppendableObjectStore must be the same provider instance.", nameof(appendable));
        }

        _objects = new PacedObjectStore(objects, pacer);
        _appendable = appendable;
        _pacer = pacer;
    }

    /// <summary>Creates a <see cref="PacedAppendableObjectStore"/> over one store instance implementing both interfaces.</summary>
    public static PacedAppendableObjectStore Create<TStore>(TStore inner, ObjectStoreRequestPacer pacer)
        where TStore : IObjectStore, IAppendableObjectStore =>
        new(inner, inner, pacer);

    /// <inheritdoc/>
    public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        _objects.CreateAsync(name, content, crc32c, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
        _objects.ReadAsync(name, offset, length, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _objects.GetPropertiesAsync(name, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) =>
        _objects.ListAsync(prefix, startAfter, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _objects.DeleteAsync(name, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        _objects.ReadVersionedAsync(name, cancellationToken);

    /// <inheritdoc/>
    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        _objects.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        return await _appendable.CreateAppendableAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        return await _appendable.AppendAsync(name, expectedLength, content, crc32c, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        return await _appendable.SealAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        await _pacer.PaceAsync(cancellationToken).ConfigureAwait(false);
        await _appendable.ReplaceWithTombstoneAsync(name, content, crc32c, cancellationToken).ConfigureAwait(false);
    }
}
