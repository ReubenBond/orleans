using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Journals;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// Holds the silo's registered data-plane <see cref="IMessageHandler"/> instances and control-plane
/// <see cref="IMessageProcessor"/> instances so <c>DurableStorageNode</c> and <c>NodeLedgers</c> share one
/// canonical channel set (orleans.md §5.15).
/// </summary>
internal sealed class MessageChannelRegistration(
    ImmutableArray<IMessageHandler> handlers,
    ImmutableArray<IMessageProcessor> processors,
    WakeupMessageHandler? wakeupHandler,
    IDisposable? ownedDisposable) : IDisposable
{
    public static readonly MessageChannelRegistration Empty = new([], [], wakeupHandler: null, ownedDisposable: null);

    public ImmutableArray<IMessageHandler> Handlers { get; } = handlers;

    public ImmutableArray<IMessageProcessor> Processors { get; } = processors;

    public WakeupMessageHandler? WakeupHandler { get; } = wakeupHandler;

    public void Dispose() => ownedDisposable?.Dispose();
}
