using System.Collections.Frozen;
using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// Holds the silo's registered data-plane <see cref="IMessageHandler"/> instances and control-plane
/// <see cref="IMessageProcessor"/> instances so <c>DurableStorageNode</c>, <c>NodeLedgers</c>, and
/// <c>DurableJournalStorageProvider</c> share one canonical channel set (orleans.md §5.15).
/// </summary>
internal sealed class MessageChannelRegistration(
    ImmutableArray<IMessageHandler> handlers,
    ImmutableArray<IMessageProcessor> processors,
    IDisposable? ownedDisposable) : IDisposable
{
    public static readonly MessageChannelRegistration Empty = new([], [], ownedDisposable: null);

    public ImmutableArray<IMessageHandler> Handlers { get; } = handlers;

    public ImmutableArray<IMessageProcessor> Processors { get; } = processors;

    public FrozenDictionary<string, MessageChannel> KnownChannels { get; } = handlers.IsDefaultOrEmpty
        ? []
        : handlers.ToFrozenDictionary(static h => h.Channel.Value, static h => h.Channel, StringComparer.Ordinal);

    public void Dispose() => ownedDisposable?.Dispose();
}
