using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Polly;
using Polly.Retry;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Control;

/// <summary>The retry of <see cref="ArchiveLogWriter"/>'s calls whose outcome is unknown (archive-logs.md §6, §8.2).</summary>
/// <remarks>
/// The store's own client retries each request, as the Azure SDK does under the options the composition sets, honouring
/// <c>Retry-After</c>; that is the one retry layer of a request. The writer only retries a call which still threw, once
/// by default, so a call's attempts are the client's, at most doubled.
/// </remarks>
public sealed record ArchiveLogWriterOptions
{
    /// <summary>Gets the retries of a call which throws, after which its exception reaches the caller; 1 by default, 0 for none.</summary>
    public int MaxRetries { get; init; } = 1;

    /// <summary>Gets the delay before each retry, which doubles from one to the next; 100 ms by default.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// Writes one key's write for a segment to its archive log (archive-logs.md §8.2): it decodes the token the write carries
/// and plans the write's requests from it with no I/O, an append run of the key's appends packed into units from the
/// token's position, or an opener of the next generation with the write's content as its base. It holds no state between
/// calls, and only it calls <see cref="IArchiveLogStore.AppendAsync"/> and <see cref="IArchiveLogStore.OpenAsync"/>
/// (README §2.5).
/// </summary>
/// <remarks>
/// <para>
/// On <see cref="ArchiveLogWriteOutcome.Applied"/> the write is held. On <see cref="ArchiveLogWriteOutcome.ConditionFailed"/>
/// the writer reads the log's properties once and classifies them (§8.3): <c>Held</c> ends the write, <c>Resume(u)</c>
/// appends from unit <c>u</c>, which lies past the unit whose condition failed, and <c>PredecessorMissing</c> returns to
/// the drain (§8.1). A log which was behind the write when its condition failed, and which its predecessor brought to the
/// write's start since, has the write issued once more: an opener's <c>Retry</c>, or a run's <c>Resume(0)</c> on its first
/// pass. Each other pass advances the run, so a write ends after at most one classification per unit and one at its
/// start; a read which would not advance it is a contradiction.
/// </para>
/// <para>
/// A call which throws is retried as is, after <see cref="ArchiveLogWriterOptions.Delay"/>, unless the caller cancelled
/// it, the store refused it, or it reports a defect or corruption; each unit lands at most once, at its position, so a
/// retry which meets its own landing fails its condition and is classified.
/// </para>
/// </remarks>
public sealed class ArchiveLogWriter
{
    private readonly IArchiveLogStore _store;
    private readonly ResiliencePipeline _retry;

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogWriter"/> class.</summary>
    /// <param name="store">The archive-log store.</param>
    /// <param name="timeProvider">The clock of the retries' delays.</param>
    /// <param name="options">The retries; by default <see cref="ArchiveLogWriterOptions"/>'s.</param>
    public ArchiveLogWriter(IArchiveLogStore store, TimeProvider timeProvider, ArchiveLogWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options ??= new ArchiveLogWriterOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetries);
        _store = store;
        _retry = options.MaxRetries == 0 ? ResiliencePipeline.Empty : new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = static arguments => ValueTask.FromResult(
                    arguments.Outcome.Exception is { } exception && IsRetried(exception, arguments.Context.CancellationToken)),
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false,
                Delay = options.Delay,
            })
            .Build();
    }

    /// <summary>
    /// Writes <paramref name="write"/> to the log <paramref name="name"/> until the log holds it, or its predecessor proves
    /// missing.
    /// </summary>
    /// <param name="name">The key's log.</param>
    /// <param name="write">The key's write for a segment, which carries the token it starts from.</param>
    /// <param name="content">
    /// The bodies of the write's commands, in order, a <c>Delete</c> having none, which the store reads during the call:
    /// the verified batch pages' own bytes, from which the store computes its transport checksum.
    /// </param>
    /// <param name="cancellationToken">Stops the write, whose outcome is then unknown.</param>
    /// <returns>
    /// <see cref="ArchiveLogConditionOutcome.Held"/> once the log holds the write, applied or superseded; or
    /// <see cref="ArchiveLogConditionOutcome.PredecessorMissing"/> when the log lacks a write before it, which the drain
    /// decides on.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The content's length is not the commands' lengths, or an append run's <c>Append</c> is empty or above the store's
    /// largest, or an opener's tombstone is not 8 bytes.
    /// </exception>
    /// <exception cref="ArchiveCorruptException">The write's token is malformed, or the log contradicts the write's chain or its own format.</exception>
    /// <exception cref="ArchiveLogRefusedException">The store refused a request which no retry can make.</exception>
    public ValueTask<ArchiveLogConditionOutcome> WriteAsync(ArchiveLogName name, ArchiveWrite write, ReadOnlySequence<byte> content, CancellationToken cancellationToken) =>
        WriteAsync(name, write, content, null, cancellationToken);

    /// <summary>
    /// Returns the requests <paramref name="write"/> plans with no failed condition, retry, or properties read: an
    /// opener's two, its base and its write-ahead object; an append run's one per unit, and a key's first run's creation
    /// of its log; none for an empty write. A drain admits each write by it (archive-logs.md §8.6).
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The write's token is malformed.</exception>
    /// <exception cref="ArgumentException">An append run's <c>Append</c> is empty or above the store's largest.</exception>
    public int PlannedRequests(ArchiveWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.Commands.IsEmpty)
        {
            return 0;
        }

        if (write.IsOpener)
        {
            return 2;
        }

        var from = ArchiveLogTokens.PositionOf(write.Token);
        var units = ArchiveLogUnitPacker.Boundaries(_store.Layout, ArchiveLogConditions.RunStart(from).Length, write.Commands.AsSpan()).Length - 1;
        return units + (from.IsOrigin ? 1 : 0);
    }

    /// <inheritdoc cref="WriteAsync(ArchiveLogName, ArchiveWrite, ReadOnlySequence{byte}, CancellationToken)"/>
    /// <param name="name">The key's log.</param>
    /// <param name="write">The key's write for a segment.</param>
    /// <param name="content">The bodies of the write's commands.</param>
    /// <param name="issued">Counts each request the write issues, every attempt and properties read among them; or none.</param>
    /// <param name="cancellationToken">Stops the write, whose outcome is then unknown.</param>
    internal async ValueTask<ArchiveLogConditionOutcome> WriteAsync(ArchiveLogName name, ArchiveWrite write, ReadOnlySequence<byte> content, RequestTally? issued, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(write);
        var from = ArchiveLogTokens.PositionOf(write.Token);
        var length = write.Commands.Sum(static command => (long)command.ContentLength);
        if (content.Length != length)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A write's content is its commands' {length} bytes, not {content.Length}."),
                nameof(content));
        }

        if (write.Commands.IsEmpty)
        {
            return ArchiveLogConditionOutcome.Held.Instance;
        }

        try
        {
            return write.IsOpener
                ? await OpenAsync(name, from, ArchiveLogOpeners.Of(write), content, issued, cancellationToken).ConfigureAwait(false)
                : await AppendAsync(name, write, from, content, issued, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveLogCorruptException exception)
        {
            throw new ArchiveCorruptException(exception.Message, exception);
        }
    }

    /// <summary>
    /// Returns whether a call which threw is retried: not when <paramref name="cancellationToken"/>, the caller's, is
    /// cancelled, though a transport's own timeout is; and not a refusal, a defect, or corruption it reports.
    /// </summary>
    internal static bool IsRetried(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        ArchiveCorruptException or ArchiveLogCorruptException or ArchiveLogRefusedException => false,
        ArgumentException or NotSupportedException or InvalidOperationException => false,
        _ => true,
    };

    /// <summary>
    /// Checks that the generation the opener created holds its metadata, as the properties read which classified it
    /// shows; a later generation's opener is another write's.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The generation records another opener.</exception>
    private static void CheckOpener(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ArchiveLogProperties read)
    {
        if (read.Position.Generation == from.Generation + 1 && read.Opener != opener)
        {
            throw new ArchiveCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"The archive log '{name}' records the opener {read.Opener} on generation {read.Position.Generation}, which the opener {opener} from {from} created."));
        }
    }

    private async ValueTask<ArchiveLogConditionOutcome> AppendAsync(ArchiveLogName name, ArchiveWrite write, ArchiveLogPosition from, ReadOnlySequence<byte> content, RequestTally? issued, CancellationToken cancellationToken)
    {
        var start = ArchiveLogConditions.RunStart(from);
        var boundaries = ArchiveLogUnitPacker.Boundaries(_store.Layout, start.Length, write.Commands.AsSpan());
        var units = new ReadOnlySequence<byte>[boundaries.Length - 1];
        for (var unit = 0; unit < units.Length; unit++)
        {
            units[unit] = content.Slice((long)(boundaries[unit] - start.Length), (long)(boundaries[unit + 1] - boundaries[unit]));
        }

        // Each pass ends the write, resumes it past the unit whose condition failed, or, once, re-issues it from its start, which
        // its predecessor reached meanwhile; so it ends within a pass per unit and one more.
        var restarted = false;
        for (var next = 0; ;)
        {
            var at = next == 0 ? from : start with { Length = boundaries[next], Units = start.Units + (ulong)next };
            var run = new ArraySegment<ReadOnlySequence<byte>>(units, next, units.Length - next);
            var outcome = await _retry.ExecuteAsync(
                async token =>
                {
                    issued?.Add(run.Count + (at.IsOrigin ? 1 : 0));
                    return await _store.AppendAsync(name, at, run, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            if (outcome.Value is ArchiveLogWriteOutcome.Applied)
            {
                return ArchiveLogConditionOutcome.Held.Instance;
            }

            var read = await PropertiesAsync(name, issued, cancellationToken).ConfigureAwait(false);
            switch (ArchiveLogConditions.ClassifyAppend(from, boundaries, read).Value)
            {
                case ArchiveLogAppendConditionOutcome.Held:
                    return ArchiveLogConditionOutcome.Held.Instance;
                case ArchiveLogAppendConditionOutcome.PredecessorMissing:
                    return ArchiveLogConditionOutcome.PredecessorMissing.Instance;
                case ArchiveLogAppendConditionOutcome.Resume resume when resume.Unit > next:
                    next = resume.Unit;
                    break;
                case ArchiveLogAppendConditionOutcome.Resume { Unit: 0 } when next == 0 && !restarted:
                    restarted = true;
                    break;
                case ArchiveLogAppendConditionOutcome.Resume resume:
                    // Positions only grow, so once the log reached a unit's start, that unit's failed condition leaves it past.
                    throw ArchiveLogConditions.Contradiction($"The archive log '{name}' failed the condition of unit {next} of the append run from {from}, yet stays at unit {resume.Unit}'s start, which only a generation tag that disagrees with its metadata leaves");
                case null:
                default:
                    throw new UnreachableException();
            }
        }
    }

    private async ValueTask<ArchiveLogConditionOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, RequestTally? issued, CancellationToken cancellationToken)
    {
        // The opener is issued once more only when its predecessor brought the log to its start after its condition failed.
        for (var retried = false; ; retried = true)
        {
            var outcome = await _retry.ExecuteAsync(
                async token =>
                {
                    issued?.Add(2);
                    return await _store.OpenAsync(name, from, opener, content, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            if (outcome.Value is ArchiveLogWriteOutcome.Applied)
            {
                return ArchiveLogConditionOutcome.Held.Instance;
            }

            var read = await PropertiesAsync(name, issued, cancellationToken).ConfigureAwait(false);
            switch (ArchiveLogConditions.ClassifyOpener(from, read).Value)
            {
                case ArchiveLogOpenerConditionOutcome.Held:
                    // Held follows a properties read which found the log.
                    CheckOpener(name, from, opener, read ?? throw new UnreachableException());
                    return ArchiveLogConditionOutcome.Held.Instance;
                case ArchiveLogOpenerConditionOutcome.PredecessorMissing:
                    return ArchiveLogConditionOutcome.PredecessorMissing.Instance;
                case ArchiveLogOpenerConditionOutcome.Retry when !retried:
                    break;
                case ArchiveLogOpenerConditionOutcome.Retry:
                    // Positions only grow, so once the log reached the opener's start, its failed condition leaves it past.
                    throw ArchiveLogConditions.Contradiction($"The archive log '{name}' failed the condition of the opener from {from} at its start, which only a generation tag that disagrees with its metadata leaves");
                case null:
                default:
                    throw new UnreachableException();
            }
        }
    }

    private ValueTask<ArchiveLogProperties?> PropertiesAsync(ArchiveLogName name, RequestTally? issued, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync(
            async token =>
            {
                issued?.Add(1);
                return await _store.GetPropertiesAsync(name, token).ConfigureAwait(false);
            },
            cancellationToken);
}

/// <summary>
/// A count of the requests a key's write issued (archive-logs.md §8.6): each call's requests, a run's units and its log's
/// creation, an opener's two, and each properties read, counted before each attempt, so an attempt which fails counts.
/// </summary>
internal sealed class RequestTally
{
    private int _issued;

    /// <summary>Gets the requests counted.</summary>
    public int Issued => Volatile.Read(ref _issued);

    /// <summary>Counts <paramref name="requests"/> more.</summary>
    public void Add(int requests) => Interlocked.Add(ref _issued, requests);
}
