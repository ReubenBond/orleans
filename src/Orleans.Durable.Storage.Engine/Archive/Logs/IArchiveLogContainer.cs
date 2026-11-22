using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// The container of a cluster's archive logs (archive-logs.md §3, orleans.md §5.13): it opens the keys' logs and the
/// object store of durable storage's objects under <c>durable-storage/</c>, both under one store prefix in the one container, so a composition
/// takes both from one registration and never pairs the logs of one container with the objects of another, as
/// <c>LogArchiveStore</c>'s construction contract asks. Opening makes no request.
/// </summary>
public interface IArchiveLogContainer
{
    /// <summary>Opens the keys' logs under <paramref name="prefix"/>.</summary>
    /// <param name="prefix">
    /// The cluster's store prefix, one object-name component of <c>[a-z0-9_-]</c> under which its logs lie, which the
    /// composition validates.
    /// </param>
    /// <param name="format">
    /// The <c>format</c> caller parameter of the store's record, which a layout that records it writes into each log, as
    /// Azure's does: the composition's journal format.
    /// </param>
    /// <returns>The archive-log store of the keys' logs under the prefix.</returns>
    IArchiveLogStore OpenLogs(string prefix, string format);

    /// <summary>Opens the object store of durable storage's objects under <paramref name="prefix"/>, in the logs' container.</summary>
    /// <param name="prefix">The cluster's store prefix.</param>
    /// <returns>The object store under the prefix.</returns>
    IObjectStore OpenObjects(string prefix);
}
