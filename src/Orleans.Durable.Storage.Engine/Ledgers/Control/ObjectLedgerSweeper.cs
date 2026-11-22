using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>
/// Reclaims the object-store ledgers a map value revokes (object-ledgers.md §2.3, §8.5): the only type which deletes an
/// object-store ledger's objects (OL-I9). The composition runs one per cluster.
/// </summary>
/// <remarks>
/// For each revoked ledger, a sweep creates its <c>dropped</c> marker unless the listing shows it, before it deletes or
/// replaces any object (OL-I4). In the per-slot layout it deletes every batch and keeps the markers and every SEAL
/// (OL-I8). In the append layout it replaces the ledger's newest extent (first in reverse-key listing order, or extent 0
/// when no extent exists yet) with a 17-byte <c>SEAL</c> tombstone block blob through
/// <see cref="IAppendableObjectStore.ReplaceWithTombstoneAsync"/>, keeps the markers and that tombstone, and deletes
/// every older extent (OL-I8). The <c>dropped</c> marker's name sorts first among the ledger's, so the listing shows it
/// before the ledger's other objects. A later sweep deletes any batch or older extent a delayed write re-created.
/// </remarks>
public sealed class ObjectLedgerSweeper
{
    /// <summary>The deletes a sweep keeps in flight.</summary>
    internal const int DeletesInFlight = 32;

    /// <summary>
    /// The objects of a ledger the map lists which a sweep passes in one listing before it lists again after the ledger,
    /// so a live ledger's slots cost a sweep a page at most.
    /// </summary>
    internal const int LiveObjectsPassed = 64;

    private readonly ObjectLedgerStore _store;
    private readonly IObjectStore _objects;
    private readonly IAppendableObjectStore? _appendable;
    private readonly ObjectLedgerLayout _layout;

    /// <summary>Initializes a new instance of the <see cref="ObjectLedgerSweeper"/> class.</summary>
    /// <param name="store">The process's object-ledger store.</param>
    public ObjectLedgerSweeper(ObjectLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Debug.Assert(store.Layout != ObjectLedgerLayout.Append || store.Appendable is not null);
        _store = store;
        _objects = store.Objects;
        _appendable = store.Appendable;
        _layout = store.Layout;
    }

    /// <summary>
    /// Lists the store's ledgers below <paramref name="map"/>'s next ledger ID and reclaims each one it revokes, which it
    /// does not list (<see cref="LedgerMapValue.NoLongerLists"/>).
    /// </summary>
    /// <param name="map">A map value the cluster committed.</param>
    /// <param name="cancellationToken">Stops the sweep; a delete already sent may still land, which a later sweep repeats.</param>
    /// <returns>The number of objects the sweep deleted.</returns>
    public ValueTask<int> SweepAsync(LedgerMapValue map, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        return SweepAsync(map.NoLongerLists, map.NextLedgerId, cancellationToken);
    }

