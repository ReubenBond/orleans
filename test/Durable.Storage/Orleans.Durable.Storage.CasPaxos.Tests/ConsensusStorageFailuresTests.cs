using Orleans.Durable.Storage.CasPaxos.Hosting;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

/// <summary>
/// The consensus stores' storage failures stop the process through Storage's fatal-error handler (conventions.md §14),
/// each with its site's exit code, the path of the failed store, and the store's error; the test host's interceptor takes
/// each report in place of the stop.
/// </summary>
[NoPageLeakCheck("Reports carry exceptions; the test rents no pages.")]
public sealed class ConsensusStorageFailuresTests
{
    [Fact]
    public void EachConsensusStorageFailureIsReportedAtItsSite()
    {
        var since = FatalErrors.ReportedCount;
        var flush = new IOException("directory flush failed");
        var transaction = new IOException("commit failed");

        ConsensusStorageFailures.Instance.OnLocalStorageFailure("directory fsync", "voters", flush);
        ConsensusStorageFailures.Instance.OnLocalStorageFailure("transaction", "voters/voter-0.db", transaction);
        ConsensusStorageFailures.Instance.OnLocalStorageFailure("ballot reservation", "proposer.db", transaction);

        var reports = FatalErrors.TakeMine(since).Select(static report => report.Failure).ToList();
        Assert.Equal(2, reports.Count);
        Assert.Equal(new FatalError(FatalErrorSite.ConsensusDirectoryFlush, "voters", flush), reports[0]);
        Assert.Equal(new FatalError(FatalErrorSite.ConsensusTransaction, "voters/voter-0.db", transaction), reports[1]);
        Assert.True(FatalErrorHandler.IsReported(transaction));
    }
}
