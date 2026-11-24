using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// Holds the silo's resolved ledger-local stream handlers and control-plane processors so hosting and control-plane
/// components share the exact configured stream set (orleans.md §5.15, LG-I16).
/// </summary>
internal sealed class LocalStreamRegistration(
    ImmutableArray<ILocalStreamHandler> handlers,
    ImmutableArray<ILocalStreamProcessor> processors,
    WakeupStreamHandler? wakeupHandler,
    IDisposable? ownedDisposable = null) : IDisposable
{
    public static LocalStreamRegistration Empty { get; } = new([], [], wakeupHandler: null);

    public ImmutableArray<ILocalStreamHandler> Handlers { get; } = handlers;

    public ImmutableArray<ILocalStreamProcessor> Processors { get; } = processors;

    public WakeupStreamHandler? WakeupHandler { get; } = wakeupHandler;

    public bool HasLocalStreams => !Handlers.IsEmpty;

    public void Dispose() => ownedDisposable?.Dispose();
}
