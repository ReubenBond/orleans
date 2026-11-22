using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// A deliberately wrong store: <see cref="JournalStoreTarget"/> changes the real store's answers or effects as each names,
/// to break one clause of the Journals contract (journals.md §2) for <see cref="JournalStoreSpec"/> to catch.
/// </summary>
public enum JournalStoreMutant
{
    /// <summary>The real store.</summary>
    None,

    /// <summary>An <c>Append</c> rejected at a stale expected version is written again at the current one and answered as that write is.</summary>
    AcceptsAtStaleVersion,

    /// <summary>A write answered <c>Moved</c> is sent again once the process serves its key, and lands.</summary>
    MovedEntryLands,

    /// <summary>A write answered <c>Fenced</c> is sent again to the successor, and lands.</summary>
    FencedEntryLands,

    /// <summary>An <c>Append</c> answered <c>Ambiguous</c> is written again at the journal's version after the takeover, so a landed one counts twice.</summary>
    AmbiguousEntryWrittenTwice,

    /// <summary>An <c>Accepted</c> answer reports a version which names no state of the journal.</summary>
    AcceptedVersionNamesNoState,

    /// <summary>A read answers what the key's previous read did, missing what was acknowledged since.</summary>
    ReadMissesAcknowledged,

    /// <summary>A read's content ends with the payload of the key's last <c>Append</c> answered without effect.</summary>
    ReadReflectsUndecided,

    /// <summary>A read reports a deleted journal, whose version is not <c>Origin</c>, as existing.</summary>
    DeleteLeavesJournal,

    /// <summary>A <c>Replace</c> of a non-empty snapshot is written as an <c>Append</c>, which adds it to the content.</summary>
    ReplaceFoldsAsAppend,

    /// <summary>A rejection reports a <c>CurrentVersion</c> which names no state of the journal.</summary>
    RejectionNamesNoState,
}

/// <summary>
/// Runs <see cref="JournalStoreSpec"/>'s requests against a <see cref="JournalStore"/> over live ledgers on memory
/// replicas and the memory archive store, whose limits the spec judges, and takes the environment's steps: a takeover,
/// which replaces the journals' open ledger through the process's transition executor; a write whose load the store holds
/// at the archive while a takeover fences it; and a crash once a write's batch reaches its replica, which disposes the
/// process's writer registry and starts the next process over the same replicas and map register, which serves nothing
/// until a takeover.
/// </summary>
/// <remarks>
/// <para>
/// A write names its expected version by how many answers before the newest version it learned of the journal: from an
/// acceptance, a rejection, or a read, as a writer learns them (JournalModel's premise), and from <c>Origin</c> at first.
/// Its answer carries the write as it ran, the version resolved. The tombstone retention is zero, so the expiry scan
/// proposes an <c>Expired</c> of every warm deleted journal; warm state's budget is zero for an even seed, so each touch
/// loads its key afresh and the scan proposes nothing. Every buffer it encodes or reads is released before the call returns.
/// </para>
/// </remarks>
internal sealed class JournalStoreTarget : IAsyncDisposable
{
    private readonly InMemoryArchiveStore _memory;
    private readonly GatedArchiveStore _store;
    private readonly JournalStoreOptions _options;
    private readonly JournalStoreMutant _mutant;
    private readonly Lock _lock = new();
    private readonly List<JournalVersion>[] _learned = [.. JournalStoreSpec.Keys.Select(static _ => new List<JournalVersion> { JournalVersion.Origin })];
    private readonly JournalAnswer?[] _previousReads = new JournalAnswer?[JournalStoreSpec.Keys.Length];
    private readonly byte[]?[] _undecided = new byte[]?[JournalStoreSpec.Keys.Length];
    private readonly List<(int Key, JournalWriteKind Kind, JournalVersion Expected, byte[] Payload)> _moved = [];
    private readonly List<(int Key, byte[] Payload)> _ambiguous = [];
    private LiveLedgers _ledgers;
    private JournalStore _journals;

