using Orleans.Durable.Storage.Testing;
using Xunit.Sdk;
using Xunit.v3;

namespace Orleans.Durable.Storage.Diagnostics.Tests;

/// <summary>
/// Pins the test hosts' recorder of fatal errors, which every test of local storage failures relies on to take only its
/// own reports and to fail a test which leaves one untaken. Its tests run alone, so the reports' numbers are theirs.
/// </summary>
[Collection(nameof(FatalErrorsTests))]
public sealed class FatalErrorsTests
{
    [Fact]
    public void TheTestCheckFailsATestWhichLeftAReportAndLeavesNothingBehind()
    {
        FatalErrorHandler.StopOnDefect("WalPlacement", new InvalidOperationException("An untaken defect."));
        var check = new FatalErrors.TestCheckAttribute();
        var test = (IXunitTest)TestContext.Current.Test!;

        var failure = Assert.Throws<FailException>(() => check.After(test.TestMethod.Method, test));

        Assert.Contains("1 fatal error was reported in this test and not taken", failure.Message, StringComparison.Ordinal);
        Assert.Contains("WalPlacement", failure.Message, StringComparison.Ordinal);
        Assert.Empty(FatalErrors.TakeMine());
    }

    [Fact]
    public void AnOwnerTakesTheReportNumberedSinceAndThoseAfterIt()
    {
        var since = FatalErrors.ReportedCount;
        FatalErrorHandler.StopOnDefect("WalRoll", new InvalidOperationException("The first."));
        FatalErrorHandler.StopOnDefect("WalDiscard", new InvalidOperationException("The second."));

        var later = Assert.Single(FatalErrors.TakeMine(since + 1));
        var first = Assert.Single(FatalErrors.TakeMine(since));

        Assert.Equal((FatalErrorSite.Defect, "WalDiscard", since + 1), (later.Site, later.Failure.Defect, later.Sequence));
        Assert.Equal((FatalErrorSite.Defect, "WalRoll", since), (first.Site, first.Failure.Defect, first.Sequence));
    }

    [Fact]
    public void ATestTakesNoReportAnotherOwnerRaised()
    {
        var since = FatalErrors.ReportedCount;
        Thread outside;
        using (ExecutionContext.SuppressFlow())
        {
            // Without the test's execution context, the report is raised outside every test.
            outside = new Thread(static () => FatalErrorHandler.StopOnDefect("StateFlush", new InvalidOperationException("Not this test's.")));
            outside.Start();
        }

        outside.Join();

        Assert.Empty(FatalErrors.TakeMine(since));
        var other = Assert.Single(FatalErrors.TakeReportedBy(test: null, since));
        Assert.Equal(("StateFlush", null), (other.Failure.Defect, other.TestId));
    }

    [Fact]
    public void AReportFromACrashedProcessIsNotRecorded()
    {
        var since = FatalErrors.ReportedCount;

        FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleWrite, "node/wal/1.clfl", new CrashedProcessException());
        FatalErrorHandler.StopOnDefect("WalWriteGroup", new IOException("A crashed write group.", new CrashedProcessException()));
        FatalErrorHandler.StopOnDefect("LedgerStoreDrop", new AggregateException(new CrashedProcessException()));

        Assert.Equal(since, FatalErrors.ReportedCount);
        Assert.Empty(FatalErrors.TakeMine(since));
    }
}

[CollectionDefinition(nameof(FatalErrorsTests), DisableParallelization = true)]
public sealed class FatalErrorsTestsCollection;
