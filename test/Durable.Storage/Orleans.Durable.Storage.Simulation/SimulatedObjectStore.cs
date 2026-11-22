using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Testing.Simulation;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// A simulated object store, with appendable objects, which injects the faults its contract allows: failed requests,
/// writes whose outcome is unknown, which apply before the caller hears, never, or later, and writes whose retrying client
/// meets its own landed attempt as <see cref="ObjectCreateOutcome.Exists"/>, <see cref="ObjectReplaceOutcome.Mismatch"/>,
/// or <see cref="ObjectAppendOutcome.LengthMismatch"/> (archive §3, §6; object-ledgers.md §3.1;
/// test/Durable.Storage/docs/spec/verification.md §7.2).
/// </summary>
/// <remarks>
/// <para>
/// Each request reaches the store after a random latency, takes effect there at one instant, and its answer returns
/// after another, so every operation is linearizable and every listing exact. A version is the decimal count of applied
/// writes, so every applied write gives its object a version it never had. A write copies its content before it
/// dispatches, so a write which lands later reads nothing of the caller's. A delayed append lands only at its expected
/// length on an open object, and a delayed seal or tombstone wherever it lands.
/// </para>
/// <para>
/// Any exception leaves a write's outcome unknown, and its type, <see cref="IOException"/> or
/// <see cref="TimeoutException"/>, is drawn independently of the outcome: the request was lost, or it applied and its
/// answer was lost, or it lands after a random delay, applying only where it still applies. A token cancelled after the
/// call ends the caller's wait with <see cref="OperationCanceledException"/> while the request goes on. Reads fail before
/// they reach the store at <see cref="ReadFailureRate"/>.
/// </para>
/// <para>
/// The store tracks itself in its environment; disposal disarms every request in flight and every delayed landing.
/// </para>
/// </remarks>
public sealed class SimulatedObjectStore : IObjectStore, IAppendableObjectStore, IAsyncDisposable
{
    private TaskCompletionSource? _replaceHold;
    private TaskCompletionSource? _replaceHeld;
    private readonly SimulationEnvironment _environment;
    private readonly SimulationRandom _random;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stopped;
    private readonly SortedDictionary<string, (byte[] Content, ObjectVersion Version, Appendability Appendability)> _objects = [with(StringComparer.Ordinal)];
    private readonly List<(ObjectName Name, byte[] Content)> _applied = [];
    private readonly List<(ObjectName Name, int AppliedBefore)> _deletions = [];
    private readonly HashSet<ObjectName> _changed = [];
    private long _appends;
    private bool _sealBeforeRetry;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatedObjectStore"/> class, empty, and tracks it in
    /// <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">The simulation.</param>
    /// <param name="name">The store's name in the trace, which also selects its random source.</param>
    public SimulatedObjectStore(SimulationEnvironment environment, string name)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _environment = environment;
        _random = environment.CreateRandom(name);
        _stopped = _stopping.Token;
        Name = name;
        _ = environment.Track(this);
    }

    /// <summary>
    /// Gets the store's name in the trace.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets the probability that a create's or replace's outcome is unknown.
    /// </summary>
    public double UnknownOutcomeRate { get; set; }

    /// <summary>
    /// Gets or sets the probability that a create or replace which applied answers as a retried request meets its own
    /// landed attempt: <see cref="ObjectCreateOutcome.Exists"/> or <see cref="ObjectReplaceOutcome.Mismatch"/>.
    /// </summary>
    public double RetriedAnswerRate { get; set; }

    /// <summary>
    /// Gets or sets the probability that a read or listing fails.
    /// </summary>
    public double ReadFailureRate { get; set; }

    /// <summary>
    /// Gets or sets the longest latency of a request, and separately of its answer.
    /// </summary>
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Gets or sets the longest delay after which a delayed write lands.
    /// </summary>
    public TimeSpan MaxLandingDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets or sets the probability that a create which carries its writer's CRC-32C meets content changed after the
    /// writer verified it: the store flips a byte of its copy, as a page changed in the writer's process would, so the
    /// copy no longer matches the CRC-32C and the store refuses it, as a service does.
    /// </summary>
    public double ChangedContentRate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a refusal of changed content loses its answer, so its caller sees an unknown
    /// outcome, as an S3 or Cloud Storage refusal whose answer is lost does; otherwise it is answered before any request,
    /// with <see cref="ObjectContentChangedException"/>, as Azure's check answers. The write applies nowhere either way.
    /// </summary>
    public bool LosesChangedContentRefusals { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the store accepts changed content, keeping the bytes its writer's CRC-32C
    /// does not match: a mutant, which the oracles must flag.
    /// </summary>
    public bool AcceptsChangedContent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a request and its answer yield to the scheduler in place of waiting a random
    /// latency: each still takes effect at one instant, between two scheduling points, but every interleaving of
    /// concurrent requests is the scheduler's choice, which an exploration enumerates, rather than the order of their
    /// latency draws (verification.md §4.1).
    /// </summary>
    public bool YieldsForLatency { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a create or replace checks its condition in one request and writes
    /// unconditionally in a second, so a concurrent write which lands between them is overwritten: a race mutant, which
    /// only an interleaving between the two requests reveals and the conformance kit's explored schedules must flag.
    /// </summary>
    public bool ChecksBeforeWritingMutant { get; set; }

    /// <summary>
    /// Gets or sets an observer which the store calls at the instant each request takes effect, in the store's
    /// linearization order: a create, replace, or tombstone which applied, an append which landed, a seal which sealed, a
    /// delete which removed an object, a listing, and a read, each served without a failure. A write which lands late calls it as it lands. It runs inside the request's step,
    /// so it neither blocks nor awaits; a trace recorder maps each effect onto a model's action (verification.md §5.1).
    /// </summary>
    public Action<ObjectStoreEffect>? Effects { get; set; }

    /// <summary>
    /// Holds every replace, before its request, until <see cref="ReleaseReplaces"/>, so a scenario keeps a manifest replace
    /// in flight by construction; returns a task which completes once a replace waits at the hold.
    /// </summary>
    public Task HoldReplaces()
    {
        _replaceHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _replaceHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _replaceHeld.Task;
    }

    /// <summary>Releases the replaces <see cref="HoldReplaces"/> holds.</summary>
    public void ReleaseReplaces() => Interlocked.Exchange(ref _replaceHold, null)?.TrySetResult();

    /// <summary>
    /// Gets the number of creates whose content changed after its writer verified it.
    /// </summary>
    public int ChangedContentWrites { get; private set; }

    /// <summary>
    /// Gets the names of the creates whose content changed after its writer verified it, which no write of the store's
    /// callers may reference.
    /// </summary>
    public IReadOnlyCollection<ObjectName> ChangedObjects => _changed;

    /// <summary>
    /// Gets the number of writes whose outcome was unknown.
    /// </summary>
    public int UnknownOutcomes { get; private set; }

    /// <summary>
    /// Gets the number of writes scheduled to land after their caller saw an unknown outcome.
    /// </summary>
    public int DelayedLandings { get; private set; }

    /// <summary>
    /// Gets the number of applied writes answered as a retried request's.
    /// </summary>
    public int RetriedAnswers { get; private set; }

    /// <summary>
    /// Returns the contents of the applied creates and replaces of <paramref name="name"/>, in the order they applied.
    /// </summary>
    public IReadOnlyList<byte[]> AppliedWritesOf(ObjectName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return [.. _applied.Where(write => write.Name == name).Select(static write => write.Content)];
    }

    /// <summary>
    /// Gets the applied creates and replaces of every object, each with its object's name and content, in the order they
    /// applied.
    /// </summary>
    public IReadOnlyList<(ObjectName Name, byte[] Content)> AppliedWrites => _applied;

    /// <summary>
    /// Gets the deletes which removed an object, each with the object's name and the number of writes
    /// <see cref="AppliedWrites"/> holds which applied before it, in the order they applied.
    /// </summary>
    public IReadOnlyList<(ObjectName Name, int AppliedBefore)> Deletions => _deletions;

    /// <summary>
    /// Returns a copy of <paramref name="content"/>: a write may land after its call returns, when
    /// the caller has released the content.
    /// </summary>
    private static byte[] Copy(ArcBuffer content) => content.ToArray();

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var copy = Copy(content);
        if (crc32c is not null && copy.Length > 0 && ChangedContentRate > 0 && _random.Chance(ChangedContentRate))
        {
            copy[_random.Next(copy.Length)] ^= 1;
            ChangedContentWrites++;
            _ = _changed.Add(name);
            Record(Invariant($"create of {name.Value}: its content changed after its writer verified it"));
        }

        await CheckAsync(name, copy, crc32c, "create", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (ChecksBeforeWritingMutant)
        {
            var absent = await RequestAsync(() => new Outcome<bool>(!_objects.ContainsKey(name.Value), null), cancellationToken);
            var written = await RequestAsync(() => Write(name, () => absent && Store(name, copy) is not null, "create"), cancellationToken);
            return written ? ObjectCreateOutcome.Created.Instance : ObjectCreateOutcome.Exists.Instance;
        }

        var created = await RequestAsync(() => Write(name, Create(name, copy), "create"), cancellationToken);
        return created ? ObjectCreateOutcome.Created.Instance : ObjectCreateOutcome.Exists.Instance;
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfEmpty(content);
        var copy = Copy(content);
        await CheckAsync(name, copy, crc32c, "replace", cancellationToken);
        if (Volatile.Read(ref _replaceHold) is { } hold)
        {
            _ = _replaceHeld?.TrySetResult();
            await hold.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ChecksBeforeWritingMutant)
        {
            var applies = await RequestAsync(
                () => new Outcome<bool>((_objects.TryGetValue(name.Value, out var stored) ? stored.Version : null) == expectedVersion, null),
                cancellationToken);
            var written = await RequestAsync(() => Write(name, () => applies ? Store(name, copy) : null, "replace"), cancellationToken);
            return written is null ? ObjectReplaceOutcome.Mismatch.Instance : new ObjectReplaceOutcome.Applied(written);
        }

        var version = await RequestAsync(() => Write(name, Replace(name, expectedVersion, copy), "replace"), cancellationToken);
        return version is null ? ObjectReplaceOutcome.Mismatch.Instance : new ObjectReplaceOutcome.Applied(version);
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateOutcome> CreateAppendableAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        var created = await RequestAsync(() => Write(name, CreateAppendable(name), "create appendable"), cancellationToken);
        return created ? ObjectCreateOutcome.Created.Instance : ObjectCreateOutcome.Exists.Instance;
    }

    /// <summary>
    /// Gets or sets a value indicating whether a retried append which meets its own landed attempt on a sealed object
    /// answers sealed, without looking for its bytes: a mutant, which the conformance oracles must flag.
    /// </summary>
    public bool AnswersSealedForALandedAppendMutant { get; set; }

    /// <summary>
    /// Makes the next append land, then a seal of its object land, as a concurrent seal does between a request and the
    /// retry of its lost answer, and then answers as that retry: the object holds the append's bytes.
    /// </summary>
    public void SealBetweenTheNextAppendAndItsRetry() => _sealBeforeRetry = true;

    /// <inheritdoc/>
    public async ValueTask<ObjectAppendOutcome> AppendAsync(ObjectName name, long expectedLength, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotAppend(expectedLength, content);
        var copy = Copy(content);
        await CheckAsync(name, copy, crc32c, "append", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_sealBeforeRetry)
        {
            _sealBeforeRetry = false;
            var append = Append(name, expectedLength, copy);
            return await RequestAsync(
                () =>
                {
                    _ = append();
                    _ = Seal(name)();
                    Record(Invariant($"append of {name.Value}: landed, answer lost, sealed, retried"));
                    return new Outcome<ObjectAppendOutcome>(append(), null);
                },
                cancellationToken);
        }

        var outcome = await RequestAsync(() => Write(name, Append(name, expectedLength, copy), "append"), cancellationToken);
        if (outcome is ObjectAppendOutcome.Appended { Length: var length } && _random.Chance(RetriedAnswerRate))
        {
            RetriedAnswers++;
            Record(Invariant($"append of {name.Value}: applied, answered as a retry meeting it"));
            return new ObjectAppendOutcome.LengthMismatch(length);
        }

        return outcome;
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectSealOutcome> SealAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        return await RequestAsync(() => Write(name, Seal(name), "seal"), cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask ReplaceWithTombstoneAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var copy = Copy(content);
        await CheckAsync(name, copy, crc32c, "tombstone", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await RequestAsync(() => Write(name, () => Store(name, copy), "tombstone"), cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectStoreArguments.ThrowIfNotRange(offset, length);
        cancellationToken.ThrowIfCancellationRequested();
        var content = await RequestAsync(() => Read(name), cancellationToken);
        if (content is null)
        {
            return null;
        }

        ObjectStoreArguments.ThrowIfPastEnd(name, offset, length, content.Value.Content.Length);
        return ArcBuffer.Wrap(content.Value.Content.AsMemory((int)offset, length));
    }

    /// <inheritdoc/>
    public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        var content = await RequestAsync(() => Read(name), cancellationToken);
        return content is { } found ? new VersionedObject(ArcBuffer.Wrap(found.Content), found.Version) : null;
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        var content = await RequestAsync(() => Read(name), cancellationToken);
        return content is { Content: var bytes, Appendability: var appendability }
            ? new ObjectProperties(
                bytes.Length,
                bytes.Length == 0 || appendability != Appendability.None ? null : ObjectStoreChecksums.Crc32C(new ReadOnlySequence<byte>(bytes)))
            : null;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        ObjectStoreArguments.ThrowIfNotPrefix(prefix);
        ObjectStoreArguments.ThrowIfNotStart(startAfter);
        return ListOnePageAsync(prefix, startAfter, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await RequestAsync(() => new Outcome<bool>(Delete(name), null), cancellationToken);
    }

    private bool Delete(ObjectName name)
    {
        if (!_objects.Remove(name.Value))
        {
            return false;
        }

        _deletions.Add((name, _applied.Count));
        Effects?.Invoke(new ObjectStoreEffect(ObjectStoreEffectKind.Deleted, name.Value, null));
        return true;
    }

    /// <summary>
    /// Lists the objects with <paramref name="prefix"/> in one request, the listing's one page.
    /// </summary>
    private async IAsyncEnumerable<ListedObject> ListOnePageAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = await RequestAsync(
            () =>
            {
                if (_random.Chance(ReadFailureRate))
                {
                    return Outcome<IReadOnlyList<ListedObject>>.Failed(Failure("list"));
                }

                Effects?.Invoke(new ObjectStoreEffect(ObjectStoreEffectKind.Listed, prefix, null));
                return new([.. _objects.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal) && ObjectStoreArguments.IsAfter(entry.Key, startAfter)).Select(static entry => new ListedObject(new ObjectName(entry.Key), entry.Value.Content.Length))], null);
            },
            cancellationToken);
        foreach (var name in names)
        {
            yield return name;
        }
    }

    /// <summary>
    /// Disarms every request in flight and every delayed landing.
    /// </summary>
    /// <returns>A task which completes once the store has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!_stopping.IsCancellationRequested)
        {
            await _stopping.CancelAsync();
            _stopping.Dispose();
        }
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    /// <summary>
    /// Checks <paramref name="copy"/>, the bytes the store received, against its writer's <paramref name="crc32c"/>, as a
    /// service does, and refuses a mismatch: answered at once, or, with <see cref="LosesChangedContentRefusals"/>, sent
    /// and refused with its answer lost.
    /// </summary>
    /// <exception cref="ObjectContentChangedException">The copy does not match, and the refusal is answered.</exception>
    private async ValueTask CheckAsync(ObjectName name, byte[] copy, uint? crc32c, string kind, CancellationToken cancellationToken)
    {
        if (AcceptsChangedContent || crc32c is not { } expected || ObjectStoreChecksums.Crc32C(new ReadOnlySequence<byte>(copy)) == expected)
        {
            return;
        }

        if (!LosesChangedContentRefusals)
        {
            Record(Invariant($"{kind} of {name.Value}: refused for changed content, answered"));
            throw ObjectContentChangedException.For(name);
        }

        Record(Invariant($"{kind} of {name.Value}: refused for changed content, answer lost"));
        _ = await RequestAsync(() => Outcome<bool>.Failed(Failure(kind + " " + name.Value)), cancellationToken);
    }

    /// <summary>
    /// Carries one request to the store and its answer back, independently of the caller, who may stop waiting.
    /// </summary>
    private async Task<T> RequestAsync<T>(Func<Outcome<T>> execute, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RunAsync(answer, execute);
        using var registration = cancellationToken.Register(() => _ = answer.TrySetException(new OperationCanceledException(cancellationToken)));
        return await answer.Task;
    }

    private async Task RunAsync<T>(TaskCompletionSource<T> answer, Func<Outcome<T>> execute)
    {
        try
        {
            await DelayAsync(MaxLatency);
            var outcome = execute();
            await DelayAsync(MaxLatency);
            _ = outcome.Failure is null ? answer.TrySetResult(outcome.Result!) : answer.TrySetException(outcome.Failure);
        }
        catch (OperationCanceledException)
        {
            _ = answer.TrySetException(new ObjectDisposedException(Name, "The store stopped."));
        }
    }

    private Outcome<(byte[] Content, ObjectVersion Version, Appendability Appendability)?> Read(ObjectName name)
    {
        if (_random.Chance(ReadFailureRate))
        {
            return Outcome<(byte[], ObjectVersion, Appendability)?>.Failed(Failure("read " + name.Value));
        }

        Effects?.Invoke(new ObjectStoreEffect(ObjectStoreEffectKind.Read, name.Value, null));
        return new(_objects.TryGetValue(name.Value, out var stored) ? stored : null, null);
    }

    /// <summary>
    /// Takes a write to its outcome: applied and answered, possibly as a retried request's; or unknown, lost, applied,
    /// or landing later.
    /// </summary>
    private Outcome<T> Write<T>(ObjectName name, Func<T> apply, string kind)
    {
        if (!_random.Chance(UnknownOutcomeRate))
        {
            var result = apply();
            if (result is true or ObjectVersion && _random.Chance(RetriedAnswerRate))
            {
                RetriedAnswers++;
                Record(Invariant($"{kind} of {name.Value}: applied, answered as a retry meeting it"));
                return new(default, null);
            }

            return new(result, null);
        }

        UnknownOutcomes++;
        switch (_random.Next(3))
        {
            case 0:
                Record(Invariant($"{kind} of {name.Value}: request lost"));
                break;
            case 1:
                _ = apply();
                Record(Invariant($"{kind} of {name.Value}: answer lost"));
                break;
            default:
                DelayedLandings++;
                var delay = _random.NextTimeSpan(MaxLandingDelay);
                Record(Invariant($"{kind} of {name.Value}: lands in {delay.TotalMilliseconds:0.###} ms"));
                _ = LandAsync(apply, delay);
                break;
        }

        return Outcome<T>.Failed(Failure(kind + " " + name.Value));
    }

    /// <summary>
    /// Returns the create of <paramref name="name"/>, which stores <paramref name="content"/> only where no object is.
    /// </summary>
    private Func<bool> Create(ObjectName name, byte[] content) => () =>
    {
        if (_objects.ContainsKey(name.Value))
        {
            return false;
        }

        Store(name, content);
        return true;
    };

    /// <summary>
    /// Returns the replace of <paramref name="name"/>, which stores <paramref name="content"/> only where the object holds
    /// <paramref name="expectedVersion"/>, or none for <see langword="null"/>, and gives the new version or
    /// <see langword="null"/>.
    /// </summary>
    private Func<ObjectVersion?> Replace(ObjectName name, ObjectVersion? expectedVersion, byte[] content) => () =>
    {
        var current = _objects.TryGetValue(name.Value, out var stored) ? stored.Version : null;
        return current == expectedVersion ? Store(name, content) : null;
    };

    private ObjectVersion Store(ObjectName name, byte[] content, Appendability appendability = Appendability.None)
    {
        _applied.Add((name, content));
        var version = new ObjectVersion(_applied.Count.ToString(CultureInfo.InvariantCulture));
        _objects[name.Value] = (content, version, appendability);
        Effects?.Invoke(new ObjectStoreEffect(ObjectStoreEffectKind.Written, name.Value, content));
        return version;
    }

    /// <summary>
    /// Returns the create of an empty, open, appendable <paramref name="name"/>, only where no object is.
    /// </summary>
    private Func<bool> CreateAppendable(ObjectName name) => () =>
    {
        if (_objects.ContainsKey(name.Value))
        {
            return false;
        }

        _ = Store(name, [], Appendability.Open);
        return true;
    };

    /// <summary>
    /// Returns the append of <paramref name="content"/> to <paramref name="name"/>, which lands only on an open object of
    /// <paramref name="expectedLength"/> bytes. An append is not one of <see cref="AppliedWrites"/>, and gives its object a
    /// version no create or replace gives.
    /// </summary>
    private Func<ObjectAppendOutcome> Append(ObjectName name, long expectedLength, byte[] content) => () =>
    {
        if (!_objects.TryGetValue(name.Value, out var stored))
        {
            return ObjectAppendOutcome.Absent.Instance;
        }

        if (stored.Appendability != Appendability.Open)
        {
            // A sealed object which holds the append's bytes at its expected length took an earlier attempt of this call.
            return stored.Appendability == Appendability.Sealed
                && !AnswersSealedForALandedAppendMutant
                && stored.Content.Length - content.Length >= expectedLength
                && stored.Content.AsSpan((int)expectedLength, content.Length).SequenceEqual(content)
                    ? new ObjectAppendOutcome.Appended(expectedLength + content.Length)
                    : ObjectAppendOutcome.Sealed.Instance;
        }

        if (stored.Content.Length != expectedLength)
        {
            return new ObjectAppendOutcome.LengthMismatch(stored.Content.Length);
        }

        byte[] appended = [.. stored.Content, .. content];
        _objects[name.Value] = (appended, new ObjectVersion(Invariant($"{_applied.Count}+{++_appends}")), Appendability.Open);
        Effects?.Invoke(new ObjectStoreEffect(ObjectStoreEffectKind.Appended, name.Value, content));
        return new ObjectAppendOutcome.Appended(appended.Length);
    };

    /// <summary>
    /// Returns the seal of <paramref name="name"/>, which seals an open object and changes no other.
    /// </summary>
    private Func<ObjectSealOutcome> Seal(ObjectName name) => () =>
    {
        if (!_objects.TryGetValue(name.Value, out var stored))
        {
            return ObjectSealOutcome.Absent.Instance;
        }

        if (stored.Appendability == Appendability.None)
        {
            return ObjectSealOutcome.NotAppendable.Instance;
        }

        _objects[name.Value] = stored with { Appendability = Appendability.Sealed };
        Effects?.Invoke(new ObjectStoreEffect(ObjectStoreEffectKind.Sealed, name.Value, null));
        return ObjectSealOutcome.Sealed.Instance;
    };

    private async Task LandAsync<T>(Func<T> apply, TimeSpan delay)
    {
        try
        {
            await Task.Delay(SimulationEnvironment.WholeMilliseconds(delay), SimulationEnvironment.Clock, _stopped);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _ = apply();
        Record("delayed write landed where it applied");
    }

    /// <summary>
    /// Returns the exception which reports a failed request, its type drawn independently of the outcome.
    /// </summary>
    private Exception Failure(string request) =>
        _random.Chance(0.5)
            ? new IOException(Invariant($"The request '{request}' to the simulated object store {Name} failed."))
            : new TimeoutException(Invariant($"The request '{request}' to the simulated object store {Name} timed out."));

    private Task DelayAsync(TimeSpan maximum) =>
        YieldsForLatency
            ? YieldAsync()
            : maximum <= TimeSpan.Zero
                ? Task.CompletedTask
                : Task.Delay(SimulationEnvironment.WholeMilliseconds(_random.NextTimeSpan(maximum)), SimulationEnvironment.Clock, _stopped);

    private async Task YieldAsync()
    {
        await Task.Yield();
        _stopped.ThrowIfCancellationRequested();
    }

    private void Record(string entry) => _environment.Trace.Record(Invariant($"object store {Name}: {entry}"));

    /// <summary>
    /// Whether a stored object is appendable, and if so whether it is open.
    /// </summary>
    private enum Appendability
    {
        /// <summary>Created or replaced whole: appends answer sealed.</summary>
        None,

        /// <summary>Appendable and open.</summary>
        Open,

        /// <summary>Appendable and sealed.</summary>
        Sealed,
    }


    private readonly record struct Outcome<T>(T? Result, Exception? Failure)
    {
        public static Outcome<T> Failed(Exception failure) => new(default, failure);
    }
}
