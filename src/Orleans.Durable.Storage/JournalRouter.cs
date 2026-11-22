using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Threading;
using Orleans.Runtime;
using Polly;
using Polly.Retry;

namespace Orleans.Durable.Storage;

/// <summary>
/// Sends each journal's entries and reads to the primary of the key's open ledger in the silo's map view (orleans.md
/// §5.5): to the silo's own Journals when its node is the primary, and otherwise through the primary node's
/// <see cref="IJournalStoreGrain"/>, each forwarded write and each first read page waiting in its own lane to that node
/// (<see cref="ForwardingBudgets"/>), so a read's backpressure never delays a write. It resends an entry until an outcome
/// settles it or its caller's cancellation ends the wait, with jittered exponential backoff, the version check making each
/// resend safe (JS-I1).
/// </summary>
/// <remarks>
/// <para>
/// A write settles only as <c>Accepted</c>, <c>Rejected</c>, <c>ReplaceRequired</c>, or <c>Ambiguous</c> (OR-I7). It is
/// resent on the answers which report it without effect: <c>Moved</c>, to the owner it names, or after a backoff and a map
/// re-read when it names none or the node it was sent to; <c>Fenced</c>, after a map re-read; <c>Throttled</c>, to the same
/// primary after a backoff; <c>Unavailable</c>, the gate's answer for the Journals'
/// <see cref="JournalStoreUnavailableException"/>, an entry never proposed, after a map re-read; and <c>NotThisNode</c>, a placement refusal, the Orleans runtime's own overload rejection, and
/// <c>ForwardQueueTimeout</c>, the first two after a registry and a map re-read. A resend goes at once only to follow a named
/// owner, or a read's <c>Expired</c>, and never twice in a row: the next backs off and re-reads the map, so two nodes whose
/// views name each other cost a backoff per pair of sends. Both paths reach the Journals through the node's gate, which
/// answers a throttled primary's write <c>Throttled</c> before them.
/// </para>
/// <para>
/// Only a failure before the send proves a write had no effect: the silo's own Journals' refusal before admission,
/// <see cref="ArgumentException"/>, is thrown as it is, and the owner's refusals, which travel in its answer, as their
/// kind's exception; any other failure of the silo's own Journals, an <see cref="InvalidDataException"/> among them, which
/// they do not promise before admission, settles the write <c>Ambiguous</c>. Corruption is thrown as it is, and the caller's
/// cancellation as <see cref="OperationCanceledException"/>. Every other failure of a send, an exception the remote call
/// ended with or a reply the router fails to decode among them, settles the write <c>Ambiguous</c>, unless it is Orleans'
/// own refusal of the call, matched at the top level or as the direct inner exception of its message rejection: the router
/// never infers from versions whether such a write applied. The match relies on the callee: the owner's host never ends a
/// call with <see cref="LimitExceededException"/> or <see cref="NodePlacementRefusedException"/>, since its replica calls
/// convert both (<see cref="JournalNodeHost"/>).
/// </para>
/// <para>
/// A read has no effect beyond a pin on the owner, which a release or its window ends, so it is read again on every
/// failure of its calls but the caller's cancellation, corruption, and a reply the router fails to decode: a lane's
/// timeout and an overload rejection after a backoff, every other failure after a registry and a map re-read. A read of
/// the silo's own Journals is read again after a backoff on every failure but the caller's cancellation, corruption,
/// invalid data, their refusal of the key, and their disposal, so a read which meets the archive store's unavailability
/// waits for the store, as a forwarded one does. It follows
/// <c>Moved</c> and <c>NotThisNode</c> as a write does, and <c>Busy</c> after a backoff, to the same owner. It reads a
/// forwarded journal's later pages one at a time, each once the previous one has arrived and each exactly
/// <c>min(PageBytes, ContentLength − offset)</c> long, and reads it again from the start when the owner answers
/// <c>Expired</c>. It gathers the journal's content before it returns, so a restarted read has delivered nothing; a read
/// which ends while its snapshot is pinned releases it (<see cref="IJournalStoreGrain.ReleaseSnapshotAsync"/>).
/// </para>
/// <para>
/// A map re-read which meets a value that is no ledger map throws it as corruption. One which meets the silo's stopped
/// runtime ends the call: a write with the gate's stopped answer, <c>Moved</c> naming no owner, since every send before it
/// had no effect, and a read with <see cref="ObjectDisposedException"/>. Every other failure of it, the register's
/// unavailability among them, is read again after a backoff. A registry re-read is requested as the answer which asks for
/// it is classified, so it overlaps the backoff.
/// </para>
/// </remarks>
internal sealed partial class JournalRouter
{
    private static readonly JournalWriteOutcome AmbiguousOutcome = new(JournalWriteOutcome.Ambiguous.Instance);

