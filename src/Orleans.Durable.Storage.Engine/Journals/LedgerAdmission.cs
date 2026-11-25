using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>Creates a batch of the ledger's writer at its next slot (<see cref="LedgerWriter.CreateBatchAsync"/>).</summary>
internal delegate ValueTask<LedgerBatch> CreateBatch(int rowCount, int maxLength, CancellationToken cancellationToken);

/// <summary>Proposes a batch through the ledger's writer, which takes its bytes (<see cref="LedgerWriter.ProposeAsync"/>).</summary>
internal delegate ValueTask<ProposalOutcome> ProposeBatch(LedgerBatch batch, CancellationToken cancellationToken);

/// <summary>
/// Loads a key's state from the history at its touch (journals.md §8.3). Its obligations: the state is the fold of every
/// row of the key decided before the call, the store's and every live ledger's (JS-I9), which includes every row of the
/// admission's own ledger the admission applied, since those are decided; the caller owns the returned state, which the
/// loader keeps no reference to; <paramref name="disposal"/> fires when the admission is disposed; and a failure fails
/// the writes waiting for the load, after which the key's next write loads again.
/// </summary>
/// <param name="key">The journal's row key.</param>
/// <param name="disposal">Fires when the admission is disposed.</param>
/// <returns>
/// The key's state and planning state, or <c>Moved</c> when the history read the load took was not this admission's to
/// answer: not linearizable here, or of another open ledger, as after a split or a handoff mid-load.
/// </returns>
internal delegate ValueTask<JournalLoadOutcome> LoadJournalState(JournalRowKey key, CancellationToken disposal);

/// <summary>
/// The outcome of a key's load (journals.md §8.3, conventions.md §15): its load, or <c>Moved</c> when the read the load
/// took is not its admission's, which answers the key's waiting entries <c>Moved</c>, proposes nothing, and leaves the key
/// unloaded.
/// </summary>
internal readonly union JournalLoadOutcome(JournalLoadOutcome.Loaded, JournalMoved)
{
    /// <summary>The key's load, from its history read.</summary>
    /// <param name="Load">The key's state and planning state.</param>
    public sealed record Loaded(JournalLoad Load);
}

/// <summary>
/// What a key's load gives admission (journals.md §8.3): its state, the fold of its history read, or
/// <see langword="null"/> for a key with no history; and its planning state, which admission then owns.
/// </summary>
/// <param name="State">The key's state.</param>
/// <param name="Planning">The key's planning state.</param>
internal readonly record struct JournalLoad(JournalState? State, JournalPlanning Planning)
{
    /// <summary>Returns the load one history read gives, its state and its planning state, over <paramref name="store"/>'s folds.</summary>
    public static JournalLoad Of(ArchiveRead read, IArchiveStore store) => new(JournalState.Fold(read), JournalPlanning.Load(store, read));

    /// <summary>Returns a key's load from its history read as <see cref="Of(ArchiveRead, IArchiveStore)"/> does, or as <paramref name="mutant"/> breaks it.</summary>
    public static JournalLoad Of(ArchiveRead read, IArchiveStore store, JournalLoadMutant mutant, RowToken? staleBase) =>
        new(JournalState.Fold(read.Prefix, read.Rows, mutant, staleBase), JournalPlanning.Load(store, read, mutant));
}

/// <summary>
/// Admission of journal entries on the primary of one open ledger (journals.md §8.1): per key, serialized, it decides each
/// entry against the key's warm state and the states each prefix of its in-flight entries leaves, batches the admitted
/// entries in order, proposes the batches, and applies each <c>Durable</c> batch to warm state before it answers its
/// entries <c>Accepted</c> (JS-I1, JS-I2). It is the one applier of its ledger's warm state.
/// </summary>
/// <remarks>
/// <para>
/// A key's warm state is loaded at its first touch in the claim and kept current by applying this admission's own
/// decided batches; it lives as long as the admission, one primary claim of one ledger (JS-I6), within a byte budget past
/// which the least recently used keys with nothing waiting or in flight are evicted and load again at their next touch.
/// An entry which applies to the state after every in-flight entry of its key is admitted; one which applies to none of
/// the possible states is <c>Rejected</c> at once with the applied <c>CurrentVersion</c>, which is durable; any other waits,
/// holding its key's later entries behind it, until the in-flight entries land and it is decided again, or until a seal
/// fences the ledger.
/// </para>
/// <para>
/// Batches go to the ledger's writer in admission order, at most the ledger's window <c>K</c> outstanding, and the
/// entries accepted and not yet in a batch are bounded in bytes: a write past the bound waits for a batch to take
/// entries. After the first proposal which is not <c>Durable</c>, the admission proposes nothing more: it answers that
/// proposal's entries as the ledger decided them, every entry it has not proposed and every waiting entry
/// <c>Fenced</c>, and every later write <c>Fenced</c>, while each proposal already made answers its entries as the ledger
/// decides it. A fault of its own in building or proposing a batch fences it the same way, fails that batch's entries
/// with the fault, which asserts nothing, and abandons the writer, so a takeover replaces the ledger. The primary admits
/// again only through a new admission, on a new open ledger, with warm state loaded afresh (journals.md §6).
/// </para>
/// <para>
/// Disposal fences the admission and waits for its outstanding proposals' outcomes, which the writer decides. The admission
/// tolerates its writer's disposal, before or after its own: the writer then decides each proposal from its view, or
/// fails it with <see cref="ObjectDisposedException"/>, which the admission answers <c>Ambiguous</c>, and a batch it
/// refused to create or propose leaves its entries <c>Fenced</c>. Its owner disposes it before the writer's registry, so
/// the writer decides from its view what a disposed registry would leave <c>Ambiguous</c> (journals.md §8.1).
/// </para>
/// </remarks>
internal sealed class LedgerAdmission : IAsyncDisposable
{
    private static readonly JournalWriteOutcome FencedOutcome = new(JournalWriteOutcome.Fenced.Instance);
    private static readonly JournalWriteOutcome AmbiguousOutcome = new(JournalWriteOutcome.Ambiguous.Instance);
    private static readonly JournalWriteOutcome ReplaceRequiredOutcome = new(JournalWriteOutcome.ReplaceRequired.Instance);
    private static readonly ContextCallback PumpInContext = static state => ((LedgerAdmission)state!).PumpAndLeave();
    private static readonly ContextCallback InlineFirstBatchInContext = static state => ((LedgerAdmission)state!).RunStashedInlineFirstBatch();
    private static readonly ContextCallback LoadInContext = static state =>
    {
        var (admission, key) = ((LedgerAdmission, KeyAdmission))state!;
        _ = admission.LoadAndLeaveAsync(key);
    };

    /// <summary>
    /// Bytes of unbatched entries required to pipeline an additional concurrent batch while another batch is already in
    /// flight (journals.md §8.1): idle traffic proposes immediately (<c>_outstanding == 0</c>), small concurrent writes
    /// coalesce into one multi-row batch while an in-flight batch completes, and large payloads pipeline at full window.
    /// </summary>
    internal const int CoalesceThresholdBytes = 64 * 1024;

    private readonly LedgerId _ledger;
    private readonly KeyRange _range;
    private readonly int _window;
    private readonly int _maxBatchLength;
    private readonly int _maxBatchEntries;
    private readonly int _maxBatchBytes;
    private readonly int _coalesceThresholdBytes;
    private readonly long _maxQueuedBytes;
    private readonly long _maxProposalBytes;
    private readonly JournalStoreOptions _options;
    private readonly FrozenDictionary<MessageChannel, IMessageHandler> _messageHandlers;
    private readonly (MessageChannel Channel, IMessageSession Session)[] _messageSessions;
    private readonly TimeSpan _tombstoneRetention;
    private readonly TimeProvider _timeProvider;
    private readonly IArchiveStore _store;
    private readonly LoadJournalState _load;
    private readonly CreateBatch _createBatch;
    private readonly ProposeBatch _propose;
    private readonly Action _abandon;
    private readonly JournalWarmState _warmState;
    private readonly Action<JournalWarmState.WarmEntry> _evictWarmEntry;
    private readonly bool _inline;
    private readonly Lock _lock = new();
    private readonly Dictionary<JournalRowKey, KeyAdmission> _keys = [];

    // Writes which wait for room among the entries accepted and not yet proposed, in arrival order.
    private readonly Queue<Pending> _waitingForRoom = new();

    // Admitted entries which no batch holds yet, in admission order.
    private readonly Queue<Pending> _unbatched = new();
    private readonly List<(Slot Below, long TakenBefore, TaskCompletionSource<bool> Applied)> _applyWaiters = [];
    private readonly Channel<Proposal> _proposals;
    private readonly CancellationTokenSource _disposing = new();


    // Admits the pump and the loads, the work which serves every writer, which disposal waits for.
    private readonly AdmissionGate _work = new();

    // The admission's owner's execution context, in which the work which serves every writer runs (conventions.md §12).
    private readonly ExecutionContext? _ownerContext = ExecutionContext.Capture();
    private readonly Action _runPump;
    private readonly Task _completions;

    // Single-slot stash for passing an inline first batch through ExecutionContext.Run without boxing a tuple.
    // Only the single active pump (_pumping == true) reads or writes these fields.
    private PendingBatch _inlineEntries;
    private int _inlineLength;
    private long _inlineBatchNumber;
    private long _queuedBytes;
    private long _unbatchedBytes;
    private long _appliedBelow;
    private int _outstanding;

    // Orders the starts of loads and the acceptances of entries, so a rejection reloads only a key whose latest load
    // started before its entry arrived (journals.md §8.3).
    private long _sequence;

    // The batches the pump has taken, and those of them ended: answered as the ledger decided them, or failed or fenced
    // before their proposals.
    private long _batchesTaken;
    private long _batchesEnded;
    private int _messageCallbacksPending;
    private int _sessionsDisposed;
    private bool _pumping;
    private bool _acceptingRoom;
    private bool _draining;
    private bool _fenced;
    private Exception? _failure;
    private TaskCompletionSource<bool>? _drained;
    private TaskCompletionSource? _disposal;

#if DEBUG
    // Each row key's admitted entries not yet landed, across every KeyAdmission the key has had, so JS-I6's assertions
    // see the entries of a key evicted or reloaded beside them.
    private readonly Dictionary<JournalRowKey, int> _inFlightByRowKey = [];
#endif