    private JournalStoreTarget(LiveLedgers ledgers, InMemoryArchiveStore memory, JournalStoreOptions options, JournalStoreMutant mutant)
    {
        _ledgers = ledgers;
        _memory = memory;
        _store = new GatedArchiveStore(memory);
        _options = options;
        _mutant = mutant;
        _journals = new JournalStore(ledgers.Registry, _store, options, TimeProvider.System);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Starts a process over a memory archive store which refuses an <c>Append</c> past <paramref name="maxAppends"/> of a
    /// generation and asks for a <c>Replace</c> from <paramref name="replaceRequestAppends"/>, with warm state's budget
    /// zero for an even <paramref name="seed"/>, and whose answers <paramref name="mutant"/> changes.
    /// </summary>
    public static async Task<JournalStoreTarget> StartAsync(int seed, int maxAppends, int replaceRequestAppends, JournalStoreMutant mutant = JournalStoreMutant.None)
    {
        var ledgers = await LiveLedgers.StartAsync(new InMemoryConditionalRegister(), TimeProvider.System, Token);
        await ledgers.BootstrapAsync(Token);
        var options = new JournalStoreOptions { TombstoneRetention = TimeSpan.Zero };
        if (seed % 2 == 0)
        {
            options.WarmStateBytes = 0;
        }

        return new JournalStoreTarget(ledgers, new InMemoryArchiveStore(maxAppends: maxAppends, replaceRequestAppends: replaceRequestAppends), options, mutant);
    }

    /// <summary>Runs a write.</summary>
    public async Task<JournalAnswer> WriteAsync(JournalWrite request)
    {
        var expected = Resolve(request.Key, request.Back);
        var payload = request.Payload;
        var input = request.Kind switch
        {
            JournalWriteKind.Append => JournalInput.Append(JournalStoreSpec.Keys[request.Key], expected.ToString(), payload),
            JournalWriteKind.Replace => JournalInput.Replace(JournalStoreSpec.Keys[request.Key], expected.ToString(), payload),
            JournalWriteKind.Delete => JournalInput.Delete(JournalStoreSpec.Keys[request.Key], expected.ToString()),
            _ => throw new UnreachableException(),
        };
        var outcome = request.Disruption switch
        {
            JournalDisruption.None => await SendAsync(request.Key, request.Kind, expected, payload),
            JournalDisruption.Takeover => await FencedAsync(request.Key, request.Kind, expected, payload),
            JournalDisruption.Crash => await CrashingAsync(request.Key, request.Kind, expected, payload),
            _ => throw new UnreachableException(),
        };
        return Answer(request.Key, request.Kind, expected, payload, input, outcome);
    }

    /// <summary>Runs a read, and learns the version it answers.</summary>
    public async Task<JournalAnswer> ReadAsync(JournalRead request)
    {
        var key = JournalStoreSpec.Keys[request.Key];
        var input = JournalInput.Read(key);
        var outcome = await _journals.ReadAsync(JournalRowKey.Encode(key), Token);
        var answer = outcome switch
        {
            JournalReadResult result => await AnswerAsync(input, result),
            JournalMoved => new JournalAnswer(input, JournalOutput.Moved),
            null => throw new UnreachableException(),
        };
        if (answer.Output.Outcome != JournalOutcome.Accepted)
        {
            return answer;
        }

        var reported = answer;
        if (_mutant == JournalStoreMutant.ReadMissesAcknowledged)
        {
            reported = _previousReads[request.Key] ?? answer;
            _previousReads[request.Key] = answer;
        }
        else if (_mutant == JournalStoreMutant.DeleteLeavesJournal && !answer.Output.Exists && answer.Output.Version != JournalModel.Origin)
        {
            reported = answer with { Output = answer.Output with { Exists = true } };
        }
        else if (_mutant == JournalStoreMutant.ReadReflectsUndecided && _undecided[request.Key] is { } undecided)
        {
            reported = answer with { Output = answer.Output with { Content = (byte[])[.. answer.Output.Content.Span, .. undecided] } };
        }

        Learn(request.Key, JournalVersion.Parse(reported.Output.Version!));
        return reported;
    }

    /// <summary>
    /// Replaces the journals' open ledger through the process's executor, and waits until the process serves its successor
    /// and the store has retired the replaced ledger's admission, so the next call meets the successor's admission.
    /// </summary>
    public async Task<JournalTakenOver> TakeOverAsync()
    {
        var replaced = _ledgers.OpenContaining(JournalRowKey.Encode(JournalStoreSpec.Keys[0]).Span.ToArray()).Id;
        await _ledgers.ReplaceAsync(replaced, Token);
        await _ledgers.ReconcileAsync(Token);
        var waiting = TimeProvider.System.GetTimestamp();
        while (_journals.ServedLedgers.Contains(replaced))
        {
            Assert.True(TimeProvider.System.GetElapsedTime(waiting) < TimeSpan.FromSeconds(30), $"The store kept the admission of the replaced ledger {replaced}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
        }
        foreach (var (key, kind, expected, payload) in Drain(_moved))
        {
            _ = await SendAsync(key, kind, expected, payload);
        }

        foreach (var (key, payload) in Drain(_ambiguous))
        {
            using var read = Assert.IsType<JournalReadResult>((await _journals.ReadAsync(JournalRowKey.Encode(JournalStoreSpec.Keys[key]), Token)).Value);
            _ = await SendAsync(key, JournalWriteKind.Append, read.CurrentVersion, payload);
        }

        return JournalTakenOver.Instance;
    }

    /// <summary>Runs one expiry scan, and reports each <c>Expired</c> it proposed with its outcome.</summary>
    public async Task<JournalScanAnswer> ScanAsync()
    {
        var proposals = new List<JournalAnswer>();
        _journals.OnExpiryProposed = (rowKey, expected) => outcome =>
        {
            var input = JournalInput.Expire(rowKey.ToString(), expected.ToString());
            var requested = false;
            var output = outcome is { } answered ? Output(answered, out requested) : JournalOutput.Ambiguous;
            lock (proposals)
            {
                proposals.Add(new JournalAnswer(input, output, requested));
            }
        };
        try
        {
            _ = await _journals.ExpireTombstonesAsync(Token);
        }
        finally
        {
            _journals.OnExpiryProposed = null;
        }

        lock (proposals)
        {
            return new JournalScanAnswer([.. proposals]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _store.Release();
        await _journals.DisposeAsync();
        await _ledgers.DisposeAsync();
        _memory.Dispose();
    }

    private static List<T> Drain<T>(List<T> items)
    {
        List<T> drained = [.. items];
        items.Clear();
        return drained;
    }

    /// <summary>Returns a write's outcome as <see cref="JournalModel"/> takes it, and the replace request an acceptance carries.</summary>
    private static JournalOutput Output(JournalWriteOutcome outcome, out bool replaceRequested)
    {
        replaceRequested = false;
        switch (outcome.Value)
        {
            case JournalWriteOutcome.Accepted accepted:
                replaceRequested = accepted.ReplaceRequested;
                return JournalOutput.Accepted(accepted.Version.ToString());
            case JournalWriteOutcome.Rejected rejected:
                return JournalOutput.Rejected(rejected.CurrentVersion.ToString());
            case JournalWriteOutcome.ReplaceRequired:
                return JournalOutput.ReplaceRequired;
            case JournalMoved:
                return JournalOutput.Moved;
            case JournalWriteOutcome.Fenced:
                return JournalOutput.Fenced;
            case JournalWriteOutcome.Ambiguous:
                return JournalOutput.Ambiguous;
            case null:
                throw new UnreachableException();
            default:
                throw new UnreachableException();
        }
    }

    private static async Task<JournalAnswer> AnswerAsync(JournalInput input, JournalReadResult result)
    {
        using (result)
        {
            var content = new byte[result.ContentLength];
            var length = 0;
            await foreach (var chunk in result.ReadPayloadsAsync(Token))
            {
                using (chunk)
                {
                    chunk.CopyTo(content.AsSpan(length));
                    length += chunk.Length;
                }
            }

            Assert.Equal(content.Length, length);
            return new JournalAnswer(input, JournalOutput.Read(result.Exists, result.CurrentVersion.ToString(), content), result.ReplaceRequested);
        }
    }

    /// <summary>Returns a version naming no state the store gives: <paramref name="version"/>'s base, far past its count.</summary>
    private static JournalVersion Unnamed(JournalVersion version) => new(version.Base, version.Count + 1_000);

    /// <summary>
    /// Converts a write's outcome to its answer, as the mutant changes it, and learns the version it reports.
    /// </summary>
    private JournalAnswer Answer(int key, JournalWriteKind kind, JournalVersion expected, byte[] payload, JournalInput input, JournalWriteOutcome outcome)
    {
        switch (outcome.Value)
        {
            case JournalWriteOutcome.Accepted accepted:
                var version = _mutant == JournalStoreMutant.AcceptedVersionNamesNoState ? Unnamed(accepted.Version) : accepted.Version;
                Learn(key, version);
                return new JournalAnswer(input, JournalOutput.Accepted(version.ToString()), accepted.ReplaceRequested);
            case JournalWriteOutcome.Rejected rejected:
                var current = _mutant == JournalStoreMutant.RejectionNamesNoState ? Unnamed(rejected.CurrentVersion) : rejected.CurrentVersion;
                Learn(key, current);
                Undecided(key, kind, payload);
                return new JournalAnswer(input, JournalOutput.Rejected(current.ToString()));
            case JournalWriteOutcome.ReplaceRequired:
                Undecided(key, kind, payload);
                return new JournalAnswer(input, JournalOutput.ReplaceRequired);
            case JournalMoved:
                Undecided(key, kind, payload);
                if (_mutant == JournalStoreMutant.MovedEntryLands)
                {
                    lock (_lock)
                    {
                        _moved.Add((key, kind, expected, payload));
                    }
                }

                return new JournalAnswer(input, JournalOutput.Moved);
            case JournalWriteOutcome.Fenced:
                Undecided(key, kind, payload);
                return new JournalAnswer(input, JournalOutput.Fenced);
            case JournalWriteOutcome.Ambiguous:
                if (_mutant == JournalStoreMutant.AmbiguousEntryWrittenTwice && kind == JournalWriteKind.Append)
                {
                    _ambiguous.Add((key, payload));
                }

                return new JournalAnswer(input, JournalOutput.Ambiguous);
            case null:
                throw new UnreachableException();
            default:
                throw new UnreachableException();
        }
    }

    private void Undecided(int key, JournalWriteKind kind, byte[] payload)
    {
        if (kind == JournalWriteKind.Append)
        {
            _undecided[key] = payload;
        }
    }

    private JournalVersion Resolve(int key, int back)
    {
        lock (_lock)
        {
            var learned = _learned[key];
            return learned[Math.Max(0, learned.Count - 1 - back)];
        }
    }

    private void Learn(int key, JournalVersion version)
    {
        lock (_lock)
        {
            if (_learned[key][^1] != version)
            {
                _learned[key].Add(version);
            }
        }
    }

    /// <summary>Encodes a write through the store and writes it, as the mutant changes it.</summary>
    private async Task<JournalWriteOutcome> SendAsync(int key, JournalWriteKind kind, JournalVersion expected, byte[] payload)
    {
        var rowKey = JournalRowKey.Encode(JournalStoreSpec.Keys[key]);
        if (_mutant == JournalStoreMutant.ReplaceFoldsAsAppend && kind == JournalWriteKind.Replace && payload.Length > 0)
        {
            kind = JournalWriteKind.Append;
        }

        JournalWriteOutcome outcome;
        using (var buffer = payload.Length == 0 ? default : ArcBuffer.Wrap(payload))
        using (var record = kind switch
        {
            JournalWriteKind.Append => _journals.EncodeAppend(rowKey, expected, buffer),
            JournalWriteKind.Replace => _journals.EncodeReplace(rowKey, expected, buffer),
            JournalWriteKind.Delete => _journals.EncodeDelete(rowKey, expected),
            _ => throw new UnreachableException(),
        })
        {
            outcome = await _journals.WriteAsync(record, Token);
        }

        if (_mutant == JournalStoreMutant.AcceptsAtStaleVersion
            && kind == JournalWriteKind.Append
            && outcome.Value is JournalWriteOutcome.Rejected rejected
            && rejected.CurrentVersion != expected)
        {
            return await SendAsync(key, kind, rejected.CurrentVersion, payload);
        }

        return outcome;
    }

    /// <summary>
    /// Holds the write's load at the archive, if it loads, replaces the journals' open ledger meanwhile, and then lets the
    /// load go on: the replaced admission's retirement answers the write <c>Fenced</c>, or, racing the registry's adoption
    /// of the successor's writer, <c>Moved</c>, or the store routes it again to the successor, which answers it.
    /// </summary>
    private async Task<JournalWriteOutcome> FencedAsync(int key, JournalWriteKind kind, JournalVersion expected, byte[] payload)
    {
        _store.HoldNextRead();
        var held = _store.ReadHeld;
        var write = SendAsync(key, kind, expected, payload);
        var loading = await Task.WhenAny(held, write) == held;
        if (!loading)
        {
            _store.Withdraw();
        }

        _ = await TakeOverAsync();
        _store.Release();
        var outcome = await write;
        if (loading)
        {
            await _store.HeldReadEnded;
        }

        if (_mutant == JournalStoreMutant.FencedEntryLands && outcome.Value is JournalWriteOutcome.Fenced)
        {
            _ = await SendAsync(key, kind, expected, payload);
        }

        return outcome;
    }

    /// <summary>
    /// Holds the answers of the write's batch once its replica stores it, and crashes the process meanwhile: disposes its
    /// writer registry, which leaves the write's outcome unknown, and starts the next process over the same replicas, which
    /// serves nothing until a takeover. A write answered before it reaches the replica meets the crash all the same.
    /// </summary>
    private async Task<JournalWriteOutcome> CrashingAsync(int key, JournalWriteKind kind, JournalVersion expected, byte[] payload)
    {
        var stored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ledgers.Transport.BeforeWriteAnswer = (_, _) =>
        {
            _ = stored.TrySetResult();
            return acknowledge.Task;
        };

        JournalWriteOutcome outcome;
        try
        {
            var write = SendAsync(key, kind, expected, payload);
            _ = await Task.WhenAny(stored.Task, write);
            await _ledgers.Registry.DisposeAsync();
            outcome = await write;
        }
        finally
        {
            _ledgers.Transport.BeforeWriteAnswer = null;
            _ = acknowledge.TrySetResult();
        }

        await _journals.DisposeAsync();
        _ledgers = await _ledgers.RestartAsync(Token);
        _journals = new JournalStore(_ledgers.Registry, _store, _options, TimeProvider.System);
        return outcome;
    }
}