    private readonly LocalRouting _routing;
    private readonly LocalJournals _journals;
    private readonly Action _rereadRegistry;
    private readonly ForwardingBudgets _budgets;
    private readonly ReplicaCallLimits _limits;
    private readonly Func<NodeId, IJournalStoreGrain> _grains;
    private readonly ILogger _logger;
    private readonly ResiliencePipeline<Attempt<JournalWriteOutcome>> _writes;
    private readonly ResiliencePipeline<Attempt<RoutedRead>> _reads;

    /// <summary>Initializes a new instance of the <see cref="JournalRouter"/> class.</summary>
    /// <param name="routing">The silo's node, map view, and archive store, once its runtime published them.</param>
    /// <param name="journals">The silo's own Journals, through their gate.</param>
    /// <param name="rereadRegistry">Asks the silo's registry resolver to re-read, as a <c>NotThisNode</c> or placement refusal does.</param>
    /// <param name="budgets">The forwarding budgets of the silo's destination nodes.</param>
    /// <param name="limits">The replica call limits, whose deadline each forwarded call has.</param>
    /// <param name="grains">Returns a node's <see cref="IJournalStoreGrain"/>.</param>
    /// <param name="timeProvider">Times the backoff.</param>
    /// <param name="logger">Logs the failures which settle a write <c>Ambiguous</c> or which the router resends.</param>
    /// <param name="backoff">The first and longest resend delays; by default 10 milliseconds and 1 second.</param>
    public JournalRouter(
        LocalRouting routing,
        LocalJournals journals,
        Action rereadRegistry,
        ForwardingBudgets budgets,
        ReplicaCallLimits limits,
        Func<NodeId, IJournalStoreGrain> grains,
        TimeProvider timeProvider,
        ILogger<JournalRouter> logger,
        (TimeSpan First, TimeSpan Longest)? backoff = null)
    {
        _routing = routing;
        _journals = journals;
        _rereadRegistry = rereadRegistry;
        _budgets = budgets;
        _limits = limits;
        _grains = grains;
        _logger = logger;
        var (first, longest) = backoff ?? (TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
        _writes = Pipeline<JournalWriteOutcome>(timeProvider, first, longest);
        _reads = Pipeline<RoutedRead>(timeProvider, first, longest);
    }

    /// <summary>Gets the forwarding budgets, which tests read.</summary>
    internal ForwardingBudgets Budgets => _budgets;

    /// <summary>
    /// Writes <paramref name="record"/>, an entry of the journal <paramref name="key"/> whose row key is
    /// <paramref name="rowKey"/>, resending it until an outcome settles it: <c>Accepted</c>, <c>Rejected</c>,
    /// <c>ReplaceRequired</c>, or <c>Ambiguous</c>; or, once the silo's runtime stopped, the gate's stopped answer,
    /// <c>Moved</c> naming no owner, without effect. The caller keeps the record until the call completes.
    /// </summary>
    /// <exception cref="ArgumentException">The silo's own Journals refused the record before admission, without effect.</exception>
    /// <exception cref="InvalidDataException">
    /// The owner refused the record as invalid in its answer (JS-I5), without effect; or the register holds a value which is
    /// no ledger map.
    /// </exception>
    /// <exception cref="JournalStoreCorruptException">The journal's history contradicts itself.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled; an entry already sent may still be decided.</exception>
    public async Task<JournalWriteOutcome> WriteAsync(string key, JournalRowKey rowKey, JournalEntryRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        Debug.Assert(record.Key.Span.SequenceEqual(rowKey.Span), "The record is an entry of the journal it is routed by.");
        return await RouteAsync(_writes, new WriteState(key, rowKey, record), static (router, state, target, node, cancellationToken) => router.WriteOnceAsync(state, target, node, cancellationToken), StoppedWrite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the journal <paramref name="key"/>, whose row key is <paramref name="rowKey"/>, from its primary: its version,
    /// existence, replace request, and content, gathered in Orleans' page pool, which the caller owns and disposes
    /// (orleans.md §5.5).
    /// </summary>
    /// <exception cref="ArgumentException">The owner refused the read's key.</exception>
    /// <exception cref="InvalidDataException">
    /// The owner's reply broke the forwarded read's rules, or failed to decode; or the register holds a value which is no
    /// ledger map.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The silo's runtime stopped.</exception>
    /// <exception cref="JournalStoreCorruptException">The journal's history contradicts itself.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<RoutedRead> ReadAsync(string key, JournalRowKey rowKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return await RouteAsync(_reads, new RouteState(key, rowKey), static (router, state, target, node, cancellationToken) => router.ReadOnceAsync(state, target, node, cancellationToken), stopped: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The resend pipeline: unbounded attempts, each resend at once when it asks to go so, and otherwise after an
    /// exponential backoff from <paramref name="first"/>, at most <paramref name="longest"/>, with equal jitter: between half
    /// of it and all of it, so a backoff never shrinks toward nothing; the caller's cancellation ends it.
    /// </summary>
    private static ResiliencePipeline<Attempt<T>> Pipeline<T>(TimeProvider timeProvider, TimeSpan first, TimeSpan longest) =>
        new ResiliencePipelineBuilder<Attempt<T>> { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions<Attempt<T>>
            {
                ShouldHandle = static arguments => ValueTask.FromResult(arguments.Outcome.Result is { Resend: not null }),
                MaxRetryAttempts = int.MaxValue,
                Delay = first,
                MaxDelay = longest,
                DelayGenerator = arguments => ValueTask.FromResult<TimeSpan?>(arguments.Outcome.Result is { Resend.Immediately: true } ? TimeSpan.Zero : Backoff(arguments.AttemptNumber, first, longest, Random.Shared.NextDouble())),
            })
            .Build();

    /// <summary>
    /// Returns the backoff before resend <paramref name="attempt"/>: the exponential ceiling,
    /// <c>min(first × 2^attempt, longest)</c>, times between a half and one as <paramref name="random"/>, in [0, 1), goes.
    /// </summary>
    /// <remarks>
    /// Polly's decorrelated jitter was rejected: the difference of two successive draws can be microseconds, so a resend
    /// it backs off from goes nearly at once.
    /// </remarks>
    internal static TimeSpan Backoff(int attempt, TimeSpan first, TimeSpan longest, double random)
    {
        var ceiling = TimeSpan.FromTicks((long)Math.Min(first.Ticks * Math.Pow(2, Math.Min(attempt, 30)), longest.Ticks));
        return ceiling * (0.5 + random / 2);
    }

    private static bool IsOwnCancellation(Exception failure, CancellationToken cancellationToken) =>
        failure is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static bool Has<T>(Exception failure)
        where T : Exception => Chain(failure).OfType<T>().Any();

    private static IEnumerable<Exception> Chain(Exception failure)
    {
        for (Exception? next = failure; next is not null; next = next.InnerException)
        {
            yield return next;
            if (next is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }

                yield break;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> until it settles, resending as each answer asks; once the silo's runtime stopped, a
    /// map re-read settles it with <paramref name="stopped"/>, or throws when there is none.
    /// </summary>
    private async Task<T> RouteAsync<TState, T>(
        ResiliencePipeline<Attempt<T>> pipeline,
        TState state,
        Func<JournalRouter, TState, NodeId, NodeId, CancellationToken, Task<Attempt<T>>> attempt,
        Func<T>? stopped,
        CancellationToken cancellationToken)
        where TState : RouteState
    {
        var settled = await pipeline.ExecuteAsync(
            static async (context, cancellationToken) =>
            {
                var (router, state, attempt, stopped) = context;
                var resend = state.Pending;
                state.Pending = null;
                if (resend is not null)
                {
                    LogResend(router._logger, state.Key, resend.To, resend.Immediately, resend.RereadMap, resend.RereadRegistry, state.Reason);
                }

                var routing = await router._routing.ReadyAsync(cancellationToken).ConfigureAwait(false);
                if (resend is { RereadMap: true })
                {
                    try
                    {
                        _ = await routing.View.ReadAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) when (stopped is not null)
                    {
                        // The silo's runtime stopped: every send so far had no effect, so the write ends with the gate's
                        // stopped answer.
                        return Attempt<T>.Of(stopped());
                    }
                    catch (Exception failure) when (failure is not (InvalidDataException or ObjectDisposedException) && !IsOwnCancellation(failure, cancellationToken))
                    {
                        // The register did not answer: back off and read it again before the next send. A value which is
                        // no ledger map is corruption, and a stopped runtime ends a read, each ending the call.
                        LogMapUnread(router._logger, state.Key, failure);
                        return state.Resend<T>(new Resend(RereadMap: true), "an unread map");
                    }
                }

                var target = resend?.To ?? routing.View.FindOpen(state.RowKey.Span)?.Primary.Node;
                if (target is not { } node)
                {
                    // The view lists no open ledger over the key, as before bootstrap.
                    return state.Resend<T>(new Resend(RereadMap: true), "no open ledger");
                }

                var settledOrResent = await attempt(router, state, node, routing.Node, cancellationToken).ConfigureAwait(false);
                if (settledOrResent.Resend is { RereadRegistry: true })
                {
                    // The resolver re-reads in the background, so the re-read overlaps the backoff.
                    router._rereadRegistry();
                }

                return settledOrResent;
            },
            (this, state, attempt, stopped),
            cancellationToken).ConfigureAwait(false);
        return settled.Settled;
    }

    /// <summary>Sends the write once, to the silo's own Journals or to its target's grain, and classifies the answer.</summary>
    private async Task<Attempt<JournalWriteOutcome>> WriteOnceAsync(WriteState write, NodeId target, NodeId node, CancellationToken cancellationToken)
    {
        JournalForwardedWriteOutcome outcome;
        if (target == node)
        {
            try
            {
                outcome = Forwarded(await _journals.WriteAsync(write.Record, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception failure) when (!IsOwnCancellation(failure, cancellationToken) && !Has<JournalStoreCorruptException>(failure) && failure is not ArgumentException)
            {
                // ArgumentException is the Journals' refusal before admission, without effect; every other failure may
                // follow it.
                return Ambiguous(write, target, failure);
            }
        }
        else
        {
            var lane = _budgets.Writes(target);
            CallBudget.Lease lease;
            try
            {
                lease = await lane.EnterLeaseAsync(write.Record.Length, lane.DeadlineAfter(_limits.Deadline), cancellationToken).ConfigureAwait(false);
            }
            catch (JournalForwardQueueTimeoutException)
            {
                return write.Resend<JournalWriteOutcome>(new Resend(), nameof(JournalForwardQueueTimeoutException));
            }

            using (lease)
            {
                // The sender copy lives only while its grant does, so a write waiting out a backoff or in a lane holds none.
                using var wire = WireBuffers.CopyToOrleans(write.Record);
                try
                {
                    outcome = await _grains(target).WriteAsync(new JournalForwardedEntry { Key = write.Key, Record = wire }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure) when (!IsOwnCancellation(failure, cancellationToken) && !Has<JournalStoreCorruptException>(failure))
                {
                    // The owner's refusals travel in its answer, so only Orleans' own refusal of the call proves it had no
                    // effect; every other failure of the send leaves the write's outcome unknown.
                    return NoEffect(failure) is { } resend ? write.Resend<JournalWriteOutcome>(resend, failure.GetType().Name) : Ambiguous(write, target, failure);
                }
            }
        }

        return outcome switch
        {
            JournalWriteOutcome.Accepted accepted => Attempt<JournalWriteOutcome>.Of(new JournalWriteOutcome(accepted)),
            JournalWriteOutcome.Rejected rejected => Attempt<JournalWriteOutcome>.Of(new JournalWriteOutcome(rejected)),
            JournalWriteOutcome.ReplaceRequired replaceRequired => Attempt<JournalWriteOutcome>.Of(new JournalWriteOutcome(replaceRequired)),
            JournalWriteOutcome.Ambiguous ambiguous => Attempt<JournalWriteOutcome>.Of(new JournalWriteOutcome(ambiguous)),
            JournalMoved moved => write.Resend<JournalWriteOutcome>(Follow(moved, target, node), nameof(JournalMoved)),
            JournalWriteOutcome.Fenced => write.Resend<JournalWriteOutcome>(new Resend(RereadMap: true), nameof(JournalWriteOutcome.Fenced)),
            LocalJournalWriteOutcome.Throttled => write.Resend<JournalWriteOutcome>(new Resend(To: target), nameof(LocalJournalWriteOutcome.Throttled)),
            LocalJournalWriteOutcome.Unavailable => write.Resend<JournalWriteOutcome>(new Resend(RereadMap: true), nameof(LocalJournalWriteOutcome.Unavailable)),
            NotThisNode => write.Resend<JournalWriteOutcome>(new Resend(RereadMap: true, RereadRegistry: true), nameof(NotThisNode)),
            JournalCallRefused refused => throw Refusal(refused, target),
            null => throw new UnreachableException(),
        };
    }

    private static JournalWriteOutcome StoppedWrite() => new(LocalJournals.Stopped);

    private Attempt<JournalWriteOutcome> Ambiguous(WriteState write, NodeId target, Exception failure)
    {
        LogAmbiguous(_logger, write.Key, target, failure);
        return Attempt<JournalWriteOutcome>.Of(AmbiguousOutcome);
    }

    /// <summary>Returns the exception the owner's refusal is reported as, its kind's, which asserts no effect.</summary>
    private static Exception Refusal(JournalCallRefused refused, NodeId owner) => refused.Kind switch
    {
        JournalRefusal.Argument => new ArgumentException($"Node {owner} refused the call: {refused.Message}"),
        JournalRefusal.Invalid => new InvalidDataException($"Node {owner} refused the record: {refused.Message}"),
        _ => throw new UnreachableException(),
    };

    /// <summary>Reads the journal once, from the silo's own Journals or through its target's grain, and classifies the answer.</summary>
    private Task<Attempt<RoutedRead>> ReadOnceAsync(RouteState read, NodeId target, NodeId node, CancellationToken cancellationToken) =>
        target == node ? ReadLocallyAsync(read, node, cancellationToken) : ReadRemotelyAsync(read, target, node, cancellationToken);

    private async Task<Attempt<RoutedRead>> ReadLocallyAsync(RouteState read, NodeId node, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await _journals.ReadAsync(read.RowKey, cancellationToken).ConfigureAwait(false);
            if (outcome.Value is JournalMoved moved)
            {
                return read.Resend<RoutedRead>(Follow(moved, node, node), nameof(JournalMoved));
            }

            using var result = outcome.Value as JournalReadResult ?? throw new UnreachableException();
            OrleansBufferWriter? content = new();
            try
            {
                await foreach (var chunk in result.ReadPayloadsAsync(cancellationToken).ConfigureAwait(false))
                {
                    using (chunk)
                    {
                        foreach (var segment in chunk)
                        {
                            content.Write(segment);
                        }
                    }
                }

                var routed = new RoutedRead(result.CurrentVersion, result.Exists, result.ReplaceRequested, content);
                content = null;
                return Attempt<RoutedRead>.Of(routed);
            }
            finally
            {
                content?.Dispose();
            }
        }
        catch (Exception failure) when (ReadLocallyAgain(failure, cancellationToken))
        {
            LogReadResent(_logger, read.Key, node, failure);
            return read.Resend<RoutedRead>(new Resend(), failure.GetType().Name);
        }
    }

    /// <summary>
    /// Returns whether a failure of a read of the silo's own Journals is read again after a backoff, as a remote read's is:
    /// every failure but the caller's cancellation, corruption, invalid data, the Journals' refusal of the key
    /// (<see cref="ArgumentException"/>), and their disposal, a stopped runtime's. The archive store's unavailability, which
    /// a history read meets while a journal's rows lie in it (journals.md §8.2), is read again until the store answers.
    /// </summary>
    private static bool ReadLocallyAgain(Exception failure, CancellationToken cancellationToken) =>
        !IsOwnCancellation(failure, cancellationToken)
        && !Has<JournalStoreCorruptException>(failure)
        && !Has<ArchiveCorruptException>(failure)
        && !Has<InvalidDataException>(failure)
        && failure is not (ArgumentException or ObjectDisposedException or OutOfMemoryException);

    private async Task<Attempt<RoutedRead>> ReadRemotelyAsync(RouteState read, NodeId target, NodeId node, CancellationToken cancellationToken)
    {
        var grain = _grains(target);
        JournalForwardedRead first;
        try
        {
            var lane = _budgets.Reads(target);
            using (await lane.EnterLeaseAsync(0, lane.DeadlineAfter(_limits.Deadline), cancellationToken).ConfigureAwait(false))
            {
                first = await grain.ReadAsync(read.Key, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (ReadAgain(failure, cancellationToken) is { } resend)
        {
            LogReadResent(_logger, read.Key, target, failure);
            return read.Resend<RoutedRead>(resend, failure.GetType().Name);
        }

        return first switch
        {
            JournalForwardedRead.Found found => await GatherAsync(read, grain, target, found, cancellationToken).ConfigureAwait(false),
            JournalMoved moved => read.Resend<RoutedRead>(Follow(moved, target, node), nameof(JournalMoved)),
            JournalForwardedRead.Busy => read.Resend<RoutedRead>(new Resend(To: target), nameof(JournalForwardedRead.Busy)),
            NotThisNode => read.Resend<RoutedRead>(new Resend(RereadMap: true, RereadRegistry: true), nameof(NotThisNode)),
            JournalCallRefused refused => throw Refusal(refused, target),
            null => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Gathers <paramref name="found"/>'s content in Orleans' page pool: its first page, and its snapshot's later pages in
    /// order, each exactly <c>min(PageBytes, ContentLength − offset)</c> long. A read which ends before its last page while
    /// its snapshot is pinned, by a failure or its caller's cancellation, releases the snapshot.
    /// </summary>
    private async Task<Attempt<RoutedRead>> GatherAsync(RouteState read, IJournalStoreGrain grain, NodeId target, JournalForwardedRead.Found found, CancellationToken cancellationToken)
    {
        var page = found.FirstPage;
        var pinned = found.Snapshot;
        OrleansBufferWriter? content = new();
        try
        {
            try
            {
                CheckFirstPage(found, target);
                if (pinned is null)
                {
                    // The whole journal arrived in one page: the content is the received buffer itself.
                    content.AppendPinned(page);
                }
                else
                {
                    content.Write(page.AsReadOnlySequence());
                }
            }
            finally
            {
                page.Dispose();
            }

            while (pinned is { } snapshot && content.Length < found.ContentLength)
            {
                var offset = content.Length;
                JournalForwardedPage next;
                try
                {
                    next = await grain.ReadPageAsync(snapshot, offset, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure) when (ReadAgain(failure, cancellationToken) is { } resend)
                {
                    LogReadResent(_logger, read.Key, target, failure);
                    return read.Resend<RoutedRead>(resend, failure.GetType().Name);
                }

                switch (next.Value)
                {
                    case JournalForwardedPage.Page later:
                        Append(content, later.Bytes, offset, found.ContentLength, target);
                        break;
                    case JournalForwardedPage.Expired:
                        pinned = null;
                        return read.Resend<RoutedRead>(new Resend(To: target, Immediately: true), nameof(JournalForwardedPage.Expired));
                    case NotThisNode:
                        // The activation which answered is not on the snapshot's node, so a release would not reach it;
                        // the snapshot's window ends its reservation.
                        pinned = null;
                        return read.Resend<RoutedRead>(new Resend(RereadMap: true, RereadRegistry: true), nameof(NotThisNode));
                    case JournalCallRefused refused:
                        throw new InvalidDataException($"Node {target} refused a page at {offset} of a journal of {found.ContentLength} bytes: {refused.Message}");
                    default:
                        throw new UnreachableException();
                }
            }

            // The last page released the snapshot.
            pinned = null;
            var routed = new RoutedRead(found.Version, found.Exists, found.ReplaceRequested, content);
            content = null;
            return Attempt<RoutedRead>.Of(routed);
        }
        finally
        {
            content?.Dispose();
            if (pinned is { } abandoned)
            {
                Release(grain, abandoned, target);
            }
        }
    }

    /// <summary>
    /// Refuses a first page which breaks the forwarded read's rules: a snapshot exactly when more pages follow, and a first
    /// page of <c>min(PageBytes, ContentLength)</c> bytes.
    /// </summary>
    private static void CheckFirstPage(JournalForwardedRead.Found found, NodeId owner)
    {
        var pinned = found.Snapshot is not null;
        var morePages = found.ContentLength > JournalForwardedRead.PageBytes;
        if (pinned != morePages || found.FirstPage.Length != Math.Min(JournalForwardedRead.PageBytes, found.ContentLength))
        {
            throw new InvalidDataException($"Node {owner} answered a first page of {found.FirstPage.Length} bytes of a journal of {found.ContentLength}, {(pinned ? "naming a snapshot" : "naming no snapshot")}.");
        }
    }

    /// <summary>Releases an abandoned read's snapshot, one way: the owner releases its reservation before its window ends.</summary>
    private void Release(IJournalStoreGrain grain, Guid snapshot, NodeId owner)
    {
        try
        {
            _ = grain.ReleaseSnapshotAsync(snapshot);
        }
        catch (Exception failure)
        {
            // The read's own failure is the one reported; the snapshot's window ends the reservation.
            LogReleaseFailed(_logger, snapshot, owner, failure);
        }
    }

    /// <summary>Appends a page at <paramref name="offset"/>, refusing one whose length is not the page the offset asks for.</summary>
    private static void Append(OrleansBufferWriter content, OrleansBuffer bytes, long offset, long contentLength, NodeId owner)
    {
        try
        {
            var expected = Math.Min(JournalForwardedRead.PageBytes, contentLength - offset);
            if (bytes.Length != expected)
            {
                throw new InvalidDataException($"Node {owner} answered a page of {bytes.Length} bytes at {offset} of a journal of {contentLength}, not {expected}.");
            }

            content.Write(bytes.AsReadOnlySequence());
        }
        finally
        {
            bytes.Dispose();
        }
    }

    /// <summary>
    /// Returns the resend a <c>Moved</c> from <paramref name="target"/> asks for: to the owner it names at once, unless it
    /// names none, this node, or the node just asked, when the router backs off and reads the map again.
    /// </summary>
    private static Resend Follow(JournalMoved moved, NodeId target, NodeId node) =>
        moved.Owner is { } owner && owner != target && owner != node ? new Resend(To: owner, Immediately: true) : new Resend(RereadMap: true);

    /// <summary>
    /// Returns the resend a failure of a send asks for when Orleans refused the call, which proves it had no effect: a
    /// placement refusal, after a registry and a map re-read, and the runtime's overload rejection. Each counts only at the
    /// top level or as the direct inner exception of Orleans' message rejection, never nested in a callee's failure.
    /// </summary>
    private static Resend? NoEffect(Exception failure) =>
        (failure is OrleansMessageRejectionException { InnerException: { } inner } ? inner : failure) switch
        {
            LimitExceededException => new Resend(),
            NodePlacementRefusedException => new Resend(RereadMap: true, RereadRegistry: true),
            _ => null,
        };

    /// <summary>
    /// Returns the resend a failure of a read's call asks for: a read has no effect beyond a pin, which a release or the
    /// owner's window ends, so it is read again on every failure but the caller's cancellation, corruption, and a reply the
    /// router fails to decode; a lane's timeout and an overload rejection after a backoff, every other failure after a
    /// registry and a map re-read.
    /// </summary>
    private static Resend? ReadAgain(Exception failure, CancellationToken cancellationToken)
    {
        if (IsOwnCancellation(failure, cancellationToken) || Has<JournalStoreCorruptException>(failure) || Has<InvalidDataException>(failure))
        {
            return null;
        }

        return failure is JournalForwardQueueTimeoutException ? new Resend() : NoEffect(failure) ?? new Resend(RereadMap: true, RereadRegistry: true);
    }

    private static JournalForwardedWriteOutcome Forwarded(LocalJournalWriteOutcome outcome) => outcome switch
    {
        JournalWriteOutcome.Accepted accepted => accepted,
        JournalWriteOutcome.Rejected rejected => rejected,
        JournalWriteOutcome.ReplaceRequired replaceRequired => replaceRequired,
        JournalMoved moved => moved,
        JournalWriteOutcome.Fenced fenced => fenced,
        JournalWriteOutcome.Ambiguous ambiguous => ambiguous,
        LocalJournalWriteOutcome.Throttled throttled => throttled,
        LocalJournalWriteOutcome.Unavailable unavailable => unavailable,
        null => throw new UnreachableException(),
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "A write of journal '{Key}' sent to node {Node} failed, leaving its outcome unknown: Ambiguous.")]
    private static partial void LogAmbiguous(ILogger logger, string key, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resending journal '{Key}' after {Reason}: to {To}, at once {Immediately}, re-reading the map {RereadMap} and the registry {RereadRegistry}.")]
    private static partial void LogResend(ILogger logger, string key, NodeId? to, bool immediately, bool rereadMap, bool rereadRegistry, string? reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A read of journal '{Key}' from node {Node} failed without effect; it is read again.")]
    private static partial void LogReadResent(ILogger logger, string key, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Routing journal '{Key}' could not read the ledger map; it reads it again after a backoff.")]
    private static partial void LogMapUnread(ILogger logger, string key, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The release of snapshot {Snapshot} on node {Node} failed to send; its window ends it.")]
    private static partial void LogReleaseFailed(ILogger logger, Guid snapshot, NodeId node, Exception exception);

    /// <summary>
    /// What a resend does: after a backoff, unless <see cref="Immediately"/>, re-reads the registry and the map as asked, and
    /// sends to the node <see cref="To"/> names, or to the primary the map names.
    /// </summary>
    private sealed record Resend(NodeId? To = null, bool Immediately = false, bool RereadMap = false, bool RereadRegistry = false);

    /// <summary>An attempt's result: settled with its value, or a resend.</summary>
    private readonly struct Attempt<T>
    {
        private readonly T _settled;

        private Attempt(T settled, Resend? resend)
        {
            _settled = settled;
            Resend = resend;
        }

        /// <summary>Gets the resend the attempt asks for, or <see langword="null"/> when it settled.</summary>
        public Resend? Resend { get; }

        /// <summary>Gets the value the attempt settled with.</summary>
        public T Settled => Resend is null ? _settled : throw new InvalidOperationException("The attempt asked for a resend.");

        public static Attempt<T> Of(T settled) => new(settled, null);

        public static Attempt<T> Again(Resend resend) => new(default!, resend);
    }

    /// <summary>A journal's routing across its attempts: its key, and the resend the last attempt asked for.</summary>
    private class RouteState(string key, JournalRowKey rowKey)
    {
        private bool _followed;

        public string Key { get; } = key;

        public JournalRowKey RowKey { get; } = rowKey;

        public Resend? Pending { get; set; }

        /// <summary>Gets what the last attempt's resend answers, for the log.</summary>
        public string? Reason { get; private set; }

        /// <summary>
        /// Records <paramref name="resend"/>, which <paramref name="reason"/> asked for, for the next attempt and returns it
        /// as an attempt. A resend which would go at once right after one which did backs off and re-reads the map instead,
        /// so nodes whose views name each other are asked at most once in a row without a delay.
        /// </summary>
        public Attempt<T> Resend<T>(Resend resend, string reason)
        {
            if (resend.Immediately && _followed)
            {
                resend = new Resend(RereadMap: true);
            }

            _followed = resend.Immediately;
            Pending = resend;
            Reason = reason;
            return Attempt<T>.Again(resend);
        }
    }

    /// <summary>A write's routing: its record, which each forwarded send copies into Orleans' pool once it is granted.</summary>
    private sealed class WriteState(string key, JournalRowKey rowKey, JournalEntryRecord record) : RouteState(key, rowKey)
    {
        public JournalEntryRecord Record { get; } = record;
    }
}

/// <summary>
/// A journal's read on its primary (journals.md §2.3), as the router returns it: the version of the content, whether the
/// journal exists, the replace request the read gives, and the content in Orleans' page pool, which its owner disposes.
/// </summary>
/// <param name="Version">The version of the content.</param>
/// <param name="Exists">Whether the journal exists.</param>
/// <param name="ReplaceRequested">Whether the read asks the journal's writer for a <c>Replace</c>.</param>
/// <param name="Content">The content, which the read's caller disposes.</param>
internal sealed record RoutedRead(JournalVersion Version, bool Exists, bool ReplaceRequested, OrleansBufferWriter Content);
