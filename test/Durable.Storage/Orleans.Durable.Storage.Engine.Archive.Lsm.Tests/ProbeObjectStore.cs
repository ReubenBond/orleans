using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// An <see cref="IObjectStore"/> over another which records each call, holds ranged reads before they read, versioned reads
/// after they read, and replaces after they apply until a test releases them, fails ranged reads, deletes, and replaces
/// as a test scripts, and runs a test's work around each listing, so view, committer, and sweeper tests can place calls.
/// </summary>
internal sealed class ProbeObjectStore(IObjectStore inner, TimeProvider? timeProvider = null) : IObjectStore
{
    private readonly ConcurrentQueue<string> _calls = new();
    private readonly ConcurrentQueue<(ObjectName Name, long Offset, int Length)> _rangedReads = new();
    private readonly ConcurrentQueue<string?> _contexts = new();
    private readonly ConcurrentQueue<long> _replaceStarts = new();
    private readonly ConcurrentQueue<long> _replaceCompletions = new();
    private readonly ConcurrentQueue<ReplaceFault> _replaceFaults = new();
    private readonly ConcurrentQueue<ObjectWrite> _writes = new();
    private TaskCompletionSource? _readGate;
    private TaskCompletionSource? _rangedReadGate;
    private int _rangedReadFailures;
    private int _deleteFailures;
    private TaskCompletionSource? _replaceGate;

    /// <summary>
    /// How a scripted replace fails: not at all, before it reaches the store, or after the store applied it.
    /// </summary>
    internal enum ReplaceFault
    {
        None,
        Before,
        After,
    }

    /// <summary>
    /// Gets each call, as its kind and object name, in the order they began.
    /// </summary>
    public IReadOnlyCollection<string> Calls => _calls;

    /// <summary>Gets the ambient <see cref="Context"/> during each versioned read and replace, in call order.</summary>
    public IReadOnlyCollection<string?> Contexts => _contexts;

    /// <summary>Gets an ambient value a caller sets, which the probe records during its versioned reads and replaces.</summary>
    public static AsyncLocal<string?> Context { get; } = new();

    /// <summary>
    /// Gets each ranged read's object, offset, and length, in the order they began.
    /// </summary>
    public IReadOnlyCollection<(ObjectName Name, long Offset, int Length)> RangedReads => _rangedReads;

    /// <summary>
    /// Gets each create and replace, with the CRC-32C it carried and whether that matches its content.
    /// </summary>
    public IReadOnlyCollection<ObjectWrite> Writes => _writes;

    /// <summary>
    /// Gets the timestamp at which each replace began, from the time provider.
    /// </summary>
    public IReadOnlyCollection<long> ReplaceStarts => _replaceStarts;

    /// <summary>
    /// Gets the timestamp at which each replace which answered completed, from the time provider.
    /// </summary>
    public IReadOnlyCollection<long> ReplaceCompletions => _replaceCompletions;

    /// <summary>
    /// Gets or sets a value indicating whether versioned reads fail before they reach the store.
    /// </summary>
    public bool FailReads { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether each create and replace reaches the store with a CRC-32C its content does
    /// not have, as content which changed after its writer verified it would, so the store refuses it.
    /// </summary>
    public bool ChangeContent { get; set; }

    /// <summary>
    /// Gets or sets what runs once a replace scripted to fail after the store applied it has been applied, before the
    /// replace fails: a competing writer, for example.
    /// </summary>
    public Func<Task>? AfterLanding { get; set; }

    /// <summary>
    /// Gets or sets work to run once a listing is asked for and before it enumerates, given its prefix: a commit landing
    /// between a sweep's listing and its manifest read, for example.
    /// </summary>
    public Func<string, Task>? BeforeList { get; set; }

    /// <summary>
    /// Gets or sets work to run once a listing has enumerated, given its prefix: a delete landing between a sweep's
    /// listing and its reads, for example.
    /// </summary>
    public Func<string, Task>? AfterList { get; set; }

    /// <summary>
    /// Gets or sets how each listed entry is reported, as a store whose listing gives a wrong length would.
    /// </summary>
    public Func<ListedObject, ListedObject>? Listed { get; set; }

    /// <summary>
    /// Gets the number of versioned reads begun.
    /// </summary>
    public int VersionedReads => _calls.Count(static call => call.StartsWith("ReadVersioned(", StringComparison.Ordinal));

    /// <summary>
    /// Fails the next <paramref name="count"/> ranged reads before they reach the store.
    /// </summary>
    public void FailRangedReads(int count) => Volatile.Write(ref _rangedReadFailures, count);

    /// <summary>
    /// Fails the next <paramref name="count"/> deletes before they reach the store.
    /// </summary>
    public void FailDeletes(int count) => Volatile.Write(ref _deleteFailures, count);

    /// <summary>
    /// Holds every ranged read which begins from now on before it reads, until <see cref="ReleaseRangedReads"/>.
    /// </summary>
    public void HoldRangedReads() => _rangedReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Releases the held ranged reads, and holds no more.
    /// </summary>
    public void ReleaseRangedReads() => Interlocked.Exchange(ref _rangedReadGate, null)?.TrySetResult();

    /// <summary>
    /// Holds every versioned read which begins from now on after it read, until <see cref="ReleaseReads"/>.
    /// </summary>
    public void HoldReads() => _readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Releases the held versioned reads, and holds no more.
    /// </summary>
    public void ReleaseReads() => Interlocked.Exchange(ref _readGate, null)?.TrySetResult();

    /// <summary>
    /// Holds every replace which begins from now on after the store answered it, until <see cref="ReleaseReplaces"/>.
    /// </summary>
    public void HoldReplaces() => _replaceGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Releases the held replaces, and holds no more.
    /// </summary>
    public void ReleaseReplaces() => Interlocked.Exchange(ref _replaceGate, null)?.TrySetResult();

    /// <summary>
    /// Fails the next replaces, one fault each, in order.
    /// </summary>
    public void FailReplaces(params ReplaceFault[] faults)
    {
        foreach (var fault in faults)
        {
            _replaceFaults.Enqueue(fault);
        }
    }

    /// <summary>
    /// Gets or sets what runs as each create begins, given the number of creates begun before it; it may throw, which
    /// fails the create before it reaches the store.
    /// </summary>
    public Action<int>? BeforeCreate { get; set; }

    /// <summary>
    /// Gets or sets what runs as each create begins, after <see cref="BeforeCreate"/>, given the number of creates begun
    /// before it; it may throw, which fails the create before it reaches the store.
    /// </summary>
    public Func<int, ValueTask>? BeforeCreateAsync { get; set; }

    public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        var earlier = _calls.Count(static call => call.StartsWith("Create(", StringComparison.Ordinal));
        var call = "Create(" + name.Value + ")";
        _calls.Enqueue(call);
        _writes.Enqueue(new ObjectWrite(call, crc32c, ObjectStoreChecksums.Crc32C(content)));
        BeforeCreate?.Invoke(earlier);
        if (BeforeCreateAsync is { } beforeCreate)
        {
            await beforeCreate(earlier);
        }

        return await inner.CreateAsync(name, content, Sent(crc32c), cancellationToken);
    }

