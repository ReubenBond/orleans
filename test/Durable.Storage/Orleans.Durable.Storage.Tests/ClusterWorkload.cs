using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// A journal workload over a cluster's routers, checked against its history (orleans.md §9, verification §10): clients
/// append to journals and read them back, each call through the router of a silo the scenario has not disrupted, and the
/// recorder keeps every invocation and answer. <see cref="AssertLinearizableAsync"/> reads every journal once more,
/// compares that read's version and content with the fold of the journal's history read through the archive's slow path,
/// checks that every answered read reports a version an acceptance reported, and judges the history against
/// <see cref="JournalModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each journal has one client, and each contended journal several, which write it concurrently through different
/// routers. A client writes only at a version an answer bound (the model's premise): after an acceptance it writes at the
/// accepted version, every other time without reading first, so a misreported version becomes a rejection or a
/// content mismatch; after any other answer it reads before it writes again.
/// </para>
/// <para>
/// The history binds what each answer asserts and nothing more. Every <c>Rejected</c> and <c>ReplaceRequired</c> is
/// recorded as a deposed owner's, a hint which binds nothing (<see cref="JournalOutput.Deposed"/>): a client of the routers
/// cannot tell whether the owner which answered was deposed, so the model checks only that the hint is a version the
/// journal held or a fresh one, other than the write's own. An <c>Ambiguous</c> write, and a write whose router failed,
/// may take effect at any later time; a read whose router failed was not answered. A failure of a router on a silo the
/// scenario did not disrupt fails the workload. The LSM keeps no per-key limit, and archive logs' layout refuses an
/// <c>Append</c> only past 40,000 units, which no scenario reaches, so <c>ReplaceRequired</c> fails the workload too
/// (warm state's §8.7 size hint on <c>Accepted</c> fires for append-only journals with no base, as it does on reads).
/// </para>
/// <para>
/// Every answered read must report <c>Origin</c>, a version an acceptance of its journal reported, or a rebased version,
/// a count of 0 (JS-I10), until the journal's first <c>Ambiguous</c> write is invoked, whose version no answer reports.
/// With the content, which the model checks, and the order, which the checker checks, that makes "every acknowledged
/// entry is present at its version" an observation of every read, not only of the final one.
/// </para>
/// <para>
/// The clients pace themselves, an operation each <see cref="Pace"/>, so a scenario's transitions run beside a steady load.
/// A stop lets each client finish the operation it is in, so every write is answered before the final reads.
/// </para>
/// </remarks>
/// <param name="keys">The journals, one client each.</param>
/// <param name="contended">The journals among <paramref name="keys"/> which <see cref="ContendingClients"/> clients write.</param>
internal sealed class ClusterWorkload(IReadOnlyList<string> keys, IReadOnlyCollection<string> contended) : IAsyncDisposable
{
    /// <summary>The time each client waits between its operations.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(10);

    /// <summary>The clients of each contended journal.</summary>
    public const int ContendingClients = 3;

