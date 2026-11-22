using Orleans.Consensus.CasPaxos.Server;
using Orleans.Durable.Storage.Diagnostics;

namespace Orleans.Durable.Storage.CasPaxos.Hosting;

/// <summary>
/// The storage seam of the native CASPaxos MetaStore's SQLite stores: each failed local storage operation which the
/// consensus stores report stops the process through <see cref="FatalErrorHandler"/> (conventions.md §14).
/// </summary>
/// <remarks>
/// The consensus stores detect their own failures: a directory flush, a voter's acceptor transaction, its binding and
/// admission records, and a proposer's ballot reservation. Each reports once and answers nothing afterward; this seam
/// gives the report Storage's bounded message and immediate exit, or a test's interceptor.
/// </remarks>
internal sealed class ConsensusStorageFailures : IFatalErrorHandler
{
    /// <summary>The one instance, which every consensus store of the silo reports to.</summary>
    public static ConsensusStorageFailures Instance { get; } = new();

    private ConsensusStorageFailures()
    {
    }

    /// <inheritdoc/>
    public void OnLocalStorageFailure(string operation, string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var site = operation.StartsWith("directory", StringComparison.Ordinal) ? FatalErrorSite.ConsensusDirectoryFlush : FatalErrorSite.ConsensusTransaction;
        FatalErrorHandler.StopOnStorageFailure(site, path, exception);
    }
}