    /// <summary>Gets or sets a step which runs before each ranged read, given its object and offset.</summary>
    public Func<ObjectName, long, Task>? BeforeRangedRead { get; set; }

    public async ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue("Read(" + name.Value + ")");
        _rangedReads.Enqueue((name, offset, length));
        if (BeforeRangedRead is { } before)
        {
            await before(name, offset);
        }

        if (_rangedReadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return Interlocked.Decrement(ref _rangedReadFailures) >= 0
            ? throw new IOException("A scripted ranged read failure.")
            : await inner.ReadAsync(name, offset, length, cancellationToken);
    }

    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue("Properties(" + name.Value + ")");
        return inner.GetPropertiesAsync(name, cancellationToken);
    }

    public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _calls.Enqueue("List(" + prefix + ")");
        if (BeforeList is { } before)
        {
            await before(prefix);
        }

        await foreach (var entry in inner.ListAsync(prefix, startAfter, cancellationToken))
        {
            yield return Listed?.Invoke(entry) ?? entry;
        }

        if (AfterList is { } after)
        {
            await after(prefix);
        }
    }

    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue("Delete(" + name.Value + ")");
        return Interlocked.Decrement(ref _deleteFailures) >= 0
            ? ValueTask.FromException(new IOException("A scripted delete failure."))
            : inner.DeleteAsync(name, cancellationToken);
    }

    /// <summary>Gets or sets a step run before each versioned read, given the object's name, as a manifest view's read.</summary>
    public Func<ObjectName, Task>? BeforeReadVersioned { get; set; }

    public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue("ReadVersioned(" + name.Value + ")");
        if (BeforeReadVersioned is { } before)
        {
            await before(name);
        }

        _contexts.Enqueue(Context.Value);
        if (FailReads)
        {
            throw new IOException("A scripted read failure.");
        }

        var gate = _readGate;
        var found = await inner.ReadVersionedAsync(name, cancellationToken);
        if (gate is not null)
        {
            try
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                found?.Content.Dispose();
                throw;
            }
        }

        return found;
    }

    public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        var call = "Replace(" + name.Value + ")";
        _calls.Enqueue(call);
        _contexts.Enqueue(Context.Value);
        _writes.Enqueue(new ObjectWrite(call, crc32c, ObjectStoreChecksums.Crc32C(content)));
        _replaceStarts.Enqueue((timeProvider ?? TimeProvider.System).GetTimestamp());
        var fault = _replaceFaults.TryDequeue(out var scripted) ? scripted : ReplaceFault.None;
        if (fault == ReplaceFault.Before)
        {
            throw new IOException("A scripted replace failure before the store applied it.");
        }

        var gate = _replaceGate;
        var outcome = await inner.ReplaceAsync(name, expectedVersion, content, Sent(crc32c), cancellationToken);
        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (fault != ReplaceFault.After)
        {
            _replaceCompletions.Enqueue((timeProvider ?? TimeProvider.System).GetTimestamp());
            return outcome;
        }

        if (AfterLanding is { } afterLanding)
        {
            await afterLanding();
        }

        throw new IOException("A scripted replace failure after the store applied it.");
    }

    /// <summary>Returns the CRC-32C a write sends: its writer's, or, with <see cref="ChangeContent"/>, another.</summary>
    private uint? Sent(uint? crc32c) => ChangeContent && crc32c is { } writers ? writers ^ 1 : crc32c;
}

/// <summary>
/// A create or replace a <see cref="ProbeObjectStore"/> saw: its call, the CRC-32C it carried, and its content's own.
/// </summary>
/// <param name="Call">The call's kind and object name.</param>
/// <param name="Crc32C">The writer's CRC-32C the call carried, or <see langword="null"/> when it carried none.</param>
/// <param name="ContentCrc32C">The CRC-32C of the call's content.</param>
internal sealed record ObjectWrite(string Call, uint? Crc32C, uint ContentCrc32C)
{
    /// <summary>Gets a value indicating whether the call carried a CRC-32C, and it matches the content.</summary>
    public bool CarriesItsContentsCrc32C => Crc32C == ContentCrc32C;
}