    private readonly HistoryRecorder<JournalInput, JournalOutput> _history = new(TimeProvider.System);
    private readonly ConcurrentDictionary<string, long> _accepted = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Silo, string Key, Exception Failure)> _failures = new();
    private readonly ConcurrentQueue<string> _defects = new();
    private readonly ConcurrentQueue<(string Key, byte[] Payload)> _ambiguous = new();
    private readonly ConcurrentDictionary<int, string> _running = new();
    private readonly ConcurrentDictionary<string, bool> _paused = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _busy = new();
    private readonly Dictionary<string, byte[]> _finalContent = [with(StringComparer.Ordinal)];
    private readonly CancellationTokenSource _disposed = new();
    private Func<IReadOnlyList<(string Silo, JournalRouter Router)>> _routers = static () => [];
    private volatile bool _stopping = true;
    private volatile bool _readsAfterAcceptances = true;
    private Task _clients = Task.CompletedTask;
    private int _nextClient;

    /// <summary>Gets the journals.</summary>
    public IReadOnlyList<string> Keys { get; } = keys;

    /// <summary>Gets the silos the scenario disrupted: no client calls their routers, and their routers' failures are expected.</summary>
    public ConcurrentDictionary<string, bool> Disrupted { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the writes accepted so far, over every journal.</summary>
    public long Accepted => _accepted.Values.Sum();

    /// <summary>Gets each write recorded <c>Ambiguous</c> so far, its journal and payload.</summary>
    public IReadOnlyList<(string Key, byte[] Payload)> AmbiguousWrites => [.. _ambiguous];

    /// <summary>Gets each journal's content, as its final read answered it.</summary>
    public IReadOnlyDictionary<string, byte[]> FinalContent => _finalContent;

    /// <summary>Gets how many journals' final reads met a fold through the slow path which rebased.</summary>
    public int RebasedFinalReads { get; private set; }

    /// <summary>
    /// Gets or sets whether each client reads after every second acceptance, as it does by default; without, it reads only
    /// after an answer which binds no version, so a single client of a journal only writes while its writes are accepted,
    /// as while the archive store is unavailable, when a read of a journal with rows in the archive waits for the store.
    /// </summary>
    public bool ReadsAfterAcceptances
    {
        get => _readsAfterAcceptances;
        set => _readsAfterAcceptances = value;
    }

    /// <summary>Gets faults the scenario observes beside the workload, such as a drop out of lineage order, which fail it at once.</summary>
    public Func<IEnumerable<string>>? Faults { get; init; }

    /// <summary>Returns the writes accepted so far to <paramref name="key"/>.</summary>
    public long AcceptedOf(string key) => _accepted.GetValueOrDefault(key);

    /// <summary>
    /// Returns what the history records of a write's <paramref name="outcome"/>: its output, and whether its response
    /// time is unbounded, as an <c>Ambiguous</c> write's is, whose entry may be decided after the answer.
    /// </summary>
    internal static (JournalOutput Output, bool Unbounded) Record(JournalWriteOutcome outcome) => outcome switch
    {
        JournalWriteOutcome.Accepted accepted => (JournalOutput.Accepted(accepted.Version.ToString()), false),
        JournalWriteOutcome.Rejected rejected => (JournalOutput.Rejected(rejected.CurrentVersion.ToString(), deposed: true), false),
        JournalWriteOutcome.ReplaceRequired => (JournalOutput.ReplaceRequiredBy(deposed: true), false),
        JournalMoved => (JournalOutput.Moved, false),
        JournalWriteOutcome.Fenced => (JournalOutput.Fenced, false),
        JournalWriteOutcome.Ambiguous => (JournalOutput.Ambiguous, true),
        null => throw new UnreachableException(),
    };

    /// <summary>
    /// Asserts that every answered read of <paramref name="history"/> reports <c>Origin</c>, a version an acceptance of its
    /// journal reported, or a rebased version, a count of 0, unless it returned once its journal's first unbounded write
    /// was invoked: that write's version no answer need report, and a read it overlaps may see it.
    /// </summary>
    internal static void AssertReadsReportAcceptedVersions(IReadOnlyList<HistoryOperation<JournalInput, JournalOutput>> history)
    {
        foreach (var journal in history.GroupBy(static operation => operation.Input.Key, StringComparer.Ordinal))
        {
            var accepted = journal
                .Where(static operation => operation.Input.Kind != JournalOperationKind.Read && operation.Output.Outcome == JournalOutcome.Accepted)
                .Select(static operation => operation.Output.Version)
                .Append(JournalModel.Origin)
                .ToHashSet(StringComparer.Ordinal);
            var unbounded = journal
                .Where(static operation => operation.Input.Kind != JournalOperationKind.Read && operation.Return == HistoryOperation.Unbounded)
                .Select(static operation => operation.Call)
                .DefaultIfEmpty(long.MaxValue)
                .Min();
            foreach (var read in journal.Where(operation => operation.Input.Kind == JournalOperationKind.Read && operation.Output.Outcome == JournalOutcome.Accepted && operation.Return < unbounded))
            {
                var version = read.Output.Version;
                Assert.True(
                    accepted.Contains(version) || JournalVersion.TryParse(version, out var parsed) && parsed.Count == 0,
                    $"A read of '{journal.Key}' by client {read.ClientId} reported version {version}, which no acceptance of it reported and which is no rebase.");
            }
        }
    }

    /// <summary>
    /// Asserts that a journal's final read, its <paramref name="version"/>, existence, and <paramref name="content"/>,
    /// agrees with the fold of its history read through the slow path, its <paramref name="prefix"/> and live
    /// <paramref name="rows"/>, and returns whether the fold
    /// rebased. A fold which rebases names the state anew, <c>(prefix.VersionBase, 0)</c>, while its owner's warm state may
    /// still name it by its earlier version (journals.md §8.3); the read must then name it by the fold's version or by an
    /// earlier name, a count above 0, which the version check and the model pin as an accepted version and the current
    /// state's name. Otherwise the versions must be equal.
    /// </summary>
    internal static bool AssertFinalRead(string key, JournalVersion version, bool exists, byte[] content, ArchivePrefix? prefix, ReadOnlySpan<ArchiveRow> rows)
    {
        var folded = JournalState.Fold(prefix, rows);
        var foldedVersion = folded?.CurrentVersion ?? JournalVersion.Origin;
        var rebased = foldedVersion.Count == 0 && foldedVersion != JournalVersion.Origin;
        if (rebased)
        {
            Assert.True(version == foldedVersion || version.Count != 0, $"The final read of '{key}' names its rebased state {version}, neither the fold's {foldedVersion} nor an earlier name.");
        }
        else
        {
            Assert.Equal(foldedVersion, version);
        }

        Assert.Equal(folded?.Exists ?? false, exists);
        Assert.Equal(SlowPathContent(prefix, rows), content);
        return rebased;
    }

    /// <summary>
    /// Returns the content of a journal's history read through the archive's slow path: the prefix's content, then each
    /// live row the prefix does not hold, an <c>Append</c> after it, a <c>Replace</c> in its place, and a <c>Delete</c>
    /// emptying it (journals.md §8.3).
    /// </summary>
    internal static byte[] SlowPathContent(ArchivePrefix? prefix, ReadOnlySpan<ArchiveRow> rows)
    {
        var content = new List<byte>(prefix?.Content.ToArray() ?? []);
        foreach (var row in rows)
        {
            if (row.IsHeld)
            {
                continue;
            }

            if (row.Kind != RowKind.Append)
            {
                content.Clear();
            }

            if (row.Kind != RowKind.Delete)
            {
                content.AddRange(row.Body.ToArray());
            }
        }

        return [.. content];
    }

    /// <summary>Starts the clients, each a new client of the history, calling the routers <paramref name="routers"/> answers.</summary>
    /// <param name="routers">Answers the cluster's silos, each by name with its router.</param>
    public void Start(Func<IReadOnlyList<(string Silo, JournalRouter Router)>> routers)
    {
        Debug.Assert(_stopping, "The workload's clients are running.");
        _routers = routers;
        _stopping = false;
        var clients = new List<Task>();
        foreach (var key in Keys)
        {
            for (var count = contended.Contains(key) ? ContendingClients : 1; count > 0; count--)
            {
                var client = _nextClient++;
                clients.Add(Task.Run(() => RunAsync(client, key, _disposed.Token)));
            }
        }

        _clients = Task.WhenAll(clients);
    }

    /// <summary>
    /// Waits until every journal in <paramref name="keys"/>, by default every one, has <paramref name="more"/> more writes
    /// accepted than now.
    /// </summary>
    public async Task ProgressesAsync(int more, TimeSpan patience, IEnumerable<string>? keys = null)
    {
        var targets = (keys ?? Keys).ToDictionary(static key => key, key => AcceptedOf(key) + more, StringComparer.Ordinal);
        var started = TimeProvider.System.GetTimestamp();
        while (targets.Any(target => AcceptedOf(target.Key) < target.Value))
        {
            AssertNoUnexpectedFailure();
            if (_clients.IsCompleted)
            {
                await _clients;
                throw new InvalidOperationException("The workload's clients ended.");
            }

            if (TimeProvider.System.GetElapsedTime(started) > patience)
            {
                var behind = targets.Where(target => AcceptedOf(target.Key) < target.Value).Select(target => $"{target.Key} at {AcceptedOf(target.Key)} of {target.Value}");
                throw new TimeoutException($"The workload made too little progress within {patience}: {string.Join(", ", behind)}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, _disposed.Token);
        }
    }

    /// <summary>
    /// Pauses the clients of <paramref name="keys"/>: each finishes the operation it is in and starts no other until
    /// <see cref="Resume"/>; completes once none of them is in an operation, within <paramref name="patience"/>.
    /// </summary>
    public async Task PauseAsync(IEnumerable<string> keys, TimeSpan patience)
    {
        foreach (var key in keys)
        {
            _paused[key] = true;
        }

        var started = TimeProvider.System.GetTimestamp();
        while (_busy.Values.Any(_paused.ContainsKey))
        {
            if (TimeProvider.System.GetElapsedTime(started) > patience)
            {
                throw new TimeoutException($"The paused journals' clients did not finish their operations within {patience}: {string.Join(", ", _busy.Where(busy => _paused.ContainsKey(busy.Value)).Select(busy => $"client {busy.Key} in {_running.GetValueOrDefault(busy.Key)}"))}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, _disposed.Token);
        }
    }

    /// <summary>Resumes every paused client.</summary>
    public void Resume() => _paused.Clear();

    /// <summary>Stops every client once it finishes the operation it is in, within <paramref name="patience"/>.</summary>
    public async Task StopAsync(TimeSpan patience)
    {
        _stopping = true;
        try
        {
            await _clients.WaitAsync(patience, _disposed.Token);
        }
        catch (TimeoutException timeout)
        {
            var running = string.Join(", ", _running.OrderBy(static client => client.Key).Select(static client => $"client {client.Key} in {client.Value}"));
            throw new TimeoutException($"The workload's clients did not stop within {patience}: {running}.", timeout);
        }

        AssertNoUnexpectedFailure();
    }

    /// <summary>
    /// Once every client stopped, reads every journal once more through <paramref name="router"/>; checks each read's
    /// version, existence, and content against the fold of the journal's history, which <paramref name="slowPath"/> reads
    /// through the archive's slow path; checks that every journal's history holds an acceptance and its final read, and
    /// that every answered read reports a version an acceptance reported; and judges every journal's history against
    /// <see cref="JournalModel"/>.
    /// </summary>
    /// <param name="router">A router of a silo the scenario did not disrupt.</param>
    /// <param name="slowPath">Reads a journal's history through the archive's slow path; the caller owns the read.</param>
    /// <param name="patience">Bounds each final read and slow-path read.</param>
    /// <param name="cancellationToken">Ends the reads.</param>
    public async Task AssertLinearizableAsync(JournalRouter router, Func<JournalRowKey, ValueTask<ArchiveRead>> slowPath, TimeSpan patience, CancellationToken cancellationToken)
    {
        Debug.Assert(_stopping && _clients.IsCompleted, "The workload's clients are running.");
        var finalClients = _nextClient;
        var client = finalClients;
        foreach (var key in Keys)
        {
            var rowKey = JournalRowKey.Encode(key);
            var invocation = _history.Invoke(client++, JournalInput.Read(key));
            var read = await Bounded(router.ReadAsync(key, rowKey, cancellationToken), $"The final read of '{key}'", patience, cancellationToken);
            using (read.Content)
            {
                var content = Bytes(read);
                _history.Complete(invocation, JournalOutput.Read(read.Exists, read.Version.ToString(), content));
                _finalContent[key] = content;
                using var history = await Bounded(slowPath(rowKey).AsTask(), $"The slow-path read of '{key}'", patience, cancellationToken);
                RebasedFinalReads += AssertFinalRead(key, read.Version, read.Exists, content, history.Prefix, history.Rows) ? 1 : 0;
            }
        }

        var operations = _history.History(static input => input.Kind == JournalOperationKind.Read ? JournalOutput.Moved : JournalOutput.Ambiguous);
        foreach (var key in Keys)
        {
            Assert.True(
                operations.Any(operation => operation.Input.Key == key && operation.Input.Kind == JournalOperationKind.Append && operation.Output.Outcome == JournalOutcome.Accepted),
                $"The history of '{key}' holds no accepted write.");
            Assert.True(
                operations.Any(operation => operation.Input.Key == key && operation.ClientId >= finalClients && operation.Output.Outcome == JournalOutcome.Accepted),
                $"The history of '{key}' lacks its final read.");
        }

        AssertReadsReportAcceptedVersions(operations);
        LinearizabilityAssert.Linearizable(JournalModel.Instance, operations, cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        await _disposed.CancelAsync();
        await _clients.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _disposed.Dispose();
    }

    private static async Task<T> Bounded<T>(Task<T> task, string what, TimeSpan patience, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(patience, cancellationToken);
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException($"{what} did not complete within {patience}.", timeout);
        }
    }

    private void AssertNoUnexpectedFailure()
    {
        Assert.True(_defects.IsEmpty, string.Join(Environment.NewLine, _defects));
        var faults = Faults?.Invoke().ToList() ?? [];
        Assert.True(faults.Count == 0, string.Join(Environment.NewLine, faults));
        var unexpected = _failures.Where(failure => !Disrupted.ContainsKey(failure.Silo)).ToList();
        Assert.True(unexpected.Count == 0, string.Join(Environment.NewLine, unexpected.Select(static failure => $"{failure.Silo}, {failure.Key}: {failure.Failure}")));
    }

    /// <summary>
    /// Runs one client of <paramref name="key"/>: it reads to bind a version, then writes at the version its last answer
    /// bound, reading after every second acceptance and after every other answer.
    /// </summary>
    private async Task RunAsync(int client, string key, CancellationToken cancellationToken)
    {
        var rowKey = JournalRowKey.Encode(key);
        JournalVersion? expected = null;
        var (readNext, accepts, operation) = (false, 0, 0);
        try
        {
            while (!_stopping)
            {
                // Marked busy before the pause is checked, so a pause which waits for no busy client misses no operation.
                _busy[client] = key;
                if (_paused.ContainsKey(key))
                {
                    _ = _busy.TryRemove(client, out _);
                    _running[client] = "its pause";
                    await Task.Delay(Pace, TimeProvider.System, cancellationToken);
                    continue;
                }

                if (expected is { } version && !readNext)
                {
                    _running[client] = $"a write of '{key}'";
                    expected = await WriteAsync(client, key, rowKey, version, operation++, cancellationToken);
                    readNext = expected is not null && ++accepts % 2 == 0 && _readsAfterAcceptances;
                }
                else
                {
                    _running[client] = $"a read of '{key}'";
                    expected = await ReadAsync(client, key, rowKey, operation++, cancellationToken);
                    readNext = false;
                }

                _ = _busy.TryRemove(client, out _);
                _running[client] = "its pause";
                await Task.Delay(Pace, TimeProvider.System, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _ = _busy.TryRemove(client, out _);
            _ = _running.TryRemove(client, out _);
        }
    }

    /// <summary>Appends once at <paramref name="expected"/>; answers the accepted version, or null after any other answer.</summary>
    private async Task<JournalVersion?> WriteAsync(int client, string key, JournalRowKey rowKey, JournalVersion expected, int operation, CancellationToken cancellationToken)
    {
        if (Pick(client, operation) is not var (silo, router))
        {
            return expected;
        }

        var body = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{key}#{client}.{operation};"));
        var invocation = _history.Invoke(client, JournalInput.Append(key, expected.ToString(), body), silo);
        using var payload = Payload(body);
        using var record = JournalEntryRecord.Append(rowKey, expected, payload);
        JournalWriteOutcome outcome;
        try
        {
            outcome = await router.WriteAsync(key, rowKey, record, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            if (!Disrupted.ContainsKey(silo))
            {
                _failures.Enqueue((silo, key, failure));
            }

            _ambiguous.Enqueue((key, body));
            _history.CompleteUnbounded(invocation, JournalOutput.Ambiguous);
            return null;
        }

        var (output, unbounded) = Record(outcome);
        if (unbounded)
        {
            _ambiguous.Enqueue((key, body));
            _history.CompleteUnbounded(invocation, output);
            return null;
        }

        _history.Complete(invocation, output);
        if (outcome.Value is JournalWriteOutcome.ReplaceRequired)
        {
            _defects.Enqueue($"An append to '{key}' through {silo} received ReplaceRequired, which no scenario's store reaches: {outcome.Value}.");
        }

        if (outcome.Value is not JournalWriteOutcome.Accepted accepted)
        {
            return null;
        }

        _ = _accepted.AddOrUpdate(key, 1, static (_, count) => count + 1);
        return accepted.Version;
    }

    /// <summary>Reads the journal once; answers its version, if the read was answered.</summary>
    private async Task<JournalVersion?> ReadAsync(int client, string key, JournalRowKey rowKey, int operation, CancellationToken cancellationToken)
    {
        if (Pick(client, operation) is not var (silo, router))
        {
            return null;
        }

        var invocation = _history.Invoke(client, JournalInput.Read(key), silo);
        RoutedRead read;
        try
        {
            read = await router.ReadAsync(key, rowKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            if (!Disrupted.ContainsKey(silo))
            {
                _failures.Enqueue((silo, key, failure));
            }

            _history.Complete(invocation, JournalOutput.Moved);
            return null;
        }

        using (read.Content)
        {
            _history.Complete(invocation, JournalOutput.Read(read.Exists, read.Version.ToString(), Bytes(read)));
            return read.Version;
        }
    }

    /// <summary>Picks the router of a silo the scenario has not disrupted, rotating each client's operations over them.</summary>
    private (string Silo, JournalRouter Router)? Pick(int client, int operation)
    {
        var routers = _routers().Where(entry => !Disrupted.ContainsKey(entry.Silo)).ToArray();
        return routers.Length == 0 ? null : routers[(client + operation) % routers.Length];
    }

    private static ArcBuffer Payload(ReadOnlySpan<byte> body)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(body);
        return writer.ConsumeSlice(body.Length);
    }

    private static byte[] Bytes(RoutedRead read)
    {
        var content = read.Content.Reader.PeekSlice(read.Content.Length);
        try
        {
            return content.ToArray();
        }
        finally
        {
            content.Dispose();
        }
    }
}
