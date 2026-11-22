using System.Diagnostics;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// This process's cached, read-only copy of the manifest (archive §2.2).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Current"/> holds only manifest values this process read, or whose replace by this process's committer
/// returned <see cref="ObjectReplaceOutcome.Applied"/>, the newest by epoch; a replace whose outcome is unknown never
/// updates it. <see cref="ReadAsync"/> reads linearizably after its call began: one read runs at a time, and the calls
/// made while it runs share the read which starts once it completes.
/// </para>
/// <para>
/// A manifest whose bytes break its format's rules is <see cref="ArchiveCorruptException"/>, with the rejection inside.
/// </para>
/// </remarks>
public sealed class ManifestView
{
    private readonly IObjectStore _store;
    private readonly Lock _sync = new();
    private ManifestSnapshot? _current;
    private TaskCompletionSource<ManifestSnapshot>? _next;
    private TaskCompletionSource<ManifestSnapshot>? _inFlight;
    private bool _reading;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManifestView"/> class, which has read nothing yet.
    /// </summary>
    /// <param name="store">The object store holding the manifest.</param>
    public ManifestView(IObjectStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Gets the newest manifest this process has read or applied, with its version; <see langword="null"/> before the
    /// first.
    /// </summary>
    public ManifestSnapshot? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Reads the manifest, linearizably after the call began, and updates <see cref="Current"/>.
    /// </summary>
    /// <param name="cancellationToken">Stops the caller's wait; the read it shares runs on.</param>
    /// <returns>The manifest read, with its version; an absent manifest reads as <see cref="ManifestValue.Absent"/>.</returns>
    /// <exception cref="ArchiveCorruptException">The manifest's bytes break its format's rules.</exception>
    public Task<ManifestSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<ManifestSnapshot> read;
        var start = false;
        lock (_sync)
        {
            read = _next ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_reading)
            {
                (_reading, _next, _inFlight, start) = (true, null, read, true);
            }
        }

        if (start)
        {
            _ = RunReadsAsync(read);
        }

        return read.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Joins the read in flight, which began before the call, or begins one when none is: a caller which needs only some
    /// value newer than one it holds, and checks what it needs of the value, shares a read already under way, and calls
    /// <see cref="ReadAsync"/> only when the joined value does not serve.
    /// </summary>
    /// <param name="cancellationToken">Stops the caller's wait; the read runs on.</param>
    /// <returns>The manifest the joined read read, with its version.</returns>
    /// <exception cref="ArchiveCorruptException">The manifest's bytes break its format's rules.</exception>
    public Task<ManifestSnapshot> JoinAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_inFlight is { } inFlight)
            {
                return inFlight.Task.WaitAsync(cancellationToken);
            }
        }

        return ReadAsync(cancellationToken);
    }

    /// <summary>
    /// Completes once <see cref="Current"/> holds at least <paramref name="epoch"/>, reading only while it is older.
    /// </summary>
    /// <returns>The value <see cref="Current"/> held at its first epoch at or past <paramref name="epoch"/>.</returns>
    /// <exception cref="ArchiveCorruptException">The manifest's bytes break its format's rules.</exception>
    public async Task<ManifestSnapshot> RefreshAsync(ulong epoch, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (Current is { } current && current.Value.Epoch >= epoch)
            {
                return current;
            }

            _ = await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a manifest this process's committer applied: <see cref="Current"/> takes it when it is newer.
    /// </summary>
    internal void Observe(ManifestSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_current is null || snapshot.Value.Epoch > _current.Value.Epoch)
            {
                _current = snapshot;
            }
        }
    }

    /// <summary>
    /// Runs the read <paramref name="read"/> stands for, then each read callers queued while it ran, until none is queued.
    /// </summary>
    private async Task RunReadsAsync(TaskCompletionSource<ManifestSnapshot> read)
    {
        while (true)
        {
            try
            {
                var snapshot = await ReadOnceAsync().ConfigureAwait(false);
                Observe(snapshot);
                _ = read.TrySetResult(snapshot);
            }
#pragma warning disable CA1031 // The read's failure is its callers' answer.
            catch (Exception failure)
#pragma warning restore CA1031
            {
                _ = read.TrySetException(failure);
            }

            lock (_sync)
            {
                if (_next is null)
                {
                    (_reading, _inFlight) = (false, null);
                    return;
                }

                (read, _next) = (_next, null);
                _inFlight = read;
            }
        }
    }

    private async Task<ManifestSnapshot> ReadOnceAsync()
    {
        if (await _store.ReadVersionedAsync(ObjectName.Manifest, CancellationToken.None).ConfigureAwait(false) is not { } found)
        {
            return new ManifestSnapshot(ManifestValue.Absent, null);
        }

        using var content = found.Content;
        var decoding = ManifestFormat.Decode(content.AsReadOnlySequence());
        Debug.Assert(decoding.Value is not null, "A decoding holds its value or its rejection.");
        return decoding switch
        {
            ManifestValue value => new ManifestSnapshot(value, found.Version),
            InvalidDataException rejection => throw new ArchiveCorruptException("The manifest's bytes break its format's rules.", rejection),
            null => throw new UnreachableException(),
        };
    }
}
