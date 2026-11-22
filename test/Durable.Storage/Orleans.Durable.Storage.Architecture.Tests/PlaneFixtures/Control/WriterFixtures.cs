namespace Orleans.Durable.Storage.Architecture.Tests.PlaneFixtures.Control;

// Control-plane fixtures for PlaneSeparationTests' check that each register has one writer. The fixture allow-list
// names FixtureManifestCommitter as the replace's writer and FixtureSweeper as the delete's.

/// <summary>
/// The manifest's committer, which replaces it in an async method: the call is in the compiler's nested state
/// machine, which the check attributes to this type.
/// </summary>
public sealed class FixtureManifestCommitter(IFixtureObjectStore store)
{
    public async ValueTask<bool> CommitAsync(CancellationToken cancellationToken)
    {
        var version = await store.ReadAsync("manifest", cancellationToken).ConfigureAwait(false);
        return await store.ReplaceAsync("manifest", version ?? 0, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The sweeper, which deletes through the concrete store in a lambda, whose closure is a nested type. It also replaces,
/// which only the manifest's committer may: being one register's writer allows no other register's write.
/// </summary>
public sealed class FixtureSweeper(ConcreteFixtureObjectStore store)
{
    public Func<ValueTask> Sweep(string name, CancellationToken cancellationToken) =>
        () => store.DeleteAsync(name, cancellationToken);

    public ValueTask<bool> ReplaceAsync(CancellationToken cancellationToken) =>
        store.ReplaceAsync("manifest", expectedVersion: 1, cancellationToken);
}

/// <summary>A control-plane worker which replaces the manifest itself rather than through its committer.</summary>
public sealed class BypassingWorker(IFixtureObjectStore store)
{
    public ValueTask<bool> PublishAsync(CancellationToken cancellationToken) =>
        store.ReplaceAsync("manifest", expectedVersion: 2, cancellationToken);
}