    /// <summary>Initializes a new instance of the <see cref="LedgerAdmission"/> class.</summary>
    /// <param name="ledger">The ledger, whose rows of a key keep the key's warm version its only name (journals.md §8.3).</param>
    /// <param name="range">The ledger's range, which every admitted key lies in.</param>
    /// <param name="window">The ledger's window <c>K</c>, the most batches outstanding.</param>
    /// <param name="maxBatchLength">The replica's payload limit, which every batch stays within.</param>
    /// <param name="options">The batch, body, memory, and retention policy, which the admission copies.</param>
    /// <param name="store">The archive store, whose pure members plan each key's tokens and judge its limits (archive.md §3).</param>
    /// <param name="timeProvider">The primary's clock, against which an <c>Expired</c>'s retention is judged.</param>
    /// <param name="load">Loads a key's state at its touch.</param>
    /// <param name="createBatch">Creates a batch of the ledger's writer.</param>
    /// <param name="propose">Proposes a batch through the ledger's writer.</param>
    /// <param name="abandon">Stops the ledger's writer after a fault of the admission's own (<see cref="LedgerWriter.Abandon"/>).</param>
    /// <param name="runInline">
    /// Runs the pump, the loads, and the answering of each decided proposal on the thread which starts them or decides
    /// the proposal, never under the admission's lock, for tests which need a deterministic schedule: once a proposal's
    /// outcome completes with synchronous continuations, the admission has applied it and proposed what follows.
    /// Otherwise they run on the thread pool.
    /// </param>
    /// <param name="messageHandlers">Registered message handlers keyed by channel.</param>
    /// <param name="maxProposalBytes">The writers' proposal byte budget used for single-entry message pre-admission checks.</param>
    /// <exception cref="ArgumentException">
    /// The replica's payload limit holds no batch of one row with a key of <see cref="KeyRange.MaxKeyLength"/> bytes, a
    /// 32-byte version base and token, and the longest row body, a <see cref="JournalStoreOptions.MaxBodyBytes"/> payload
    /// or a <c>Delete</c>'s 8-byte tombstone (journals.md §8.4).
    /// </exception>
    internal LedgerAdmission(
        LedgerId ledger,
        KeyRange range,
        int window,
        int maxBatchLength,
        JournalStoreOptions options,
        IArchiveStore store,
        TimeProvider timeProvider,
        LoadJournalState load,
        CreateBatch createBatch,
        ProposeBatch propose,
        Action abandon,
        bool runInline = false,
        FrozenDictionary<MessageChannel, IMessageHandler>? messageHandlers = null,
        long maxProposalBytes = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(createBatch);
        ArgumentNullException.ThrowIfNull(propose);
        ArgumentNullException.ThrowIfNull(abandon);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        var handlers = messageHandlers ?? [];
        CheckOptions(options, maxBatchLength, handlers.Count > 0);
        _options = options.Clone();
        _messageHandlers = handlers;
        _maxBatchEntries = _options.MaxBatchEntries;
        _maxBatchBytes = _options.MaxBatchBytes;
        _coalesceThresholdBytes = Math.Min(CoalesceThresholdBytes, _maxBatchBytes);
        _maxQueuedBytes = _options.MaxQueuedBytes;
        _maxProposalBytes = maxProposalBytes;
        _tombstoneRetention = _options.TombstoneRetention;
        _timeProvider = timeProvider;
        _store = store;
        _ledger = ledger;
        _range = range;
        _window = window;
        _maxBatchLength = maxBatchLength;
        _load = load;
        _createBatch = createBatch;
        _propose = propose;
        _abandon = abandon;
        _warmState = new JournalWarmState(_options.WarmStateBytes);
        _evictWarmEntry = EvictWarmEntry;
        _inline = runInline;
        _messageSessions = OpenMessageSessions(ledger, handlers);
        _proposals = Channel.CreateUnbounded<Proposal>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = runInline });
        _runPump = RunPump;
        _completions = CompleteProposalsAsync();
    }

    /// <summary>The decision on an entry against its key's possible states (journals.md §8.1, step 3).</summary>
    private enum Decision
    {
        /// <summary>The entry applies to the state after every in-flight entry: it is admitted.</summary>
        Admit,

        /// <summary>The entry applies to no possible state: it is rejected at once.</summary>
        Reject,

        /// <summary>The entry applies to some possible state but not to the last: it waits.</summary>
        Wait,

        /// <summary>The entry applies to the state after every in-flight entry, whose count is at the <c>u64</c> maximum: no row can advance it.</summary>
        Overflow,

        /// <summary>
        /// An <c>Append</c> which the store's per-generation limits refuse with nothing of its key in flight: it is answered
        /// <c>ReplaceRequired</c> at once, from the applied state, with no effect (JS-I4).
        /// </summary>
        ReplaceRequired,
    }

    /// <summary>A break of the key's token chain which the drain's and the reads' chain checks must catch (journals.md §10).</summary>
    internal enum ChainMutant
    {
        /// <summary>The token planning of journals.md §8.1 step 5.</summary>
        None,

        /// <summary>A roll keeps the key's token from the earlier segment instead of <c>End()</c> of its write there.</summary>
        TokenNotRolled,

        /// <summary>A defining row stores a version before it one count past the fold's.</summary>
        VersionOffTheFold,
    }

    /// <summary>A decision of <c>ReplaceRequired</c> which the white-box oracle must catch (journals.md §10).</summary>
    internal enum ReplaceRequiredMutant
    {
        /// <summary>The decision of journals.md §8.1 step 4.</summary>
        None,

        /// <summary>Answers <c>ReplaceRequired</c> while entries of the key are in flight, which decide the write.</summary>
        AnswersWithEntriesInFlight,

        /// <summary>Answers <c>ReplaceRequired</c> for an <c>Append</c> both candidate writes admit, one short of the limits.</summary>
        AnswersWhenBothAdmit,
    }

    /// <summary>A rejection which the rebase test must catch (journals.md §10).</summary>
    internal enum ReloadMutant
    {
        /// <summary>The reload before a rejection of journals.md §8.3.</summary>
        None,

        /// <summary>Rejects a warm key's entry at once, even when only a reload would show its expected version names the state.</summary>
        RejectsWithoutReload,
    }

    /// <summary>A decision on a key's waiting entries which the white-box rejection oracle, explored schedules, or the progress check must catch (journals.md §10).</summary>
    internal enum RejectionMutant
    {
        /// <summary>The decisions of journals.md §8.1 step 3.</summary>
        None,

        /// <summary>Rejects at once an entry which applies to a state a prefix of its key's in-flight entries leaves, which waits.</summary>
        RejectsWaiting,

        /// <summary>Never decides a key's waiting entries again once its in-flight entries land.</summary>
        NeverDecidesWaitingAgain,

        /// <summary>Decides a write arriving while its key's state is still loading against the unloaded state instead of waiting for the load.</summary>
        DecidesWhileLoading,
    }

    /// <summary>An eviction which JS-I6's per-row-key count must catch (journals.md §10).</summary>
    internal enum EvictionMutant
    {
        /// <summary>The eviction of journals.md §8.3: a key with nothing waiting or in flight.</summary>
        None,

        /// <summary>Evicts a key with admitted entries in flight (<c>evict-with-inflight</c>).</summary>
        EvictWithInFlight,
    }

    /// <summary>Where a key's state is.</summary>
    private enum KeyLoad
    {
        Unloaded,
        Loading,
        Loaded,
    }

    /// <summary>Gets or sets the chain mutant the admission plans as, which tests set to show the chain checks catch it.</summary>
    internal ChainMutant ChainMutation { get; set; }

    /// <summary>Gets or sets the <c>ReplaceRequired</c> mutant the admission decides as, which tests set to show the oracle catches it.</summary>
    internal ReplaceRequiredMutant ReplaceRequiredMutation { get; set; }

    /// <summary>Gets or sets the reload mutant the admission rejects as, which tests set to show the rebase test catches it.</summary>
    internal ReloadMutant ReloadMutation { get; set; }

    /// <summary>Gets or sets the rejection mutant the admission decides as, which tests set to show the oracle catches it.</summary>
    internal RejectionMutant RejectionMutation { get; set; }

    /// <summary>Gets or sets the eviction mutant the admission evicts as, which tests set to show JS-I6's assertions catch it.</summary>
    internal EvictionMutant EvictionMutation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether admission applies entries to warm state at admission rather than after
    /// <c>Durable</c> (<c>apply-on-admit</c>, journals.md §10, JS-I2/JS-I3): tests set this to verify that rejections
    /// and reads catch uncommitted warm-state mutation.
    /// </summary>
    internal bool ApplyOnAdmitMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>Expired</c> skips the tombstone retention check (JS-I7 mutant): tests
    /// set this to verify that expiring a tombstone before <c>TombstoneRetention</c> fails the oracle.
    /// </summary>
    internal bool IgnoreRetentionMutant { get; set; }

    /// <summary>
    /// Gets or sets an observer of each rejection, which the white-box rejection oracle records (journals.md §10); it runs
    /// under the admission's lock, so it only records.
    /// </summary>
    internal Action<JournalRejection>? OnRejected { get; set; }

    /// <summary>
    /// Gets or sets an observer of each <c>ReplaceRequired</c>, which the white-box oracle records (journals.md §10); it runs
    /// under the admission's lock, so it only records.
    /// </summary>
    internal Action<JournalReplaceRequired>? OnReplaceRequired { get; set; }

    /// <summary>Gets the bytes of rows accepted and not yet in a batch, which every answer before a batch releases.</summary>
    internal long QueuedBytes
    {
        get
        {
            lock (_lock)
            {
                return _queuedBytes;
            }
        }
    }

    /// <summary>Gets the slot below which every <c>Durable</c> batch of the admission is applied to warm state.</summary>
    public Slot AppliedBelow
    {
        get
        {
            lock (_lock)
            {
                return new Slot(_appliedBelow);
            }
        }
    }

    /// <summary>Creates the admission of <paramref name="writer"/>'s ledger, proposing through it, or through <paramref name="propose"/>, which wraps it.</summary>
    /// <exception cref="InvalidOperationException">The writer only appends.</exception>
    internal static LedgerAdmission For(
        LedgerWriter writer,
        JournalStoreOptions options,
        IArchiveStore store,
        TimeProvider timeProvider,
        LoadJournalState load,
        ProposeBatch? propose = null,
        bool runInline = false,
        FrozenDictionary<MessageChannel, IMessageHandler>? messageHandlers = null,
        long maxProposalBytes = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new LedgerAdmission(
            writer.LedgerId,
            writer.Range,
            writer.Window,
            writer.MaxBatchLength,
            options,
            store,
            timeProvider,
            load,
            writer.CreateBatchAsync,
            propose ?? writer.ProposeAsync,
            writer.Abandon,
            runInline,
            messageHandlers,
            maxProposalBytes);
    }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="IMessageSession.OnBatchDurable"/> fires before quorum
    /// durability (<c>OnDurableBeforeQuorumMutant</c>, JS-I12 race mutant for tests).
    /// </summary>
    internal bool OnDurableBeforeQuorumMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether callers complete <c>Accepted</c> before
    /// <see cref="IMessageSession.OnBatchDurable"/> runs (<c>CompleteCallerBeforeOnDurableMutant</c>, JS-I12 race mutant for tests).
    /// </summary>
    internal bool CompleteCallerBeforeOnDurableMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="IDisposable.Dispose"/> on message sessions runs before the last
    /// possible <see cref="IMessageSession.OnBatchDurable"/> (<c>DisposeBeforeLastOnDurableMutant</c>, JS-I12 race mutant for tests).
    /// </summary>
    internal bool DisposeBeforeLastOnDurableMutant { get; set; }

    /// <summary>
    /// Returns the longest batch the admission builds of one row (journals.md §8.4): a key of
    /// <see cref="KeyRange.MaxKeyLength"/> bytes, a 32-byte version base and token, and the longest row body, a
    /// <paramref name="maxBodyBytes"/> payload or a <c>Delete</c>'s 8-byte tombstone.
    /// </summary>
    internal static long LongestBatch(int maxBodyBytes) =>
        LedgerBatch.MaxHeaderLength(1) + LedgerBatch.MaxRowLength(KeyRange.MaxKeyLength, RowToken.MaxLength, Pending.TokenReservation, Math.Max(maxBodyBytes, JournalEntry.TombstoneLength));

    /// <summary>
    /// Returns the longest single-entry batch including up to <see cref="JournalStoreOptions.MaxMessagesPerWrite"/>
    /// unversioned <see cref="RowKind.Message"/> rows when <paramref name="hasMessageChannels"/> is <see langword="true"/>.
    /// </summary>
    internal static long LongestBatch(JournalStoreOptions options, bool hasMessageChannels)
    {
        if (!hasMessageChannels)
        {
            return LongestBatch(options.MaxBodyBytes);
        }

        var perMessageOverhead = LedgerBatch.MaxMessageLength(MessageChannel.MaxLength, MessageRecord.MaxPayloadLength) - MessageRecord.MaxPayloadLength;
        var messageRowsBytes = perMessageOverhead * options.MaxMessagesPerWrite + options.MaxMessageBytesPerWrite;
        var journalRowBytes = LedgerBatch.MaxRowLength(KeyRange.MaxKeyLength, RowToken.MaxLength, Pending.TokenReservation, Math.Max(options.MaxBodyBytes, JournalEntry.TombstoneLength));
        return LedgerBatch.MaxHeaderLength(1 + options.MaxMessagesPerWrite) + journalRowBytes + messageRowsBytes;
    }

    /// <summary>
    /// Returns the largest batch the admission builds under <paramref name="options"/> (journals.md §8.4): the larger of one
    /// longest row's batch (<see cref="LongestBatch(JournalStoreOptions, bool)"/>), which a row past <c>MaxBatchBytes</c>
    /// takes alone, and a batch of <c>MaxBatchEntries</c> entries within <c>MaxBatchBytes</c> of rows.
    /// </summary>
    internal static long LargestBatch(JournalStoreOptions options, bool hasMessageChannels = false)
    {
        var maxRows = hasMessageChannels
            ? (int)Math.Min(int.MaxValue, options.MaxBatchEntries * (1L + options.MaxMessagesPerWrite))
            : options.MaxBatchEntries;
        return Math.Max(LongestBatch(options, hasMessageChannels), LedgerBatch.MaxHeaderLength(maxRows) + options.MaxBatchBytes);
    }

    /// <summary>Checks that each of <paramref name="options"/>' settings is within its range (journals.md §8.8).</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of its range, named by the setting.</exception>
    internal static void CheckRanges(JournalStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
    }

    /// <summary>
    /// Checks the admission's own guards on <paramref name="options"/>: each setting within its range, and the replica's
    /// payload limit holding a batch of one longest entry (<see cref="LongestBatch(JournalStoreOptions, bool)"/>, journals.md §8.4),
    /// whatever larger batch the policy allows, which the store's startup checks bound.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of its range.</exception>
    /// <exception cref="ArgumentException">The payload limit holds no batch of one longest entry, named as <c>maxBatchLength</c>.</exception>
    internal static void CheckOptions(JournalStoreOptions options, int maxBatchLength, bool hasMessageChannels = false)
    {
        CheckRanges(options);
        var longestBatch = LongestBatch(options, hasMessageChannels);
        if (maxBatchLength < longestBatch)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The replica's payload limit of {maxBatchLength} bytes holds no batch of one entry with the longest key and body, {Math.Max(options.MaxBodyBytes, JournalEntry.TombstoneLength)} bytes: {longestBatch} bytes."),
                nameof(maxBatchLength));
        }
    }

    /// <summary>
    /// Checks the preconditions of journals.md §2.2 which need no state, which the writing process checks before the entry
    /// leaves it and the owner as it arrives: the record holds an <c>Append</c>, <c>Replace</c>, or <c>Delete</c>, never
    /// an <c>Expired</c>, which only the expiry scan writes (JS-I7); <paramref name="store"/> admits its key
    /// (<c>AdmitsKey</c>); and its payload is at most <paramref name="maxBodyBytes"/> (journals.md §8.4).
    /// </summary>
    /// <exception cref="ArgumentException">A precondition fails, named as <c>record</c>.</exception>
    internal static void CheckRecord(JournalEntryRecord record, IArchiveStore store, int maxBodyBytes) =>
        CheckRecord(record, store, maxBodyBytes, maxMessagesPerWrite: 0, maxMessageBytesPerWrite: 0, []);

    /// <summary>
    /// Checks the preconditions of journals.md §2.2 and §8.1 step 1, including all attached
    /// <see cref="MessageRecord"/> items against <paramref name="options"/> and <paramref name="messageHandlers"/>.
    /// </summary>
    internal static void CheckRecord(
        JournalEntryRecord record,
        IArchiveStore store,
        JournalStoreOptions options,
        FrozenDictionary<MessageChannel, IMessageHandler> messageHandlers) =>
        CheckRecord(record, store, options.MaxBodyBytes, options.MaxMessagesPerWrite, options.MaxMessageBytesPerWrite, messageHandlers);

    private static void CheckRecord(
        JournalEntryRecord record,
        IArchiveStore store,
        int maxBodyBytes,
        int maxMessagesPerWrite,
        int maxMessageBytesPerWrite,
        FrozenDictionary<MessageChannel, IMessageHandler> messageHandlers)
    {
        if (record.Key is null)
        {
            throw new ArgumentException("The record is the default, which holds no entry.", nameof(record));
        }

        if (record.Entry.Kind == JournalEntryKind.Expired)
        {
            throw new ArgumentException("Only the key's primary writes an Expired, through its expiry scan (JS-I7).", nameof(record));
        }

        CheckKey(record.Key, store, nameof(record));
        CheckPayloadLength(record.Entry.PayloadLength, maxBodyBytes, nameof(record));
        if (record.Entry.HasMessages)
        {
            CheckMessages(record.Entry.Messages.AsSpan(), maxMessagesPerWrite, maxMessageBytesPerWrite, messageHandlers, nameof(record));
        }
    }

    /// <summary>Validates attached <see cref="MessageRecord"/> items before admission (journals.md §8.1 step 1).</summary>
    internal static void CheckMessages(
        ReadOnlySpan<MessageRecord> messages,
        int maxMessagesPerWrite,
        int maxMessageBytesPerWrite,
        FrozenDictionary<MessageChannel, IMessageHandler> messageHandlers,
        string parameterName)
    {
        if (messages.IsEmpty)
        {
            return;
        }

        if (messages.Length > maxMessagesPerWrite)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The entry attaches {messages.Length} messages, exceeding MaxMessagesPerWrite ({maxMessagesPerWrite})."),
                parameterName);
        }

        long totalBytes = 0;
        for (var i = 0; i < messages.Length; i++)
        {
            ref readonly var item = ref messages[i];
            if (item.Channel is null)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Message {i} has a null channel."),
                    parameterName);
            }

            if (!messageHandlers.TryGetValue(item.Channel, out var handler))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Message channel '{item.Channel}' is not registered on this JournalStore."),
                    parameterName);
            }

            if (item.Payload.Length > MessageRecord.MaxPayloadLength)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Message {i} payload of {item.Payload.Length} bytes exceeds {MessageRecord.MaxPayloadLength}."),
                    parameterName);
            }

            totalBytes += item.Payload.Length;
            if (totalBytes > maxMessageBytesPerWrite)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Combined message payload size {totalBytes} bytes exceeds MaxMessageBytesPerWrite ({maxMessageBytesPerWrite})."),
                    parameterName);
            }

            if (handler.Validate(item.Payload.Span) is { } rejection)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Message {i} for channel '{item.Channel}' failed validation: {rejection.Message}"),
                    parameterName,
                    rejection);
            }
        }
    }

    /// <summary>Checks that <paramref name="store"/> admits <paramref name="key"/> (<c>AdmitsKey</c>, journals.md §2.2).</summary>
    /// <exception cref="ArgumentException">The store cannot store the key, named as <paramref name="parameterName"/>.</exception>
    internal static void CheckKey(JournalRowKey key, IArchiveStore store, string parameterName)
    {
        if (!store.AdmitsKey(key.Span))
        {
            throw new ArgumentException("The archive store cannot store the entry's key: AdmitsKey refuses it.", parameterName);
        }
    }

    /// <summary>Checks that a payload of <paramref name="length"/> bytes is at most <paramref name="maxBodyBytes"/> (journals.md §8.4).</summary>
    /// <exception cref="ArgumentException">The payload is longer, named as <paramref name="parameterName"/>.</exception>
    internal static void CheckPayloadLength(long length, int maxBodyBytes, string parameterName)
    {
        if (length > maxBodyBytes)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The entry's payload is {length} bytes, longer than MaxBodyBytes, {maxBodyBytes} bytes."),
                parameterName);
        }
    }

    /// <summary>
    /// Admits <paramref name="record"/>, of which it retains its own lease while it needs the payload; the caller keeps and
    /// disposes its own. Returns the write's outcome once decided.
    /// </summary>
    /// <param name="record">
    /// An <c>Append</c>, <c>Replace</c>, or <c>Delete</c>, whose CRC-32C its encoding computed or its decoding verified
    /// where its bytes entered the primary (JS-I5).
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the caller waiting, which asserts nothing: an entry accepted before or after it fires may still be proposed
    /// and decided (journals.md §2.2).
    /// </param>
    /// <returns>
    /// The outcome; <c>Moved</c> without an owner for a key outside the ledger's range; <c>ReplaceRequired</c> for an
    /// <c>Append</c> the archive store's per-generation limits refuse, after which the writer's next write is a
    /// <c>Replace</c> (journals.md §8.1 step 4).
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The record is the default or an <c>Expired</c>, which only the expiry scan writes (JS-I7); the archive store cannot
    /// store its key (<c>AdmitsKey</c>); or its body is longer than <see cref="JournalStoreOptions.MaxBodyBytes"/>
    /// (journals.md §8.4); each refused before anything is loaded or proposed. The
    /// task fails with it for an entry which applies only to a state no row can advance, whose count is the <c>u64</c>
    /// maximum.
    /// </exception>
    public Task<JournalWriteOutcome> AdmitAsync(JournalEntryRecord record, CancellationToken cancellationToken = default)
    {
        CheckRecord(record, _store, _options, _messageHandlers);
        return Submit(record, cancellationToken, out _);
    }

    /// <summary>
    /// Admits <paramref name="record"/> for <see cref="JournalStore.WriteAsync"/> and reports in
    /// <paramref name="mayMove"/> whether the returned task can still complete with a same-node
    /// <see cref="JournalMoved"/> that requires a retry with the original record.
    /// </summary>
    internal Task<JournalWriteOutcome> AdmitForStore(JournalEntryRecord record, CancellationToken cancellationToken, out bool mayMove) =>
        Submit(record, cancellationToken, out mayMove);

    /// <summary>
    /// Admits an <c>Expired</c> of the deleted state at <paramref name="expectedVersion"/>, for the primary's expiry scan
    /// (journals.md §8.6, JS-I7). It is judged against the warm applied state alone, and never loads its key: rejected at
    /// once when its key holds no loaded warm state, reporting its own expected version, or while any entry of its key is
    /// in flight or waiting, and otherwise admitted only when the applied state is deleted, at its expected version, and
    /// its tombstone's retention has passed on the admission's clock (JS-I1). It is no touch of its key's warm state.
    /// </summary>
    internal Task<JournalWriteOutcome> ExpireAsync(JournalRowKey key, JournalVersion expectedVersion, CancellationToken cancellationToken = default) =>
        SubmitExpireAsync(key, expectedVersion, cancellationToken);

    private Task<JournalWriteOutcome> SubmitExpireAsync(JournalRowKey key, JournalVersion expectedVersion, CancellationToken cancellationToken)
    {
        using var record = JournalEntryRecord.Expired(key, expectedVersion);
        return Submit(record, cancellationToken, out _);
    }

    /// <summary>
    /// Adds to <paramref name="candidates"/> the expiry scan's candidates among the admission's warm state (journals.md §8.6):
    /// each loaded key with nothing waiting or in flight whose applied state is deleted, its tombstone's retention passed on
    /// the admission's clock, and its count below the <c>u64</c> maximum, with that state's version. A fenced admission adds
    /// none. The candidates are hints, which <see cref="ExpireAsync"/> decides afresh (JS-I1).
    /// </summary>
    internal void AddExpiryCandidates(List<(JournalRowKey Key, JournalVersion Version)> candidates)
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        lock (_lock)
        {
            if (_fenced)
            {
                return;
            }

            foreach (var key in _keys.Values)
            {
                if (key.Load != KeyLoad.Loaded
                    || !key.Waiting.IsEmpty
                    || key.InFlightCount != 0
                    || !RetentionPassed(key.Applied, now)
                    || key.Applied!.CurrentVersion is not { Count: < ulong.MaxValue } version)
                {
                    continue;
                }

                candidates.Add((key.RowKey, version));
            }
        }
    }

    /// <summary>Copies every loaded warm key's applied state and next token for <see cref="JournalStore.KeepWarmStateAcrossClaimsMutant"/>.</summary>
    internal void CopyWarmForMutant(
        LedgerId ledger,
        System.Collections.Concurrent.ConcurrentDictionary<JournalRowKey, (LedgerId Ledger, JournalState? Applied, RowToken NextToken)> destination)
    {
        lock (_lock)
        {
            foreach (var key in _keys.Values)
            {
                if (key.Load == KeyLoad.Loaded && key.Planning is { } planning)
                {
                    destination[key.RowKey] = (ledger, key.Applied?.Clone(), planning.Written.End());
                }
            }
        }
    }

    /// <summary>Returns whether <paramref name="rowKey"/> currently holds loaded warm state in this active claim.</summary>
    internal bool IsWarm(JournalRowKey rowKey)
    {
        lock (_lock)
        {
            return !_fenced && _keys.TryGetValue(rowKey, out var key) && key.Load == KeyLoad.Loaded;
        }
    }

    /// <summary>
    /// Loads a cold tombstone candidate found in the key indexes (journals.md §8.6) through the unified single-flight
    /// <see cref="KeyLoad.Loading"/> sentinel so concurrent foreground writes queue behind the load and never race with
    /// stale warm state, then submits its <c>Expired</c> entry without evicting live warm keys. Returns
    /// <see langword="null"/> when the key was skipped without proposing (already warm with active work, concurrent
    /// write arrived during load, resurrected, already expired, retention not yet passed, or admission backpressured);
    /// otherwise the submitted <c>Expired</c>'s outcome.
    /// </summary>
    internal async Task<JournalWriteOutcome?> ExpireColdAsync(
        JournalRowKey key,
        Func<JournalVersion, Action<JournalWriteOutcome?>?>? onProposed,
        CancellationToken cancellationToken)
    {
        KeyAdmission? loading = null;
        JournalVersion? warmVersion = null;
        lock (_lock)
        {
            if (_failure is { } failure)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            if (_fenced)
            {
                return FencedOutcome;
            }

            if (!_range.Contains(key.Span))
            {
                return new JournalWriteOutcome(new JournalMoved(Owner: null));
            }

            if (_fenced || _draining || _failure is not null)
            {
                return FencedOutcome;
            }

            if (_keys.TryGetValue(key, out var existing))
            {
                if (existing.Load != KeyLoad.Loaded
                    || !existing.Waiting.IsEmpty
                    || existing.InFlightCount != 0
                    || !RetentionPassed(existing.Applied)
                    || existing.Applied!.CurrentVersion is not { Count: < ulong.MaxValue } existingVersion)
                {
                    return null;
                }

                var existingRowLength = (int)LedgerBatch.MaxRowLength(key.Length, existingVersion.Base.Length, Pending.TokenReservation, 0);
                if (_waitingForRoom.Count > 0 || !HasRoom(existingRowLength))
                {
                    return null;
                }

                warmVersion = existingVersion;
            }
            else
            {
                AssertNoneInFlight(key, "A cold expiry load starts only with no admitted entry in flight (JS-I6).");
                var warmEntry = _warmState.Touch(key, KeyAdmission.Overhead);
                loading = new KeyAdmission(warmEntry)
                {
                    Load = KeyLoad.Loading,
                    LoadStartedAt = ++_sequence,
                    IdlesLeastRecent = true,
                };
                _keys.Add(key, loading);
                Enter();
            }
        }

        if (warmVersion is { } existingReady)
        {
            var warmObserved = onProposed?.Invoke(existingReady);
            return await ObserveProposalAsync(SubmitExpireAsync(key, existingReady, cancellationToken), warmObserved).ConfigureAwait(false);
        }

        Task<JournalWriteOutcome>? submitted = null;
        Action<JournalWriteOutcome?>? observed = null;
        var after = default(After);
        try
        {
            JournalLoadOutcome loaded;
            try
            {
                loaded = await _load(key, _disposing.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var fenced = false;
                lock (_lock)
                {
                    fenced = _fenced;
                    if (!fenced)
                    {
                        Unload(loading!);
                        while (loading!.Waiting.TryDequeue(out var waiting))
                        {
                            after.Fail(waiting, exception);
                            Unqueue(waiting, ref after);
                        }

                        ResolvePumpLocked(ref after);
                    }
                }

                Run(ref after);
                if (fenced || _disposing.IsCancellationRequested && exception is OperationCanceledException)
                {
                    return FencedOutcome;
                }

                throw;
            }

            if (loaded.Value is JournalMoved moved)
            {
                lock (_lock)
                {
                    if (!_fenced)
                    {
                        AnswerMoved(loading!, moved, ref after);
                        ResolvePumpLocked(ref after);
                    }
                }

                Run(ref after);
                return new JournalWriteOutcome(moved);
            }

            if (loaded.Value is not JournalLoadOutcome.Loaded found)
            {
                throw new UnreachableException("A load outcome is either Loaded or Moved.");
            }

            JournalVersion? readyVersion = null;
            lock (_lock)
            {
                if (_failure is { } failure)
                {
                    if (!_fenced)
                    {
                        Unload(loading!);
                    }

                    ExceptionDispatchInfo.Throw(failure);
                }

                if (_fenced)
                {
                    return FencedOutcome;
                }

                AssertSameState(loading!, found.Load.State);
                _warmState.SetLoaded(loading!.Warm, found.Load.State, found.Load.Planning);
                loading.Load = KeyLoad.Loaded;
                loading.Reloading = false;

                if (!loading.Waiting.IsEmpty)
                {
                    // A concurrent foreground write arrived while loading; let it proceed and skip cold expiry.
                    Drain(loading, ref after);
                }
                else if (cancellationToken.IsCancellationRequested
                    || !RetentionPassed(loading.Applied)
                    || loading.Applied!.CurrentVersion is not { Count: < ulong.MaxValue } version)
                {
                    MaybeIdle(loading);
                }
                else
                {
                    var rowLength = (int)LedgerBatch.MaxRowLength(key.Length, version.Base.Length, Pending.TokenReservation, 0);
                    if (_waitingForRoom.Count > 0 || !HasRoom(rowLength))
                    {
                        MaybeIdle(loading);
                    }
                    else if (onProposed is null)
                    {
                        using var record = JournalEntryRecord.Expired(key, version);
                        submitted = SubmitLocked(record, rowLength, ref after, cancellationToken, out _, out _);
                    }
                    else
                    {
                        readyVersion = version;
                    }
                }

                ResolvePumpLocked(ref after);
            }

            if (readyVersion is { } toObserve)
            {
                try
                {
                    observed = onProposed!(toObserve);
                }
                catch
                {
                    lock (_lock)
                    {
                        MaybeIdle(loading!);
                    }

                    throw;
                }

                Exception? failed = null;
                JournalWriteOutcome? immediateOutcome = null;
                var skip = false;
                lock (_lock)
                {
                    if (_failure is { } failure)
                    {
                        failed = failure;
                    }
                    else if (_fenced)
                    {
                        immediateOutcome = FencedOutcome;
                    }
                    else if (cancellationToken.IsCancellationRequested
                        || loading!.Evicted
                        || loading.Load != KeyLoad.Loaded
                        || !loading.Waiting.IsEmpty
                        || loading.InFlightCount != 0
                        || !RetentionPassed(loading.Applied)
                        || loading.Applied!.CurrentVersion != toObserve)
                    {
                        MaybeIdle(loading!);
                        skip = true;
                    }
                    else
                    {
                        var rowLength = (int)LedgerBatch.MaxRowLength(key.Length, toObserve.Base.Length, Pending.TokenReservation, 0);
                        if (_waitingForRoom.Count > 0 || !HasRoom(rowLength))
                        {
                            MaybeIdle(loading);
                            skip = true;
                        }
                        else
                        {
                            using var record = JournalEntryRecord.Expired(key, toObserve);
                            submitted = SubmitLocked(record, rowLength, ref after, cancellationToken, out _, out _);
                        }
                    }

                    ResolvePumpLocked(ref after);
                }

                if (failed is not null)
                {
                    observed?.Invoke(null);
                    ExceptionDispatchInfo.Throw(failed);
                }

                if (immediateOutcome is { } earlyOutcome)
                {
                    observed?.Invoke(earlyOutcome);
                    return earlyOutcome;
                }

                if (skip)
                {
                    observed?.Invoke(null);
                    return null;
                }
            }

            Run(ref after);
        }
        finally
        {
            _work.Exit();
        }

        return submitted is not null
            ? await ObserveProposalAsync(submitted, observed).ConfigureAwait(false)
            : null;

        static async Task<JournalWriteOutcome?> ObserveProposalAsync(Task<JournalWriteOutcome> task, Action<JournalWriteOutcome?>? callback)
        {
            JournalWriteOutcome? outcome = null;
            try
            {
                outcome = await task.ConfigureAwait(false);
                return outcome;
            }
            finally
            {
                callback?.Invoke(outcome);
            }
        }
    }

    /// <summary>
    /// Returns the loaded warm applied version of <paramref name="key"/>, or <see langword="null"/> when the key is not
    /// loaded in warm state; used only by <c>JournalStore.AnswersWarmStateNewerVersionMutant</c>.
    /// </summary>
    internal JournalVersion? TryGetWarmVersion(JournalRowKey key)
    {
        lock (_lock)
        {
            return !_fenced && _keys.TryGetValue(key, out var admission) && admission.Load == KeyLoad.Loaded
                ? admission.Applied?.CurrentVersion ?? JournalVersion.Origin
                : null;
        }
    }

    /// <summary>Gets the number of owner reads waiting for the admission to apply its batches, which tests read.</summary>
    internal int ApplyWaiters
    {
        get
        {
            lock (_lock)
            {
                return _applyWaiters.Count;
            }
        }
    }

    /// <summary>
    /// Waits until every <c>Durable</c> batch of the admission below <paramref name="below"/> is applied to warm state, as an
    /// owner read waits for the tail it observed (journals.md §8.2), so a write at the version the read returns is judged
    /// against an applied state at least as new as the read: until the admission has applied through a slot at or above
    /// it, or has ended every batch it took before the call, since a batch it takes later gets a slot at or above any tail
    /// observed before. Slots of batches which are not the admission's, such as a writer's segment markers, need no wait.
    /// A fenced admission answers <see langword="false"/>, whatever it applied, and so does every waiter when it fences;
    /// a waiter whose <paramref name="cancellationToken"/> fires leaves the admission's waiters.
    /// </summary>
    /// <returns><see langword="true"/> once applied; <see langword="false"/> when the admission is fenced first.</returns>
    public Task<bool> WaitAppliedAsync(Slot below, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> applied;
        lock (_lock)
        {
            if (_fenced)
            {
                return Task.FromResult(false);
            }

            if (IsApplied(below, _batchesTaken))
            {
                return Task.FromResult(true);
            }

            applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _applyWaiters.Add((below, _batchesTaken, applied));
        }

        return cancellationToken.CanBeCanceled ? WaitAppliedCancellablyAsync(applied, cancellationToken) : applied.Task;
    }

    /// <summary>
    /// Admits nothing more, answering every entry it has not proposed <c>Fenced</c>, and waits for its outstanding
    /// proposals, which the ledger's writer decides, to be answered. Every call waits for the one disposal.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _disposal, mine, null) is { } existing)
        {
            await existing.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            mine.SetResult();
        }
        catch (Exception exception)
        {
            mine.SetException(exception);
            throw;
        }
    }

    /// <summary>
    /// Decides <paramref name="pending"/>'s entry against the states its key's applied state and in-flight entries give (journals.md
    /// §8.1, step 3). While entries are in flight the key's base is fixed and each adds 1 to the count, so the possible
    /// states' versions are the applied state's and one more count per in-flight entry, and the entry's expected version
    /// names at most one of them. An <c>Expired</c> is judged against the applied state alone. An <c>Append</c> which
    /// applies then meets the store's limits (step 4).
    /// </summary>
    private Decision Decide(KeyAdmission key, Pending pending)
    {
        var entry = pending.Entry;
        var expected = entry.ExpectedVersion;
        if (entry.Kind == JournalEntryKind.Expired)
        {
            if (key.InFlightCount != 0)
            {
                return Decision.Reject;
            }

            if (!JournalState.AppliesTo(key.Applied, entry) || !RetentionPassed(key.Applied))
            {
                return Decision.Reject;
            }
        }
        else
        {
            var applied = key.Applied?.CurrentVersion ?? JournalVersion.Origin;
            var effectiveInFlight = ApplyOnAdmitMutant ? 0 : key.InFlightCount;
            if (expected.Base != applied.Base || expected.Count < applied.Count || expected.Count - applied.Count > (ulong)effectiveInFlight)
            {
                return Decision.Reject;
            }

            if (expected.Count - applied.Count < (ulong)effectiveInFlight)
            {
                return RejectionMutation == RejectionMutant.RejectsWaiting ? Decision.Reject : Decision.Wait;
            }
        }

        if (expected.Count == ulong.MaxValue)
        {
            return Decision.Overflow;
        }

        return entry.Kind == JournalEntryKind.Append ? DecideLimits(key, pending.Command) : Decision.Admit;
    }

    /// <summary>
    /// Decides an <c>Append</c> against the store's per-generation limits (journals.md §8.1 step 4): admitted when both
    /// candidate writes admit it. While entries of the key are in flight, they decide the write it would join, so it waits
    /// for them when a candidate refuses, or when the admitted write has reached the store's replace request, near which a
    /// write split across segments could pass what the candidates judge (archive.md §3); with nothing in flight, a refusal
    /// answers <c>ReplaceRequired</c>, from the applied state.
    /// </summary>
    private Decision DecideLimits(KeyAdmission key, ArchiveCommand append)
    {
        var planning = key.Planning!;
        var inFlight = key.InFlightCount > 0 && ReplaceRequiredMutation != ReplaceRequiredMutant.AnswersWithEntriesInFlight;
        if (inFlight && _store.IsReplaceRequested(planning.Admitted.End()))
        {
            return Decision.Wait;
        }

        if (planning.AdmitsEitherWay(_store, append) && !AnswersOneEarly(planning, append))
        {
            return Decision.Admit;
        }

        // A refusal with nothing in flight is decided from the applied state and has no effect (JS-I4); with entries in
        // flight, they decide the write this Append would join, so it waits for their outcomes.
        return inFlight ? Decision.Wait : Decision.ReplaceRequired;
    }

    /// <summary>The mutant which refuses an <c>Append</c> both candidates admit when one more would pass the limits: an answer one early.</summary>
    private bool AnswersOneEarly(JournalPlanning planning, ArchiveCommand append)
    {
        if (ReplaceRequiredMutation != ReplaceRequiredMutant.AnswersWhenBothAdmit)
        {
            return false;
        }

        var after = _store.Begin(planning.Admitted.End());
        after.Add(append);
        return !after.Admits(append);
    }

    /// <summary>Returns whether <paramref name="state"/> is deleted and its tombstone's deletion time plus the retention is at or before the admission's clock.</summary>
    private bool RetentionPassed(JournalState? state) => RetentionPassed(state, _timeProvider.GetUtcNow().UtcTicks);

    /// <summary>Returns whether <paramref name="state"/> is deleted and its tombstone's deletion time plus the retention is at or before <paramref name="nowTicks"/>.</summary>
    private bool RetentionPassed(JournalState? state, long nowTicks)
    {
        if (IgnoreRetentionMutant)
        {
            return state?.DeletedAt is not null;
        }

        // Subtracting the retention from the clock cannot overflow, where adding it to the deletion time could.
        return state?.DeletedAt is { } deleted && deleted.UtcTicks <= nowTicks - _tombstoneRetention.Ticks;
    }

    /// <summary>
    /// Raises a debug assertion for a defect on a worker of its own, so the admission's own work goes on answering
    /// (verification.md §11.2).
    /// </summary>
    [Conditional("DEBUG")]
    private static void ReportDefect(Exception exception) =>
        _ = Task.Run(() => Debug.Fail($"Journals admission met a fault of its own: {exception}"), CancellationToken.None);

    private static void StartDetached(Action work)
    {
        var suppress = !ExecutionContext.IsFlowSuppressed();
        var flow = suppress ? ExecutionContext.SuppressFlow() : default;
        try
        {
            _ = Task.Run(work, CancellationToken.None);
        }
        finally
        {
            if (suppress)
            {
                flow.Undo();
            }
        }
    }

    private Task<JournalWriteOutcome> Submit(JournalEntryRecord record, CancellationToken cancellationToken, out bool mayMove)
    {
        var entry = record.Entry;
        if (!_range.Contains(record.Key.Span))
        {
            mayMove = false;
            return Task.FromResult(new JournalWriteOutcome(new JournalMoved(Owner: null)));
        }

        Debug.Assert(entry.Kind != JournalEntryKind.Append || entry.PayloadLength > 0, "No record holds an empty Append (journals.md §7).");

        // Pre-admission size guard for entries with attached messages (journals.md §8.1 step 1).
        var rowLength = ComputeEntryRowLength(record);
        if (entry.HasMessages)
        {
            var rowCount = 1 + entry.Messages.Length;
            var minLimit = Math.Min(_maxBatchLength, Math.Min(_maxQueuedBytes, _maxProposalBytes));
            var singleBatchSize = LedgerBatch.MaxHeaderLength(rowCount) + rowLength;
            if (singleBatchSize > minLimit)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The entry and its {entry.Messages.Length} messages require {rowCount} rows and {singleBatchSize} bytes, exceeding the admission limit ({minLimit} bytes)."),
                    nameof(record));
            }
        }

        // The constructor checked that a row of the longest key and body fits a batch, so the row's length fits an int.
        var after = default(After);
        Task<JournalWriteOutcome> outcome;
        var inlineBatch = default(PendingBatch);
        var inlineBatchLength = 0;
        long inlineBatchNumber = 0;
        lock (_lock)
        {
            outcome = SubmitLocked(record, (int)rowLength, ref after, cancellationToken, out var pending, out mayMove);
            if (after.Pump && TryStartPumping())
            {
                (inlineBatch, inlineBatchLength) = TakeBatch();
                _outstanding++;
                inlineBatchNumber = ++_batchesTaken;
                DequeueBatchBytes(inlineBatch, ref after);
                after.Pump = false;
            }
            else
            {
                ResolvePumpLocked(ref after);
            }

            if (!pending.Taken && !pending.IsAnswered)
            {
                pending.EnsureRetained();
            }
        }

        Run(ref after);
        if (inlineBatch.Count > 0)
        {
            RunInlineFirstBatch(inlineBatch, inlineBatchLength, inlineBatchNumber);
        }

        return outcome;
    }

    private static long ComputeEntryRowLength(JournalEntryRecord record)
    {
        var entry = record.Entry;
        var total = LedgerBatch.MaxRowLength(record.Key.Length, entry.ExpectedVersion.Base.Length, Pending.TokenReservation, entry.RowBodyLength);
        if (entry.HasMessages)
        {
            var messages = entry.Messages;
            for (var i = 0; i < messages.Length; i++)
            {
                var item = messages[i];
                total += LedgerBatch.MessageLength(item.Channel, item.Payload.Length);
            }
        }

        return total;
    }

    private Task<JournalWriteOutcome> SubmitLocked(
        JournalEntryRecord record,
        int rowLength,
        ref After after,
        CancellationToken cancellationToken,
        out Pending pending,
        out bool mayMove)
    {
        Debug.Assert(_lock.IsHeldByCurrentThread, "SubmitLocked requires the admission lock.");
        Debug.Assert(LedgerBatch.MaxHeaderLength(1 + record.Entry.Messages.Length) + rowLength <= _maxBatchLength, "The row of any key and body admission takes fits a batch, which the constructor and pre-admission check verified.");
        pending = new Pending(record, rowLength, ownsPayload: false);
        if (_failure is { } failure)
        {
            after.Fail(pending, failure);
        }
        else if (_fenced || _draining)
        {
            after.Answer(pending, FencedOutcome);
        }
        else if (_waitingForRoom.Count > 0 || !HasRoom(pending.RowLength))
        {
            _waitingForRoom.Enqueue(pending);
        }
        else
        {
            Accept(pending, ref after);
        }

        // An entry already admitted into InFlight, already answered synchronously inside SubmitLocked (Fenced,
        // Rejected, ReplaceRequired, or fault), or waiting on a loaded key that has already written to this ledger
        // (which is pinned non-idle and never reloads) can never complete with a same-node JournalMoved.
        mayMove = !pending.Admitted
            && !pending.IsAnswered
            && !(pending.Key is { Load: KeyLoad.Loaded, Planning.Segment: { } segment } && segment.Ledger == _ledger);
        return cancellationToken.CanBeCanceled ? pending.Outcome.WaitAsync(cancellationToken) : pending.Outcome;
    }

    /// <summary>Returns whether an entry of <paramref name="rowLength"/> fits among those accepted and not yet proposed. Under the lock.</summary>
    private bool HasRoom(int rowLength) => _queuedBytes == 0 || _queuedBytes + rowLength <= _maxQueuedBytes;

    /// <summary>
    /// Accepts a write: its key's entries decide it once the key's state is loaded. An <c>Expired</c> never loads its key
    /// (journals.md §8.6): one whose key holds no loaded warm state is rejected at once, with no effect and no history read,
    /// reporting its own expected version, a hint which names no state the admission read; and one of a loaded key with
    /// anything waiting or in flight is rejected at once (§8.1, step 3). An <c>Expired</c> is no touch of its key: once
    /// decided, its key returns to the least recently used end of warm state. Under the lock.
    /// </summary>
    private void Accept(Pending pending, ref After after)
    {
        var expiry = pending.Entry.Kind == JournalEntryKind.Expired;
        var found = _keys.TryGetValue(pending.RowKey, out var key);
        if (expiry)
        {
            if (!found || key!.Load != KeyLoad.Loaded)
            {
                RecordCold(pending);
                after.Answer(pending, new JournalWriteOutcome(new JournalWriteOutcome.Rejected(pending.Entry.ExpectedVersion)));
                return;
            }

            if (!key.Waiting.IsEmpty || key.InFlightCount != 0)
            {
                Record(key, pending);
                after.Answer(pending, Rejected(key));
                return;
            }
        }

        _queuedBytes += pending.RowLength;
        if (found)
        {
            _warmState.Touch(key!.Warm);
        }
        else
        {
            var warmEntry = _warmState.Touch(pending.RowKey, KeyAdmission.Overhead);
            key = new KeyAdmission(warmEntry);
            _keys.Add(pending.RowKey, key);
        }

        key.IdlesLeastRecent = expiry;
        pending.Key = key;
        pending.AcceptedAt = ++_sequence;
        key.Waiting.Enqueue(pending);
        switch (key.Load)
        {
            case KeyLoad.Loaded:
                Drain(key, ref after);
                break;
            case KeyLoad.Unloaded:
                key.Load = KeyLoad.Loading;
                key.LoadStartedAt = ++_sequence;
                Enter();
                (after.Loads ??= []).Add(key);
                break;
            case KeyLoad.Loading:
                if (RejectionMutation == RejectionMutant.DecidesWhileLoading)
                {
                    Drain(key, ref after);
                }

                break;
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Accepts the writes waiting for room, in arrival order, while they fit. Under the lock.</summary>
    private void AcceptWaitingForRoom(ref After after)
    {
        if (_acceptingRoom)
        {
            return;
        }

        _acceptingRoom = true;
        while (!_fenced && _waitingForRoom.TryPeek(out var next) && HasRoom(next.RowLength))
        {
            Accept(_waitingForRoom.Dequeue(), ref after);
        }

        _acceptingRoom = false;
    }

    /// <summary>Removes an entry from those accepted and not yet proposed. Under the lock.</summary>
    private void Unqueue(Pending pending, ref After after)
    {
        _queuedBytes -= pending.RowLength;
        Debug.Assert(_queuedBytes >= 0, "The queued bytes count each accepted entry once.");
        AcceptWaitingForRoom(ref after);
    }

    /// <summary>Decides the key's waiting entries in order, until one waits. Under the lock.</summary>
    private void Drain(KeyAdmission key, ref After after)
    {
        while (key.Waiting.TryPeek(out var head))
        {
            Decision decision;
            try
            {
                decision = Decide(key, head);
                if (decision == Decision.Admit)
                {
                    key.Planning!.AddAdmitted(_store, head.Command);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A fault of the admission's own, as of the store's folds: the entry fails with it, which asserts
                // nothing, and the admission fences and abandons its writer once out of the lock.
                _ = key.Waiting.Dequeue();
                _queuedBytes -= head.RowLength;
                after.Fail(head, exception);
                Fence(ref after);
                after.Defect ??= exception;
                return;
            }

            switch (decision)
            {
                case Decision.Admit:
                    _ = key.Waiting.Dequeue();
                    head.Admitted = true;
                    key.AddInFlight(head);
                    CountInFlight(head.RowKey, 1);
                    if (ApplyOnAdmitMutant)
                    {
                        var admittedVersion = new JournalVersion(head.Entry.ExpectedVersion.Base, head.Entry.ExpectedVersion.Count + 1);
                        _warmState.SetApplied(key.Warm, JournalState.Apply(key.Applied, head.Entry, admittedVersion));
                    }

                    _unbatched.Enqueue(head);
                    _unbatchedBytes += head.RowLength;
                    after.Pump = true;
                    break;
                case Decision.Reject when ReloadsBeforeRejecting(key, head):
                    // The key's warm version came from rows which may since have dropped, so a read may name its state by
                    // the rebased version (journals.md §8.3): the key loads again, and the entry is decided once more.
                    Debug.Assert(!_fenced, "A fenced admission decides nothing, so it never reloads.");
                    key.Load = KeyLoad.Loading;
                    key.LoadStartedAt = ++_sequence;
                    key.Reloading = true;
                    Enter();
                    (after.Loads ??= []).Add(key);
                    JournalMetrics.RecordReload();
                    return;
                case Decision.Reject:
                    _ = key.Waiting.Dequeue();
                    Record(key, head);
                    after.Answer(head, Rejected(key));
                    Unqueue(head, ref after);
                    break;
                case Decision.Overflow:
                    _ = key.Waiting.Dequeue();
                    after.Fail(head, new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The {head.Entry.Kind} applies at version {head.Entry.ExpectedVersion}, a state no row can advance: its count is the u64 maximum.")));
                    Unqueue(head, ref after);
                    break;
                case Decision.ReplaceRequired:
                    _ = key.Waiting.Dequeue();
                    RecordReplaceRequired(key, head);
                    after.Answer(head, ReplaceRequiredOutcome);
                    Unqueue(head, ref after);
                    break;
                case Decision.Wait:
                    return;
                default:
                    throw new UnreachableException();
            }
        }

        MaybeIdle(key);
    }

    /// <summary>
    /// Returns whether a rejection of <paramref name="head"/> waits for its key to load again (journals.md §8.3): its
    /// expected version is not the key's warm one; the key has no recorded segment in this admission's ledger, so its warm
    /// version came from rows which may since have dropped, or from the archive alone, which may since have forgotten the
    /// key; nothing of the key is in flight, so it may load (JS-I6); and its latest load started before the entry arrived,
    /// so a burst of entries rejected together reloads once. An <c>Expired</c> never loads its key (§8.6). Under the lock.
    /// </summary>
    private bool ReloadsBeforeRejecting(KeyAdmission key, Pending head) =>
        ReloadMutation == ReloadMutant.None
        && key.Load == KeyLoad.Loaded
        && head.Entry.Kind != JournalEntryKind.Expired
        && key.InFlightCount == 0
        && key.LoadStartedAt < head.AcceptedAt
        && key.Planning?.Segment?.Ledger != _ledger
        && head.Entry.ExpectedVersion != (key.Applied?.CurrentVersion ?? JournalVersion.Origin);

    /// <summary>
    /// Lists a loaded key with nothing waiting or in flight as idle, at the most recently used end, or at the least recently
    /// used end when an <c>Expired</c> was its last entry, which is no touch (journals.md §8.6); and evicts past warm state's
    /// budget. Under the lock.
    /// </summary>
    private void MaybeIdle(KeyAdmission key)
    {
        var inFlight = key.InFlightCount != 0 && EvictionMutation != EvictionMutant.EvictWithInFlight;
        if (_fenced || key.Evicted || key.Load != KeyLoad.Loaded || !key.Waiting.IsEmpty || inFlight || key.Warm.IsIdle)
        {
            return;
        }

        var leastRecent = key.IdlesLeastRecent;
        key.IdlesLeastRecent = false;
        _warmState.IdleAndEvict(key.Warm, leastRecent, _evictWarmEntry);
    }

    private void EvictWarmEntry(JournalWarmState.WarmEntry victim)
    {
        if (_keys.TryGetValue(victim.RowKey, out var existing))
        {
            Debug.Assert(existing.Waiting.IsEmpty, "A key is evicted only with nothing waiting (JS-I6).");
        }

        AssertNoneInFlight(victim.RowKey, "A key is evicted only with nothing in flight (JS-I6).");
        _ = _keys.Remove(victim.RowKey);
    }

    /// <summary>
    /// Returns whether the pump should open a batch now under <see cref="_lock"/>: always when idle
    /// (<c>_outstanding == 0</c>), in deterministic inline mode, while draining for cooperative release, while writers
    /// wait for queue room, or once unbatched entries reach <c>MaxBatchEntries</c> or <c>CoalesceThresholdBytes</c>;
    /// otherwise lets small concurrent writes coalesce behind the in-flight batch until it completes (journals.md §8.1).
    /// </summary>
    private bool CanProposeLocked() =>
        _outstanding < _window
        && (_outstanding == 0
            || _inline
            || _draining
            || _waitingForRoom.Count > 0
            || _unbatched.Count >= _maxBatchEntries
            || _unbatchedBytes >= _coalesceThresholdBytes);

    /// <summary>Starts the pump when entries wait for a batch and the admission should propose now. Under the lock.</summary>
    private bool TryStartPumping()
    {
        if (_pumping || _fenced || _unbatched.Count == 0 || !CanProposeLocked())
        {
            return false;
        }

        _pumping = true;
        Enter();
        return true;
    }

    /// <summary>Resolves a pending pump request under the lock so <see cref="Run"/> does not re-acquire the lock.</summary>
    private void ResolvePumpLocked(ref After after)
    {
        Debug.Assert(_lock.IsHeldByCurrentThread, "ResolvePumpLocked requires the admission lock.");
        if (after.Pump)
        {
            after.Pump = false;
            if (TryStartPumping())
            {
                after.StartPump = true;
            }
        }

        TryCompleteDrainLocked(ref after);
    }

    /// <summary>Subtracts the bytes of every entry in <paramref name="entries"/> from <see cref="_queuedBytes"/> and admits waiting writers. Under the lock.</summary>
    private void DequeueBatchBytes(PendingBatch entries, ref After after)
    {
        Debug.Assert(_lock.IsHeldByCurrentThread, "DequeueBatchBytes requires the admission lock.");
        for (var index = 0; index < entries.Count; index++)
        {
            _queuedBytes -= entries[index].RowLength;
        }

        Debug.Assert(_queuedBytes >= 0, "The queued bytes count each accepted entry once.");
        AcceptWaitingForRoom(ref after);
    }

    /// <summary>
    /// Admits work which serves every writer: a load or the pump, which leaves the gate when it ends
    /// (<see cref="LoadAndLeaveAsync"/>, <see cref="PumpAndLeave"/>). Under the lock, so the work is admitted before the
    /// admission fences, and so before disposal closes the gate.
    /// </summary>
    private void Enter()
    {
        var entered = _work.TryEnterUnscoped();
        Debug.Assert(entered, "Disposal closes the gate only after the admission fenced, and no work starts after that.");
    }

    /// <summary>Does what a section under the lock left for after it: answers writes, starts loads, and starts the pump.</summary>
    private void Run(ref After after)
    {
        Debug.Assert(!after.Pump, "Callers resolve Pump under the lock via ResolvePumpLocked before Run.");
        var answeredSingle = after.AnsweredSingle;
        var answeredMany = after.AnsweredMany;
        var applied = after.Applied;
        var loads = after.Loads;
        var startPump = after.StartPump;
        var defect = after.Defect;
        after = default;
        answeredSingle?.Publish();
        if (answeredMany is not null)
        {
            foreach (var pending in answeredMany)
            {
                pending.Publish();
            }
        }

        if (applied is not null)
        {
            foreach (var (waiter, result) in applied)
            {
                _ = waiter.TrySetResult(result);
            }
        }

        if (loads is not null)
        {
            foreach (var key in loads)
            {
                StartLoad(key);
            }
        }

        if (defect is not null)
        {
            _abandon();
            ReportDefect(defect);
        }

        if (!startPump)
        {
            return;
        }

        if (_inline)
        {
            PumpAndLeave();
        }
        else
        {
            StartDetached(_runPump);
        }
    }

    /// <summary>
    /// Runs the first batch taken inline by <see cref="Submit"/> in the execution context of the admission's owner
    /// (conventions.md §12). When batch creation and proposal complete synchronously and no further entries wait, the
    /// pump finishes without hopping to the thread pool or waking the completions channel; otherwise it hands off to the
    /// pump loop on the thread pool (or inline when <see cref="_inline"/> is set).
    /// </summary>
    private void RunInlineFirstBatch(PendingBatch entries, int length, long batchNumber)
    {
        if (_ownerContext is not null)
        {
            // _pumping is held exclusively across this synchronous call, so stashing the arguments avoids a closure/tuple allocation.
            _inlineEntries = entries;
            _inlineLength = length;
            _inlineBatchNumber = batchNumber;
            ExecutionContext.Run(_ownerContext, InlineFirstBatchInContext, this);
            return;
        }

        var suppress = !ExecutionContext.IsFlowSuppressed();
        var flow = suppress ? ExecutionContext.SuppressFlow() : default;
        try
        {
            _ = RunInlineFirstBatchCoreAsync(entries, length, batchNumber);
        }
        finally
        {
            if (suppress)
            {
                flow.Undo();
            }
        }
    }

    private void RunStashedInlineFirstBatch()
    {
        var entries = _inlineEntries;
        var length = _inlineLength;
        var batchNumber = _inlineBatchNumber;
        _inlineEntries = default;
        _ = RunInlineFirstBatchCoreAsync(entries, length, batchNumber);
    }

    private async Task RunInlineFirstBatchCoreAsync(PendingBatch entries, int length, long batchNumber)
    {
        var handedOff = false;
        try
        {
            LedgerBatch batch;
            var singleBatchInline = true;
            try
            {
                // The admission's disposal ends a creation's wait for its writer's budget, which an outage may prolong.
                var batchTask = _createBatch(entries.TotalRows, length, _disposing.Token);
                if (!batchTask.IsCompletedSuccessfully)
                {
                    // Retain any borrowed payload before suspending while the caller's record is still alive.
                    entries.RetainBorrowedPayloads();
                    singleBatchInline = false;
                }

                batch = await batchTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ObjectDisposedException || exception is OperationCanceledException && _disposing.IsCancellationRequested)
            {
                FenceDisposed(entries);
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Defect(entries, exception);
                return;
            }

            var result = await ProposeAsync(entries, batch, batchNumber, singleBatchInline).ConfigureAwait(false);
            if (!singleBatchInline)
            {
                if (result == ProposeResult.ContinuePumping)
                {
                    await PumpAsync().ConfigureAwait(false);
                }

                return;
            }

            switch (result)
            {
                case ProposeResult.Stopped:
                case ProposeResult.CompletedAndIdle:
                    break;
                case ProposeResult.ContinuePumping:
                    handedOff = true;
                    if (_inline)
                    {
                        PumpAndLeave();
                    }
                    else
                    {
                        StartDetached(_runPump);
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }
        finally
        {
            if (!handedOff)
            {
                _work.Exit();
            }
        }
    }

    /// <summary>
    /// Starts a key's load, which serves every writer of the key, in the execution context of the admission's owner rather
    /// than of the writer whose call started it (conventions.md §12): on the thread pool, or on this thread when it runs
    /// inline. Never under the lock.
    /// </summary>
    private void StartLoad(KeyAdmission key)
    {
        if (_inline)
        {
            _ = LoadAndLeaveAsync(key);
        }
        else if (_ownerContext is null)
        {
            StartDetached(() => _ = LoadAndLeaveAsync(key));
        }
        else
        {
            StartDetached(() => ExecutionContext.Run(_ownerContext, LoadInContext, (this, key)));
        }
    }

    /// <summary>Runs the pump on the thread pool in the execution context of the admission's owner.</summary>
    private void RunPump()
    {
        if (_ownerContext is null)
        {
            PumpAndLeave();
        }
        else
        {
            ExecutionContext.Run(_ownerContext, PumpInContext, this);
        }
    }

    /// <summary>Runs the pump, and then leaves the gate it entered (<see cref="Enter"/>).</summary>
    private void PumpAndLeave() => _ = PumpAndLeaveAsync();

    /// <summary>Runs the pump, which may wait for its writer's batch creations, and then leaves the gate it entered.</summary>
    private async Task PumpAndLeaveAsync()
    {
        try
        {
            await PumpAsync().ConfigureAwait(false);
        }
        finally
        {
            _work.Exit();
        }
    }

    /// <summary>Runs a key's load, and then leaves the gate it entered (<see cref="Enter"/>).</summary>
    private async Task LoadAndLeaveAsync(KeyAdmission key)
    {
        try
        {
            await LoadAsync(key).ConfigureAwait(false);
        }
        finally
        {
            _work.Exit();
        }
    }

    /// <summary>
    /// Proposes batches of the admitted entries in admission order while the window has room. Only one pump runs at a
    /// time, so batches reach the writer in order, which gives them slots in order at their creation (ledgers.md §2.4);
    /// it holds no lock across a creation, which may wait for the writer's earlier proposals to be decided.
    /// </summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            var after = default(After);
            PendingBatch entries = default;
            int length = 0;
            long batchNumber = 0;
            lock (_lock)
            {
                if (_fenced || _unbatched.Count == 0 || !CanProposeLocked())
                {
                    _pumping = false;
                    TryCompleteDrainLocked(ref after);
                }
                else
                {
                    (entries, length) = TakeBatch();
                    _outstanding++;
                    batchNumber = ++_batchesTaken;

                    // Every taken entry leaves the queued bytes before any waiting write is accepted, which may fence the
                    // admission and answer every entry still queued.
                    DequeueBatchBytes(entries, ref after);
                    after.Pump = false;
                }
            }

            if (after.Applied is not null && length == 0)
            {
                Run(ref after);
                return;
            }

            if (length == 0)
            {
                return;
            }

            Run(ref after);
            if (!await ProposeAsync(entries, length, batchNumber).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Creates one batch, writes it, and proposes it, or fences the admission when that fails; returns whether to go on.</summary>
    private async ValueTask<bool> ProposeAsync(PendingBatch entries, int length, long batchNumber)
    {
        LedgerBatch batch;
        try
        {
            // The admission's disposal ends a creation's wait for its writer's budget, which an outage may prolong.
            batch = await _createBatch(entries.TotalRows, length, _disposing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException || exception is OperationCanceledException && _disposing.IsCancellationRequested)
        {
            // The writer or the admission was disposed before the batch was created: nothing was appended, and nothing
            // will be.
            FenceDisposed(entries);
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Defect(entries, exception);
            return false;
        }

        return await ProposeAsync(entries, batch, batchNumber, singleBatchInline: false).ConfigureAwait(false) == ProposeResult.ContinuePumping;
    }

    /// <summary>
    /// Plans the batch's rows' tokens, writes it, and proposes it, or fences the admission when that fails. When the
    /// proposal completes synchronously and every earlier batch has already ended (<see cref="_batchesEnded"/> equals
    /// <paramref name="batchNumber"/> minus one), it applies and answers the batch directly in strict slot order without
    /// hopping through the completions channel.
    /// </summary>
    private async ValueTask<ProposeResult> ProposeAsync(PendingBatch entries, LedgerBatch batch, long batchNumber, bool singleBatchInline)
    {
        ValueTask<ProposalOutcome> outcome;
        var proposedSlot = batch.Slot;
        try
        {
            PlanTokens(entries, batch);
            var offTheFold = ChainMutation == ChainMutant.VersionOffTheFold;
            for (var index = 0; index < entries.Count; index++)
            {
                entries[index].WriteTo(batch, offTheFold);
            }

            if (OnDurableBeforeQuorumMutant && entries.HasMessages && proposedSlot is { } slot)
            {
                // JS-I12 race mutant: fires OnBatchDurable before quorum durability.
                InvokeMessageSessions(entries, slot);
            }

            outcome = _propose(batch, CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // The writer was disposed before the proposal: nothing was appended, and nothing will be.
            FenceDisposed(entries);
            return ProposeResult.Stopped;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Defect(entries, exception);
            return ProposeResult.Stopped;
        }
        finally
        {
            batch.Dispose();
            entries.ReleasePayloads();
        }

        if (outcome.IsCompletedSuccessfully && !entries.HasMessages)
        {
            var completed = await outcome.ConfigureAwait(false);
            var after = default(After);
            var fastApplied = false;
            var stopPumping = false;
            lock (_lock)
            {
                if (_batchesEnded == batchNumber - 1 && _messageCallbacksPending == 0)
                {
                    fastApplied = true;
                    ApplyOutcomeLocked(entries, completed, ref after);
                    EndBatch(ref after);
                    after.Pump = false;
                    if (singleBatchInline)
                    {
                        if (_fenced || _unbatched.Count == 0 || !CanProposeLocked())
                        {
                            _pumping = false;
                            stopPumping = true;
                            TryCompleteDrainLocked(ref after);
                        }
                    }
                    else if (_fenced)
                    {
                        _pumping = false;
                        stopPumping = true;
                        TryCompleteDrainLocked(ref after);
                    }
                }
            }

            if (fastApplied)
            {
                Run(ref after);
                if (completed.Value is not ProposalOutcome.Durable)
                {
                    DisposeSessionsOnce();
                }

                if (stopPumping)
                {
                    return singleBatchInline ? ProposeResult.CompletedAndIdle : ProposeResult.Stopped;
                }

                return ProposeResult.ContinuePumping;
            }

            return EnqueueProposal(entries, ValueTask.FromResult(completed), singleBatchInline);
        }

        return EnqueueProposal(entries, outcome, singleBatchInline);
    }

    private ProposeResult EnqueueProposal(PendingBatch entries, ValueTask<ProposalOutcome> outcome, bool singleBatchInline)
    {
        if (entries.HasMessages)
        {
            lock (_lock)
            {
                _messageCallbacksPending++;
            }
        }

        var written = _proposals.Writer.TryWrite(new Proposal(entries, outcome));
        Debug.Assert(written, "The completions channel closes only after the pump ends.");
        if (singleBatchInline)
        {
            var after = default(After);
            var stopped = false;
            lock (_lock)
            {
                if (_fenced || _unbatched.Count == 0 || !CanProposeLocked())
                {
                    _pumping = false;
                    stopped = true;
                    TryCompleteDrainLocked(ref after);
                }
            }

            if (stopped)
            {
                Run(ref after);
                return ProposeResult.CompletedAndIdle;
            }
        }

        return ProposeResult.ContinuePumping;
    }

    private enum ProposeResult
    {
        Stopped,
        CompletedAndIdle,
        ContinuePumping,
    }

    /// <summary>
    /// Plans each row's token now that its batch names its segment (journals.md §8.1 step 5, JS-I10). A row in another
    /// segment than its key's recorded one is the key's defining row there: its token is the <c>End()</c> of the key's
    /// write in the recorded segment, both folds begin again from it, and the admitted fold retakes every admitted entry
    /// of the key not yet in a batch, this one first. Each row then stores the key's token and joins the written fold. A
    /// batch which took no slot names no segment, and its proposal completes <c>Fenced</c>: its rows plan nothing.
    /// </summary>
    private void PlanTokens(PendingBatch entries, LedgerBatch batch)
    {
        if (batch.Segment is not { } segment)
        {
            return;
        }

        lock (_lock)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var pending = entries[index];
                var key = pending.Key!;
                var planning = key.Planning!;
                var rolled = planning.Segment != segment;
                if (rolled)
                {
                    var token = ChainMutation == ChainMutant.TokenNotRolled ? planning.Token : planning.Written.End();
                    planning.RollTo(_store, segment, token);
                    key.RetakeUnplannedInFlight(_store, planning);
                }

                Debug.Assert(
                    pending.Command.Kind != RowKind.Append || planning.Written.Admits(pending.Command),
                    "The write an Append joins admits it, as step 4 judged (journals.md §8.1).");
                pending.Token = planning.Token;
                pending.Defines = rolled;
                pending.Planned = true;
                planning.AddWritten(_store, pending.Command);
            }
        }
    }

    /// <summary>The writer was disposed before a batch's proposal: its entries are <c>Fenced</c>, and so is the admission.</summary>
    private void FenceDisposed(PendingBatch entries)
    {
        var after = default(After);
        lock (_lock)
        {
            _pumping = false;

            // Fenced first, so the batch's end releases no apply waiter while an earlier batch is unapplied.
            AnswerAndFence(entries, FencedOutcome, ref after);
            EndBatch(ref after);
        }

        Run(ref after);
    }


    /// <summary>
    /// A fault of the admission's own while it built or proposed a batch: it proposes nothing more, fails the batch's
    /// entries with the fault, which asserts nothing, and abandons the writer, so a takeover replaces the ledger.
    /// </summary>
    private void Defect(PendingBatch entries, Exception exception)
    {
        var after = default(After);
        lock (_lock)
        {
            _pumping = false;
            for (var index = 0; index < entries.Count; index++)
            {
                after.Fail(entries[index], exception);
            }

            // Fenced first, so the batch's end releases no apply waiter while an earlier batch is unapplied.
            Fence(ref after);
            EndBatch(ref after);
        }

        Run(ref after);
        _abandon();
        ReportDefect(exception);
    }

    /// <summary>Takes the next batch's entries from the front of the queue: at least one, within the batch policy. Under the lock.</summary>
    private (PendingBatch Entries, int Length) TakeBatch()
    {
        if (_unbatched.Count == 1)
        {
            var single = _unbatched.Dequeue();
            _unbatchedBytes -= single.RowLength;
            Debug.Assert(_unbatchedBytes == 0, "Unbatched bytes equal zero when the queue empties.");
            single.Taken = true;
            var singleLength = LedgerBatch.MaxHeaderLength(single.RowCount) + single.RowLength;
            return (new PendingBatch(single), singleLength);
        }

        var rowsBytes = 0L;
        var count = 0;
        var totalRows = 0;
        var hasMessages = false;
        foreach (var pending in _unbatched)
        {
            var nextRows = totalRows + pending.RowCount;
            if (count == _maxBatchEntries
                || count > 0 && (rowsBytes + pending.RowLength > _maxBatchBytes
                    || LedgerBatch.MaxHeaderLength(nextRows) + rowsBytes + pending.RowLength > _maxBatchLength))
            {
                break;
            }

            rowsBytes += pending.RowLength;
            totalRows = nextRows;
            hasMessages |= pending.HasMessages;
            count++;
        }

        var length = LedgerBatch.MaxHeaderLength(totalRows) + rowsBytes;
        _unbatchedBytes -= rowsBytes;
        if (count == 1)
        {
            var single = _unbatched.Dequeue();
            Debug.Assert(_unbatched.Count != 0 || _unbatchedBytes == 0, "Unbatched bytes equal zero when the queue empties.");
            single.Taken = true;
            return (new PendingBatch(single), (int)length);
        }

        var entries = new Pending[count];
        for (var index = 0; index < count; index++)
        {
            var item = _unbatched.Dequeue();
            item.Taken = true;
            entries[index] = item;
        }

        Debug.Assert(_unbatched.Count != 0 || _unbatchedBytes == 0, "Unbatched bytes equal zero when the queue empties.");
        return (new PendingBatch(entries, totalRows, hasMessages), (int)length);
    }

    /// <summary>Answers each proposal's entries in proposal order, which is slot order, as the ledger decides it.</summary>
    private async Task CompleteProposalsAsync()
    {
        try
        {
            await foreach (var proposal in _proposals.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                ProposalOutcome outcome = default;
                Exception? fault = null;
                var sessionDefect = false;
                try
                {
                    outcome = await proposal.Outcome.ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // The writer's disposal ended the proposal while its view listed the ledger open, which asserts nothing.
                    outcome = new ProposalOutcome(default(ProposalOutcome.Ambiguous));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    fault = exception;
                }

                var isDurable = fault is null && outcome.Value is ProposalOutcome.Durable;
                var durableSlot = isDurable ? ((ProposalOutcome.Durable)outcome.Value).Slot : Slot.Zero;
                var hasMessages = proposal.Entries.HasMessages;
                var invokeAfterApply = isDurable && hasMessages && CompleteCallerBeforeOnDurableMutant && !OnDurableBeforeQuorumMutant;

                // JS-I12: invoke IMessageSession.OnBatchDurable strictly in slot order outside _lock AFTER
                // ProposalOutcome.Durable, BEFORE warm-state Apply and BEFORE callers receive Accepted.
                if (isDurable && hasMessages && !invokeAfterApply && !OnDurableBeforeQuorumMutant)
                {
                    try
                    {
                        InvokeMessageSessions(proposal.Entries, durableSlot);
                    }
                    catch (Exception sessionFault) when (sessionFault is not OutOfMemoryException)
                    {
                        FatalErrorHandler.StopOnDefect("JournalsMessageSession", sessionFault);
                        fault = sessionFault;
                        sessionDefect = true;
                        isDurable = false;
                    }
                }

                var after = default(After);
                lock (_lock)
                {
                    if (hasMessages)
                    {
                        _messageCallbacksPending--;
                        Debug.Assert(_messageCallbacksPending >= 0, "Pending message callback count never goes negative.");
                    }

                    if (fault is not null)
                    {
                        for (var index = 0; index < proposal.Entries.Count; index++)
                        {
                            after.Fail(proposal.Entries[index], fault);
                        }

                        Fence(ref after);
                    }
                    else
                    {
                        ApplyOutcomeLocked(proposal.Entries, outcome, ref after);
                    }

                    EndBatch(ref after);
                    after.Pump = true;
                    ResolvePumpLocked(ref after);
                }

                Run(ref after);

                if (invokeAfterApply)
                {
                    // JS-I12 race mutant: yields so callers observe Accepted before OnBatchDurable.
                    await Task.Yield();
                    InvokeMessageSessions(proposal.Entries, durableSlot);
                }

                if (isDurable && hasMessages && DisposeBeforeLastOnDurableMutant)
                {
                    // JS-I12 race mutant: disposes sessions prematurely before subsequent batches finish.
                    DisposeSessionsOnce();
                }

                if (fault is not null)
                {
                    _abandon();
                    if (!sessionDefect)
                    {
                        ReportDefect(fault);
                    }

                    DisposeSessionsOnce();
                }
                else if (!isDurable)
                {
                    // After the first non-Durable outcome (Fenced/Ambiguous), no higher slot can ever be Durable (LG-I5).
                    DisposeSessionsOnce();
                }
            }
        }
        finally
        {
            DisposeSessionsOnce();
        }
    }

    private static (MessageChannel Channel, IMessageSession Session)[] OpenMessageSessions(
        LedgerId ledger,
        FrozenDictionary<MessageChannel, IMessageHandler> handlers)
    {
        if (handlers.Count == 0)
        {
            return [];
        }

        var sorted = new MessageChannel[handlers.Count];
        var i = 0;
        foreach (var channel in handlers.Keys)
        {
            sorted[i++] = channel;
        }

        Array.Sort(sorted);
        var sessions = new (MessageChannel Channel, IMessageSession Session)[sorted.Length];
        var opened = 0;
        try
        {
            for (var index = 0; index < sorted.Length; index++)
            {
                var channel = sorted[index];
                var session = handlers[channel].OpenLedger(ledger);
                ArgumentNullException.ThrowIfNull(session);
                sessions[index] = (channel, session);
                opened++;
            }

            return sessions;
        }
        catch
        {
            for (var index = 0; index < opened; index++)
            {
                sessions[index].Session.Dispose();
            }

            throw;
        }
    }

    private void InvokeMessageSessions(PendingBatch entries, Slot slot)
    {
        Debug.Assert(
            DisposeBeforeLastOnDurableMutant || Volatile.Read(ref _sessionsDisposed) == 0,
            "IMessageSession.OnBatchDurable never runs after Dispose (JS-I12).");
        if (_messageSessions.Length == 0)
        {
            return;
        }

        if (_messageSessions.Length == 1)
        {
            if (entries.Count == 1)
            {
                var singleMessages = entries[0].Entry.Messages;
                if (!singleMessages.IsEmpty)
                {
                    _messageSessions[0].Session.OnBatchDurable(slot, singleMessages.AsSpan());
                }

                return;
            }

            var total = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                total += entries[index].Entry.Messages.Length;
            }

            if (total == 0)
            {
                return;
            }

            var records = new MessageRecord[total];
            var cursor = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                var messages = entries[index].Entry.Messages;
                for (var j = 0; j < messages.Length; j++)
                {
                    records[cursor++] = messages[j];
                }
            }

            _messageSessions[0].Session.OnBatchDurable(slot, records);
            return;
        }

        for (var s = 0; s < _messageSessions.Length; s++)
        {
            var (channel, session) = _messageSessions[s];
            var count = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                var messages = entries[index].Entry.Messages;
                for (var j = 0; j < messages.Length; j++)
                {
                    if (messages[j].Channel.Equals(channel))
                    {
                        count++;
                    }
                }
            }

            if (count == 0)
            {
                continue;
            }

            var records = new MessageRecord[count];
            var cursor = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                var messages = entries[index].Entry.Messages;
                for (var j = 0; j < messages.Length; j++)
                {
                    if (messages[j].Channel.Equals(channel))
                    {
                        records[cursor++] = messages[j];
                    }
                }
            }

            session.OnBatchDurable(slot, records);
        }
    }

    private void DisposeSessionsOnce()
    {
        if (Interlocked.Exchange(ref _sessionsDisposed, 1) != 0)
        {
            return;
        }

        for (var index = 0; index < _messageSessions.Length; index++)
        {
            _messageSessions[index].Session.Dispose();
        }
    }

    private void ApplyOutcomeLocked(PendingBatch entries, ProposalOutcome outcome, ref After after)
    {
        switch (outcome.Value)
        {
            case ProposalOutcome.Durable durable:
                Apply(entries, durable.Slot, ref after);
                break;
            case ProposalOutcome.Fenced:
                AnswerAndFence(entries, FencedOutcome, ref after);
                break;
            case ProposalOutcome.Ambiguous:
                AnswerAndFence(entries, AmbiguousOutcome, ref after);
                break;
            case null:
                throw new UnreachableException();
            default:
                throw new UnreachableException();
        }
    }

    private void AnswerAndFence(PendingBatch entries, JournalWriteOutcome outcome, ref After after)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            after.Answer(entries[index], outcome);
        }

        Fence(ref after);
    }

    /// <summary>
    /// Applies a <c>Durable</c> batch's entries to their keys' warm state, answers them <c>Accepted</c> (JS-I2), and
    /// decides again the entries waiting on them. Under the lock.
    /// </summary>
    private void Apply(PendingBatch entries, Slot slot, ref After after)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var pending = entries[index];
            var key = pending.Key!;
            key.RemoveLanded(pending);
            CountInFlight(pending.RowKey, -1);
            Debug.Assert(
                key.InFlightCount != 0 || key.Planning!.FoldsAgree(),
                "With nothing of a key in flight, its admitted fold and its written fold hold the same write (journals.md §8.1).");
            if (_failure is not null)
            {
                after.Fail(pending, _failure);
                continue;
            }

            if (!ApplyOnAdmitMutant)
            {
                try
                {
                    _warmState.SetApplied(key.Warm, JournalState.Apply(key.Applied, pending.Entry, pending.Version));
                }
                catch (JournalStoreCorruptException exception)
                {
                    // Warm state no longer folds the ledger's decided rows: a defect which stops the process (conventions.md
                    // §14). Where a test intercepts the stop, the admission answers nothing more but the failure.
                    FatalErrorHandler.StopOnDefect("JournalsWarmState", exception);
                    _failure = exception;
                    after.Fail(pending, exception);
                    Fence(ref after);
                    continue;
                }
            }

            var replaceRequested = (key.Applied?.IsReplaceRequested ?? false) || _store.IsReplaceRequested(key.Planning!.Admitted.End());
            after.Answer(pending, new JournalWriteOutcome(new JournalWriteOutcome.Accepted(pending.Version, replaceRequested)));
        }

        _appliedBelow = slot.Value + 1;
        if (RejectionMutation == RejectionMutant.NeverDecidesWaitingAgain)
        {
            return;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            Drain(entries[index].Key!, ref after);
        }
    }

    /// <summary>
    /// Admits nothing more, and answers <c>Fenced</c> every entry waiting for room, waiting on its key, or admitted and not
    /// yet proposed. Proposed entries take their proposals' outcomes. Under the lock.
    /// </summary>
    private void Fence(ref After after)
    {
        if (_fenced)
        {
            return;
        }

        _fenced = true;
        ClearInFlightCounts();
        while (_waitingForRoom.TryDequeue(out var pending))
        {
            after.Answer(pending, FencedOutcome);
        }

        while (_unbatched.TryDequeue(out var pending))
        {
            _queuedBytes -= pending.RowLength;
            _unbatchedBytes -= pending.RowLength;
            after.Answer(pending, FencedOutcome);
        }

        Debug.Assert(_unbatchedBytes == 0, "Unbatched bytes equal zero after fencing.");

        foreach (var key in _keys.Values)
        {
            while (key.Waiting.TryDequeue(out var waiting))
            {
                _queuedBytes -= waiting.RowLength;
                after.Answer(waiting, FencedOutcome);
            }
        }

        Debug.Assert(_queuedBytes == 0, "Every entry accepted and not yet proposed was waiting on its key or for a batch, and is answered.");

        foreach (var (_, _, applied) in _applyWaiters)
        {
            (after.Applied ??= []).Add((applied, false));
        }

        _applyWaiters.Clear();
        if (_drained is { } drained)
        {
            _drained = null;
            (after.Applied ??= []).Add((drained, false));
        }
    }

    /// <summary>
    /// Cooperatively drains staged and in-flight entries of this admission within <paramref name="deadline"/> while
    /// refusing newly arriving writes, aborting immediately if <paramref name="writerFenced"/> completes or the
    /// admission fences (orleans.md §5.6, OR-I24). Once drained cleanly, fences the admission so warm state is retired.
    /// </summary>
    internal async Task<LedgerAdmissionDrainResult> PrepareReleaseAsync(
        TimeSpan deadline,
        Task writerFenced,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var after = default(After);
        Task<bool>? waitDrained = null;
        lock (_lock)
        {
            if (_fenced || _failure is not null || writerFenced.IsCompleted)
            {
                return LedgerAdmissionDrainResult.Sealed;
            }

            _draining = true;
            if (_unbatched.Count > 0)
            {
                after.Pump = true;
                ResolvePumpLocked(ref after);
            }

            if (!IsIdleLocked())
            {
                _drained ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waitDrained = _drained.Task;
            }
        }

        Run(ref after);

        if (waitDrained is null)
        {
            return LedgerAdmissionDrainResult.Drained;
        }

        var keepDraining = false;
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(deadline, _timeProvider, waiting.Token);
            var winner = await Task.WhenAny(waitDrained, writerFenced, delay).ConfigureAwait(false);
            await waiting.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (winner == writerFenced)
            {
                keepDraining = true;
                return LedgerAdmissionDrainResult.Sealed;
            }

            if (winner == waitDrained)
            {
                if (!await waitDrained.ConfigureAwait(false))
                {
                    keepDraining = true;
                    return LedgerAdmissionDrainResult.Sealed;
                }

                lock (_lock)
                {
                    if (_fenced || _failure is not null || writerFenced.IsCompleted)
                    {
                        keepDraining = true;
                        return LedgerAdmissionDrainResult.Sealed;
                    }
                }

                keepDraining = true;
                return LedgerAdmissionDrainResult.Drained;
            }

            return LedgerAdmissionDrainResult.TimedOut;
        }
        finally
        {
            if (!keepDraining && !writerFenced.IsCompleted)
            {
                CancelDrain();
            }
        }
    }

    /// <summary>Resumes admitting writes after a cooperative drain aborted before the ledger writer stopped.</summary>
    internal void CancelDrain()
    {
        lock (_lock)
        {
            if (!_fenced)
            {
                _draining = false;
                _drained = null;
            }
        }
    }

    private bool IsIdleLocked()
    {
        if (_pumping || _outstanding != 0 || _unbatched.Count != 0 || _waitingForRoom.Count != 0)
        {
            return false;
        }

        foreach (var key in _keys.Values)
        {
            if (key.Load == KeyLoad.Loading || !key.Waiting.IsEmpty || key.InFlightCount != 0)
            {
                return false;
            }
        }

        return true;
    }

    private void TryCompleteDrainLocked(ref After after)
    {
        if (_draining && !_fenced && _drained is { } drained && IsIdleLocked())
        {
            _drained = null;
            (after.Applied ??= []).Add((drained, true));
        }
    }

    internal enum LedgerAdmissionDrainResult
    {
        Drained,
        Sealed,
        TimedOut,
    }

    /// <summary>Loads a key's state, then decides its waiting entries.</summary>
    private async Task LoadAsync(KeyAdmission key)
    {
        // A load may rebase, so one beside an in-flight entry could split the key's chain between that entry's row and
        // the rebased state.
        AssertNoneInFlight(key.RowKey, "A key loads only with no admitted entry in flight or waiting on in-flight outcomes (JS-I6).");
        var after = default(After);
        try
        {
            JournalLoadOutcome loaded;
            try
            {
                loaded = await _load(key.RowKey, _disposing.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (_lock)
                {
                    // The next touch loads again; the entries which waited for this load fail with its exception.
                    Unload(key);
                    while (key.Waiting.TryDequeue(out var waiting))
                    {
                        after.Fail(waiting, exception);
                        Unqueue(waiting, ref after);
                    }

                    ResolvePumpLocked(ref after);
                }

                return;
            }

            lock (_lock)
            {
                if (_fenced)
                {
                    // The admission fenced while the state loaded, and answered the key's entries then.
                    return;
                }

                switch (loaded.Value)
                {
                    case JournalLoadOutcome.Loaded found:
                        AssertSameState(key, found.Load.State);
                        _warmState.SetLoaded(key.Warm, found.Load.State, found.Load.Planning);
                        key.Load = KeyLoad.Loaded;
                        key.Reloading = false;
                        Drain(key, ref after);
                        break;
                    case JournalMoved moved:
                        AnswerMoved(key, moved, ref after);
                        break;
                    case null:
                        throw new UnreachableException("conventions.md §15: a load answers a union which holds a case.");
                    default:
                        throw new UnreachableException();
                }

                ResolvePumpLocked(ref after);
            }
        }
        finally
        {
            Run(ref after);
        }
    }

    /// <summary>
    /// Answers the writes waiting for a load whose read was not the admission's <c>Moved</c>: nothing of the key was
    /// proposed, and its next touch loads again. Under the lock.
    /// </summary>
    private void AnswerMoved(KeyAdmission key, JournalMoved moved, ref After after)
    {
        Unload(key);
        var outcome = new JournalWriteOutcome(moved);
        while (key.Waiting.TryDequeue(out var waiting))
        {
            after.Answer(waiting, outcome);
            Unqueue(waiting, ref after);
        }
    }

    /// <summary>
    /// Asserts that a reload, before a rejection, found the state the key's warm one is (journals.md §8.3): with nothing of
    /// the key in flight no command intervened, so only the version may differ, a rebased name of the same state. Debug
    /// builds only; under the lock.
    /// </summary>
    [Conditional("DEBUG")]
    private static void AssertSameState(KeyAdmission key, JournalState? reloaded)
    {
        if (!key.Reloading)
        {
            return;
        }

        var warm = key.Applied;
        Debug.Assert(
            (warm?.Exists ?? false) == (reloaded?.Exists ?? false)
            && (warm?.ContentLength ?? 0) == (reloaded?.ContentLength ?? 0)
            && warm?.DeletedAt == reloaded?.DeletedAt,
            "A reload before a rejection finds the warm state, under its version or a rebased one (journals.md §8.3).");
    }

    /// <summary>Removes a key whose load ended without a state from the admission's keys, so its next touch loads again. Under the lock.</summary>
    private void Unload(KeyAdmission key)
    {
        AssertNoneInFlight(key.RowKey, "A key whose load ended without a state has no admitted entry in flight (JS-I6).");
        _ = _keys.Remove(key.RowKey);
        _warmState.Remove(key.Warm);
    }

    /// <summary>
    /// Ends a batch the pump took: answered as the ledger decided it, or failed or fenced before its proposal, so a waiter
    /// which waits for the admission's batches no longer waits for it. Under the lock.
    /// </summary>
    private void EndBatch(ref After after)
    {
        _outstanding--;
        _batchesEnded++;
        Debug.Assert(_batchesEnded <= _batchesTaken, "The admission ends each batch it took once.");
        Debug.Assert(_outstanding != 0 || _batchesEnded == _batchesTaken, "With no batch outstanding, every batch taken has ended.");
        for (var index = _applyWaiters.Count - 1; index >= 0; index--)
        {
            var (below, takenBefore, applied) = _applyWaiters[index];
            if (IsApplied(below, takenBefore))
            {
                (after.Applied ??= []).Add((applied, true));
                _applyWaiters.RemoveAt(index);
            }
        }

        TryCompleteDrainLocked(ref after);
    }

    /// <summary>
    /// Returns whether a wait for the admission's batches below <paramref name="below"/>, which began once it had taken
    /// <paramref name="takenBefore"/> batches, is over. Under the lock.
    /// </summary>
    private bool IsApplied(Slot below, long takenBefore) => _appliedBelow >= below.Value || _batchesEnded >= takenBefore;

    /// <summary>Waits for an apply waiter, removing it from the admission's waiters if <paramref name="cancellationToken"/> fires first.</summary>
    private async Task<bool> WaitAppliedCancellablyAsync(TaskCompletionSource<bool> applied, CancellationToken cancellationToken)
    {
        try
        {
            return await applied.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                _ = _applyWaiters.RemoveAll(waiter => waiter.Applied == applied);
            }

            throw;
        }
    }

    private static JournalWriteOutcome Rejected(KeyAdmission key) =>
        new(new JournalWriteOutcome.Rejected(key.Applied?.CurrentVersion ?? JournalVersion.Origin));

    /// <summary>Records a rejection for the white-box oracle, with what it was decided from. Under the lock.</summary>
    private void Record(KeyAdmission key, Pending pending)
    {
        if (OnRejected is not { } rejected)
        {
            return;
        }

        var inFlight = key.SnapshotInFlight();
        rejected(new JournalRejection(
            pending.RowKey,
            pending.Crc,
            pending.Entry.Kind,
            pending.Entry.ExpectedVersion,
            key.Applied?.CurrentVersion ?? JournalVersion.Origin,
            key.Applied?.DeletedAt,
            new Slot(_appliedBelow),
            inFlight,
            _timeProvider.GetUtcNow(),
            _tombstoneRetention,
            key.Planning?.Segment?.Ledger == _ledger,
            key.LoadStartedAt,
            pending.AcceptedAt,
            Cold: false));
    }

    /// <summary>
    /// Records, for the white-box oracle, the rejection of an <c>Expired</c> whose key holds no loaded warm state, which
    /// reports its own expected version, decided from no state. Under the lock.
    /// </summary>
    private void RecordCold(Pending pending) =>
        OnRejected?.Invoke(new JournalRejection(
            pending.RowKey,
            pending.Crc,
            pending.Entry.Kind,
            pending.Entry.ExpectedVersion,
            pending.Entry.ExpectedVersion,
            AppliedDeletedAt: null,
            new Slot(_appliedBelow),
            [],
            _timeProvider.GetUtcNow(),
            _tombstoneRetention,
            RecordedHere: false,
            LoadStartedAt: 0,
            AcceptedAt: 0,
            Cold: true));

    /// <summary>Records a <c>ReplaceRequired</c> for the white-box oracle, with what it was decided from. Under the lock.</summary>
    private void RecordReplaceRequired(KeyAdmission key, Pending pending) =>
        OnReplaceRequired?.Invoke(new JournalReplaceRequired(pending.RowKey, pending.Crc, pending.Entry.ExpectedVersion, pending.Entry.PayloadLength, new Slot(_appliedBelow), key.InFlightCount));

    /// <summary>Returns the bytes warm state counts for a loaded key of <paramref name="rowKeyLength"/> bytes which holds a state, which tests size budgets by.</summary>
    internal static long LoadedKeyBytes(int rowKeyLength) => KeyAdmission.Overhead + rowKeyLength + JournalState.EstimatedBytes + JournalPlanning.EstimatedBytes;

    /// <summary>Gets the number of entries waiting on their keys, which the progress check reads once the run is quiet.</summary>
    internal int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _waitingForRoom.Count + _keys.Values.Sum(static key => key.Waiting.Count);
            }
        }
    }

    /// <summary>
    /// Returns the warm state and planning state of a loaded key with nothing waiting or in flight, which the cold-primary
    /// property compares with a load (journals.md §10), or <see langword="null"/>.
    /// </summary>
    internal JournalWarm? Warm(JournalRowKey rowKey)
    {
        lock (_lock)
        {
            return !_fenced && _keys.TryGetValue(rowKey, out var key) && key.Load == KeyLoad.Loaded && key.Waiting.IsEmpty && key.InFlightCount == 0
                ? JournalWarm.Of(key.Applied, key.Planning!)
                : null;
        }
    }

    /// <summary>
    /// Counts a change in a row key's entries in flight, for JS-I6's assertions, until the admission fences and forgets
    /// the counts. Debug builds only; under the lock.
    /// </summary>
    [Conditional("DEBUG")]
    private void CountInFlight(JournalRowKey rowKey, int change)
    {
        Debug.Assert(_lock.IsHeldByCurrentThread, "The counts change under the lock.");
#if DEBUG
        if (_fenced)
        {
            return;
        }

        var count = _inFlightByRowKey.GetValueOrDefault(rowKey) + change;
        Debug.Assert(count >= 0, "A key's entries land once each.");
        if (count == 0)
        {
            _ = _inFlightByRowKey.Remove(rowKey);
        }
        else
        {
            _inFlightByRowKey[rowKey] = count;
        }
#endif
    }

    /// <summary>
    /// Forgets the counts once the admission fences: it loads and evicts nothing more, and an entry answered
    /// <c>Fenced</c> or <c>Ambiguous</c> never lands. Debug builds only; under the lock.
    /// </summary>
    [Conditional("DEBUG")]
    private void ClearInFlightCounts()
    {
        Debug.Assert(_lock.IsHeldByCurrentThread, "The counts change under the lock.");
#if DEBUG
        _inFlightByRowKey.Clear();
#endif
    }

    /// <summary>Asserts that no admitted entry of <paramref name="rowKey"/> is in flight, under any of its key's admissions (JS-I6). Debug builds only.</summary>
    [Conditional("DEBUG")]
    private void AssertNoneInFlight(JournalRowKey rowKey, string message)
    {
        lock (_lock)
        {
#if DEBUG
            Debug.Assert(!_inFlightByRowKey.ContainsKey(rowKey), message);
#endif
        }
    }

    private async Task DisposeCoreAsync()
    {
        var after = default(After);
        lock (_lock)
        {
            Fence(ref after);
        }

        Run(ref after);
        try
        {
            await _disposing.CancelAsync().ConfigureAwait(false);
            await _work.CloseAsync().ConfigureAwait(false);
            _ = _proposals.Writer.TryComplete();
            await _completions.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _keys.Clear();
                _warmState.Clear();
            }

            _disposing.Dispose();
            _ownerContext?.Dispose();
        }
    }

    /// <summary>
    /// What a section under the lock leaves for after it: writes to answer (with a single-entry fast slot so 1-entry
    /// batches allocate no list), keys to load, whether to start the pump, and a fault of the admission's own, after which
    /// it abandons its writer.
    /// </summary>
    private struct After
    {
        public Pending? AnsweredSingle;
        public List<Pending>? AnsweredMany;
        public List<KeyAdmission>? Loads;
        public List<(TaskCompletionSource<bool> Applied, bool Result)>? Applied;
        public bool Pump;
        public bool StartPump;
        public Exception? Defect;

        public void Answer(Pending pending, JournalWriteOutcome outcome)
        {
            pending.SetAnswer(outcome);
            AddAnswered(pending);
        }

        public void Fail(Pending pending, Exception failure)
        {
            pending.SetFailure(failure);
            AddAnswered(pending);
        }

        private void AddAnswered(Pending pending)
        {
            if (AnsweredSingle is null && AnsweredMany is null)
            {
                AnsweredSingle = pending;
                return;
            }

            if (AnsweredMany is null)
            {
                AnsweredMany = [AnsweredSingle!, pending];
                AnsweredSingle = null;
                return;
            }

            AnsweredMany.Add(pending);
        }
    }

    /// <summary>One batch of entries taken from <see cref="_unbatched"/>: inline single entry when count is one, avoiding a <see cref="Pending"/> array allocation.</summary>
    private readonly struct PendingBatch
    {
        private readonly Pending? _single;
        private readonly Pending[]? _many;

        public PendingBatch(Pending single)
        {
            _single = single;
            TotalRows = single.RowCount;
            HasMessages = single.HasMessages;
        }

        public PendingBatch(Pending[] many, int totalRows, bool hasMessages)
        {
            _many = many;
            TotalRows = totalRows;
            HasMessages = hasMessages;
        }

        public int Count => _many is not null ? _many.Length : _single is not null ? 1 : 0;

        public int TotalRows { get; }

        public bool HasMessages { get; }

        public Pending this[int index] => _many is not null ? _many[index] : _single!;

        public void RetainBorrowedPayloads()
        {
            if (_many is not null)
            {
                foreach (var pending in _many)
                {
                    pending.EnsureRetained();
                }
            }
            else
            {
                _single?.EnsureRetained();
            }
        }

        public void ReleasePayloads()
        {
            if (_many is not null)
            {
                foreach (var pending in _many)
                {
                    pending.ReleasePayload();
                }
            }
            else
            {
                _single?.ReleasePayload();
            }
        }
    }

    /// <summary>One batch proposed, with its entries in row order and its outcome.</summary>
    private readonly record struct Proposal(PendingBatch Entries, ValueTask<ProposalOutcome> Outcome);

    /// <summary>
    /// A small FIFO queue of <see cref="Pending"/> entries with a single inline slot so uncontended keys allocate no
    /// <see cref="Queue{T}"/>.
    /// </summary>
    private struct PendingQueue
    {
        private Pending? _single;
        private Queue<Pending>? _overflow;

        public readonly bool IsEmpty => _single is null && (_overflow is null || _overflow.Count == 0);

        public readonly int Count => _single is not null ? 1 : _overflow?.Count ?? 0;

        public void Enqueue(Pending item)
        {
            if (_overflow is not null)
            {
                _overflow.Enqueue(item);
                return;
            }

            if (_single is null)
            {
                _single = item;
                return;
            }

            _overflow = new Queue<Pending>(4);
            _overflow.Enqueue(_single);
            _overflow.Enqueue(item);
            _single = null;
        }

        public readonly bool TryPeek([NotNullWhen(true)] out Pending? item)
        {
            if (_single is not null)
            {
                item = _single;
                return true;
            }

            if (_overflow is not null && _overflow.TryPeek(out item))
            {
                return true;
            }

            item = null;
            return false;
        }

        public Pending Dequeue()
        {
            if (_single is not null)
            {
                var item = _single;
                _single = null;
                return item;
            }

            return _overflow!.Dequeue();
        }

        public bool TryDequeue([NotNullWhen(true)] out Pending? item)
        {
            if (_single is not null)
            {
                item = _single;
                _single = null;
                return true;
            }

            if (_overflow is not null && _overflow.TryDequeue(out item))
            {
                return true;
            }

            item = null;
            return false;
        }

        public readonly void RetakeUnplanned(IArchiveStore store, JournalPlanning planning)
        {
            if (_single is not null)
            {
                if (!_single.Planned)
                {
                    planning.AddAdmitted(store, _single.Command);
                }

                return;
            }

            if (_overflow is null)
            {
                return;
            }

            foreach (var admitted in _overflow)
            {
                if (!admitted.Planned)
                {
                    planning.AddAdmitted(store, admitted.Command);
                }
            }
        }

        public readonly ImmutableArray<JournalRejection.InFlightEntry> Snapshot()
        {
            if (_single is not null)
            {
                return [new JournalRejection.InFlightEntry(_single.Entry.Kind, _single.Entry.ExpectedVersion)];
            }

            if (_overflow is null || _overflow.Count == 0)
            {
                return [];
            }

            var builder = ImmutableArray.CreateBuilder<JournalRejection.InFlightEntry>(_overflow.Count);
            foreach (var admitted in _overflow)
            {
                builder.Add(new JournalRejection.InFlightEntry(admitted.Entry.Kind, admitted.Entry.ExpectedVersion));
            }

            return builder.MoveToImmutable();
        }
    }

    /// <summary>
    /// A key's admission: its warm state and its planning state once loaded, its waiting entries, and its in-flight
    /// entries.
    /// </summary>
    private sealed class KeyAdmission(JournalWarmState.WarmEntry warm)
    {
        internal const long Overhead = 200;

        /// <summary>The entries not yet decided, in arrival order, while there are any; the first may wait on the in-flight entries.</summary>
        public PendingQueue Waiting;

        private PendingQueue _inFlight;

        public JournalWarmState.WarmEntry Warm { get; } = warm;

        public JournalRowKey RowKey => Warm.RowKey;

        public KeyLoad Load { get; set; }

        public JournalState? Applied => Warm.Applied;

        /// <summary>Gets the key's planning state, once loaded (journals.md §8.1).</summary>
        public JournalPlanning? Planning => Warm.Planning;

        public int InFlightCount => _inFlight.Count;

        /// <summary>Gets a value indicating whether the key left the admission's keys, evicted or after a failed load.</summary>
        public bool Evicted => Warm.Evicted;

        /// <summary>Gets or sets a value indicating whether the key loads again before a rejection (journals.md §8.3).</summary>
        public bool Reloading { get; set; }

        /// <summary>Gets or sets whether the key's last accepted entry was an <c>Expired</c>, which is no touch, so the key idles at the least recently used end.</summary>
        public bool IdlesLeastRecent { get; set; }

        /// <summary>Gets or sets where in the admission's sequence the key's latest load started.</summary>
        public long LoadStartedAt { get; set; }

        public void AddInFlight(Pending pending) => _inFlight.Enqueue(pending);

        public void RemoveLanded(Pending pending)
        {
            var landed = _inFlight.Dequeue();
            Debug.Assert(ReferenceEquals(landed, pending), "A key's entries land in the order they were admitted.");
        }

        public void RetakeUnplannedInFlight(IArchiveStore store, JournalPlanning planning) => _inFlight.RetakeUnplanned(store, planning);

        public ImmutableArray<JournalRejection.InFlightEntry> SnapshotInFlight() => _inFlight.Snapshot();
    }

    /// <summary>
    /// One write: inherits <see cref="TaskCompletionSource{TResult}"/> directly to avoid a separate TCS allocation, and
    /// holds its record (either borrowing the caller's live lease for an inline batch or retaining its own lease when
    /// queued/async), its command in its key's archive write, its key, its planned token, and its outcome.
    /// </summary>
    private sealed class Pending(JournalEntryRecord record, int rowLength, bool ownsPayload)
        : TaskCompletionSource<JournalWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        /// <summary>The token length an entry's row reserves before its batch plans the token (journals.md §8.1 step 5).</summary>
        public const int TokenReservation = RowToken.MaxLength;

        private JournalEntryRecord _record = record;
        private bool _ownsPayload = ownsPayload;
        private JournalWriteOutcome _answer;
        private Exception? _failure;

        public JournalRowKey RowKey => _record.Key;

        public JournalEntry Entry => _record.Entry;

        /// <summary>Gets the entry's command in its key's archive write, which the folds take: a <c>Delete</c>'s carries its tombstone.</summary>
        public ArchiveCommand Command { get; } = CommandOf(record);

        public int RowLength { get; } = rowLength;

        public int RowCount => 1 + _record.Entry.Messages.Length;

        public bool HasMessages => _record.Entry.HasMessages;

        public KeyAdmission? Key { get; set; }

        /// <summary>Gets the CRC-32C of the entry's record, which identifies the write.</summary>
        public uint Crc => _record.Crc32C;

        /// <summary>Gets or sets where in the admission's sequence the entry was accepted.</summary>
        public long AcceptedAt { get; set; }

        public Task<JournalWriteOutcome> Outcome => Task;

        /// <summary>Gets or sets a value indicating whether this entry was admitted into its key's in-flight queue.</summary>
        public bool Admitted { get; set; }

        /// <summary>Gets or sets a value indicating whether this entry was taken into a batch.</summary>
        public bool Taken { get; set; }

        /// <summary>Gets a value indicating whether this entry already has a synchronous answer or failure staged.</summary>
        public bool IsAnswered { get; private set; }

        /// <summary>Gets or sets the token the entry's row stores, which its batch plans.</summary>
        public RowToken Token { get; set; }

        /// <summary>Gets or sets a value indicating whether the entry's row is its key's defining row in its batch's segment, where its token rolled.</summary>
        public bool Defines { get; set; }

        /// <summary>Gets or sets a value indicating whether the entry's batch planned its token, after which the admitted fold no longer retakes it.</summary>
        public bool Planned { get; set; }

        /// <summary>Gets the version the entry's row received, which <see cref="WriteTo"/> adopts.</summary>
        public JournalVersion Version { get; private set; }

        /// <summary>Ensures this pending entry holds its own retained lease on the record's payload.</summary>
        public void EnsureRetained()
        {
            if (!_ownsPayload)
            {
                _record = _record.Retain();
                _ownsPayload = true;
            }
        }

        /// <summary>
        /// Writes the entry's row into <paramref name="batch"/> from the verified record (journals.md §4.2, JS-I5): its
        /// key, its row kind, the version before it, which is the expected version, its planned <see cref="Token"/>, and the
        /// row's native body, the payload or a <c>Delete</c>'s tombstone. It adopts the version <c>BeginRow</c> returns
        /// (LG-I13), which is the next of the expected version, since the entry applies to the state after every in-flight
        /// entry. <paramref name="versionOffTheFold"/> is the chain mutant which stores a defining row's version before it
        /// one count past the fold's.
        /// </summary>
        public void WriteTo(LedgerBatch batch, bool versionOffTheFold = false)
        {
            var entry = _record.Entry;
            Debug.Assert(
                batch.Segment is null || batch.Defines(RowKey.Span) == Defines,
                "A key's token rolls exactly on its defining row in a segment (journals.md §8.1 step 5).");
            var before = versionOffTheFold && Defines ? entry.ExpectedVersion.Next() : entry.ExpectedVersion;
            var bodyLength = (int)entry.RowBodyLength;
            Span<byte> bodyHeadBuffer = stackalloc byte[JournalEntry.TombstoneLength];
            var bodyHead = _record.WriteRowBodyHead(bodyHeadBuffer);
            Debug.Assert(
                IsCommandOf(Command, entry.RowKind, bodyLength, bodyHead),
                "The command the folds take is the archive command of the row the entry writes (journals.md §8.1).");
            var version = batch.BeginRow(RowKey.Span, entry.RowKind, before.ToRowVersion(), Token, bodyLength, bodyHead);
            foreach (var span in _record.Payload)
            {
                batch.WriteBody(span);
            }

            if (entry.HasMessages)
            {
                var messages = entry.Messages;
                for (var i = 0; i < messages.Length; i++)
                {
                    var item = messages[i];
                    batch.BeginMessage(item.Channel, item.Payload.Length, item.Payload.Span);
                }
            }

            Version = JournalVersion.FromRowVersion(version);
            Debug.Assert(versionOffTheFold || Version == entry.ExpectedVersion.Next(), "An admitted entry's row advances its expected version by one (JS-I1, LG-I13).");
        }

        /// <summary>
        /// Returns whether <paramref name="command"/> is the archive command of a row of <paramref name="kind"/> whose body
        /// is <paramref name="bodyLength"/> bytes beginning with <paramref name="bodyHead"/>: a <c>Delete</c>'s body is its
        /// tombstone, whole in its head, and every other kind's is its content.
        /// </summary>
        private static bool IsCommandOf(ArchiveCommand command, RowKind kind, int bodyLength, ReadOnlySpan<byte> bodyHead) =>
            command.Kind == kind && (kind == RowKind.Delete
                ? command.ContentLength == 0 && bodyHead.Length == bodyLength && (command.Tombstone ?? default).Span.SequenceEqual(bodyHead)
                : command.ContentLength == bodyLength && command.Tombstone is null);

        private static ArchiveCommand CommandOf(JournalEntryRecord record)
        {
            var entry = record.Entry;
            if (entry.Kind == JournalEntryKind.Append)
            {
                return ArchiveCommand.Append((int)entry.RowBodyLength);
            }

            if (entry.Kind == JournalEntryKind.Replace)
            {
                return ArchiveCommand.Replace((int)entry.RowBodyLength);
            }

            if (entry.Kind == JournalEntryKind.Delete)
            {
                var tombstone = new byte[JournalEntry.TombstoneLength];
                _ = record.WriteRowBodyHead(tombstone);
                return ArchiveCommand.Delete(tombstone);
            }

            if (entry.Kind == JournalEntryKind.Expired)
            {
                return ArchiveCommand.Delete(default);
            }

            throw new UnreachableException();
        }

        /// <summary>Releases the lease on the entry's payload, once a batch holds its bytes or the write is answered.</summary>
        public void ReleasePayload()
        {
            if (_ownsPayload)
            {
                _ownsPayload = false;
                _record.Dispose();
            }

            _record = _record.WithoutPayload();
        }

        public void SetAnswer(JournalWriteOutcome outcome)
        {
            _answer = outcome;
            IsAnswered = true;
        }

        public void SetFailure(Exception failure)
        {
            _failure = failure;
            IsAnswered = true;
        }

        /// <summary>Releases the payload and completes the write with its answer, exactly once. Outside the lock.</summary>
        public void Publish()
        {
            ReleasePayload();
            var completed = _failure is { } failure ? TrySetException(failure) : TrySetResult(_answer);
            Debug.Assert(completed, "Every write resolves to exactly one outcome (JS-I4).");
        }
    }
}