    /// <summary>
    /// Lists the store's ledgers below <paramref name="next"/>, which no later ledger's ID reaches, and reclaims each one
    /// <paramref name="revokes"/> names.
    /// </summary>
    /// <param name="revokes">Whether the map revoked a ledger, which no later map lists.</param>
    /// <param name="next">The map's next ledger ID: no ledger at or above it exists yet.</param>
    /// <param name="cancellationToken">Stops the sweep.</param>
    /// <returns>The number of objects the sweep deleted.</returns>
    internal async ValueTask<int> SweepAsync(Func<LedgerId, bool> revokes, LedgerId next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revokes);
        var deleted = 0;
        List<ObjectName> deletes = [with(DeletesInFlight)];
        string? start = null;
        while (true)
        {
            string? restart = null;
            LedgerId? ledger = null;
            var revoked = false;
            var keptNewestExtent = false;
            var ledgerEvicted = false;
            var passed = 0;
            await foreach (var item in _objects.ListAsync(ObjectLedgerNames.LedgersPrefix, start, cancellationToken).ConfigureAwait(false))
            {
                if (ObjectLedgerNames.ParseLedger(item.Name) is not { } id)
                {
                    continue;
                }

                if (id >= next)
                {
                    // Names order ledgers by ID, so every later name lies at or above the next ledger ID too.
                    break;
                }

                if (id != ledger)
                {
                    if (_layout == ObjectLedgerLayout.Append && ledger is { } previous && revoked && !keptNewestExtent)
                    {
                        await ReplaceWithTombstoneAsync(ObjectLedgerNames.Extent(previous, default), default, cancellationToken).ConfigureAwait(false);
                    }

                    ledger = id;
                    revoked = revokes(id);
                    keptNewestExtent = false;
                    ledgerEvicted = false;
                    passed = 0;
                    if (revoked && !ObjectLedgerNames.IsDropped(item.Name))
                    {
                        _store.EvictCachedLedger(id);
                        ledgerEvicted = true;

                        // OL-I4, LL-I9: the drop marker is the reclamation's first durable step, from which every read and
                        // recovery of the ledger answers the drop.
                        _ = await _objects.CreateAsync(ObjectLedgerNames.Dropped(id), default, crc32c: null, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (!revoked)
                {
                    if (++passed > LiveObjectsPassed)
                    {
                        restart = ObjectLedgerNames.AfterLedger(id);
                        break;
                    }

                    continue;
                }

                // OL-I8: the markers last for the store's life.
                if (ObjectLedgerNames.IsMarker(item.Name))
                {
                    continue;
                }

                if (_layout == ObjectLedgerLayout.Append)
                {
                    if (ObjectLedgerNames.ParseExtentFromLedgerObject(item.Name) is not { } firstSlot)
                    {
                        continue;
                    }

                    if (!keptNewestExtent)
                    {
                        keptNewestExtent = true;
                        // OL-I8: replace the newest extent with a 17-byte SEAL tombstone block blob and keep it forever.
                        // Verify length-17 objects are non-appendable (block blobs record CRC-32C; appendable objects do not).
                        if (item.Length != ObjectLedgerExtentFormat.TombstoneLength ||
                            (await _objects.GetPropertiesAsync(item.Name, cancellationToken).ConfigureAwait(false))?.Crc32c is null)
                        {
                            if (!ledgerEvicted)
                            {
                                _store.EvictCachedLedger(id);
                                ledgerEvicted = true;
                            }

                            await ReplaceWithTombstoneAsync(item.Name, firstSlot, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }
                }
                else if (item.Length == 0)
                {
                    // Per-slot layout (OL-I8): keep every SEAL (empty object), delete every batch.
                    continue;
                }

                if (!ledgerEvicted)
                {
                    _store.EvictCachedLedger(id);
                    ledgerEvicted = true;
                }

                deletes.Add(item.Name);
                if (deletes.Count == DeletesInFlight)
                {
                    deleted += await DeleteAsync(deletes, cancellationToken).ConfigureAwait(false);
                }
            }

            if (_layout == ObjectLedgerLayout.Append && ledger is { } trailing && revoked && !keptNewestExtent)
            {
                await ReplaceWithTombstoneAsync(ObjectLedgerNames.Extent(trailing, default), default, cancellationToken).ConfigureAwait(false);
            }

            deleted += await DeleteAsync(deletes, cancellationToken).ConfigureAwait(false);
            if (restart is null)
            {
                return deleted;
            }

            start = restart;
        }
    }

    private async ValueTask ReplaceWithTombstoneAsync(ObjectName name, Slot firstSlot, CancellationToken cancellationToken)
    {
        using var tombstone = ArcBuffer.Wrap(ObjectLedgerExtentFormat.EncodeTombstone(firstSlot));
        var crc32c = ObjectStoreChecksums.Crc32C(tombstone);
        try
        {
            await _appendable!.ReplaceWithTombstoneAsync(name, tombstone, crc32c, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectContentChangedException exception)
        {
            ObjectContentChangedDefect.Stop(exception);
            throw;
        }
    }

    /// <summary>Deletes the batches or older extents gathered, all in flight at once, and answers how many.</summary>
    private async ValueTask<int> DeleteAsync(List<ObjectName> deletes, CancellationToken cancellationToken)
    {
        var count = deletes.Count;
        if (count > 0)
        {
            await Task.WhenAll(deletes.Select(name => _objects.DeleteAsync(name, cancellationToken).AsTask())).ConfigureAwait(false);
            deletes.Clear();
        }

        return count;
    }
}
