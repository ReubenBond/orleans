using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Control;

/// <summary>
/// Shared per-segment <see cref="RowKind.Message"/> validator and folder used by both LSM (<c>DrainWorker</c>)
/// and Archive Logs (<c>ArchiveLogDrainWorker</c>) during a single <see cref="DrainScan"/> pass (archive.md §8.1,
/// LG-I15, LG-I16).
/// </summary>
internal sealed class MessageFolds : IDisposable
{
    private readonly LedgerMetadata _ledger;
    private readonly LedgerSegmentId _segment;
    private readonly ImmutableArray<RegisteredProcessor> _processors;
    private readonly bool _skipUnknownChannelMutant;
    private readonly IMessageFold?[] _folds;
    private long _memoryBytes;
    private bool _hasMessages;

    internal MessageFolds(
        LedgerMetadata ledger,
        LedgerSegmentId segment,
        ImmutableArray<RegisteredProcessor> processors,
        bool skipUnknownChannelMutant = false)
    {
        _ledger = ledger;
        _segment = segment;
        _processors = processors;
        _skipUnknownChannelMutant = skipUnknownChannelMutant;
        _folds = processors.IsEmpty ? [] : new IMessageFold?[processors.Length];
    }

    /// <summary>Gets a value indicating whether at least one <see cref="RowKind.Message"/> row was folded in this segment.</summary>
    public bool HasMessages => _hasMessages;

    /// <summary>Gets the total bytes currently retained across all active channel folds.</summary>
    public long MemoryBytes => _memoryBytes;

    /// <summary>Validates and builds a canonical, sorted processor table for a drain worker.</summary>
    internal static ImmutableArray<RegisteredProcessor> ValidateProcessors(IEnumerable<IMessageProcessor>? processors)
    {
        if (processors is null)
        {
            return [];
        }

        var seen = new HashSet<MessageChannel>();
        var list = new List<RegisteredProcessor>();
        foreach (var processor in processors)
        {
            ArgumentNullException.ThrowIfNull(processor, nameof(processors));
            ArgumentNullException.ThrowIfNull(processor.Channel, nameof(processors));
            if (!seen.Add(processor.Channel))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Duplicate message processor registered for channel '{processor.Channel}'."),
                    nameof(processors));
            }

            list.Add(new RegisteredProcessor(processor.Channel, processor));
        }

        list.Sort(static (a, b) => a.Channel.CompareTo(b.Channel));
        return [.. list];
    }

    /// <summary>
    /// Validates and folds one <see cref="RowKind.Message"/> row, returning the net change in retained bytes to charge
    /// to <see cref="DrainScan"/>'s <see cref="ByteBudget"/>.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">
    /// The row targets a channel with no registered <see cref="IMessageProcessor"/> (fail-closed, LG-I15), or the
    /// processor's fold rejects a malformed payload.
    /// </exception>
    public long Add(Slot slot, int rowIndex, ReadOnlySpan<byte> channelBytes, ReadOnlySequence<byte> payload)
    {
        var index = FindProcessor(channelBytes);
        if (index < 0)
        {
            if (_skipUnknownChannelMutant)
            {
                return 0;
            }

            var name = Encoding.ASCII.GetString(channelBytes);
            throw new ArchiveCorruptException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Ledger {_ledger.Id} segment {_segment.Start} slot {slot} row {rowIndex} carries a Message for unregistered channel '{name}' (LG-I15)."));
        }

        var fold = _folds[index] ??= _processors[index].Processor.BeginSegmentFold(_ledger, _segment);
        var before = fold.MemoryBytes;
        fold.Add(slot, rowIndex, payload);
        var after = fold.MemoryBytes;
        var delta = after - before;
        _memoryBytes += delta;
        _hasMessages = true;
        return delta;
    }

    /// <summary>Clears and disposes all opened channel folds, returning the negative memory delta.</summary>
    public void Clear()
    {
        for (var i = 0; i < _folds.Length; i++)
        {
            if (_folds[i] is { } fold)
            {
                fold.Clear();
                fold.Dispose();
                _folds[i] = null;
            }
        }

        _memoryBytes = 0;
        _hasMessages = false;
    }

    /// <summary>Commits all opened channel folds in canonical channel order before segment completion.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < _folds.Length; i++)
        {
            if (_folds[i] is { } fold)
            {
                await fold.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Clear();

    private int FindProcessor(ReadOnlySpan<byte> channelBytes)
    {
        for (var i = 0; i < _processors.Length; i++)
        {
            if (_processors[i].Channel.Span.SequenceEqual(channelBytes))
            {
                return i;
            }
        }

        return -1;
    }

    internal readonly record struct RegisteredProcessor(MessageChannel Channel, IMessageProcessor Processor);
}
