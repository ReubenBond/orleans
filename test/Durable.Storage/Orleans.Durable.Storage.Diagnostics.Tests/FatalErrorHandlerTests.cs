using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Diagnostics.Tests;

public sealed class FatalErrorHandlerTests
{
    [Fact]
    public void AnInterceptedReportReachesTheInterceptorAndReturns()
    {
        var since = FatalErrors.ReportedCount;
        var error = new IOException("A failed flush.");

        FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleFlush, "node/wal/1.clfl", error);

        var reported = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(new FatalError(FatalErrorSite.HandleFlush, "node/wal/1.clfl", error), reported.Failure);
    }

    [Fact]
    public void TheDescriptionNamesTheExitCodeTheOperationThePathAndTheOperatingSystemError()
    {
        var failure = new FatalError(
            FatalErrorSite.FlushDirectory, "node/wal", new IOException("The flush failed.", new Win32Exception(5)));

        Assert.Equal(
            "Durable storage stopped the process with exit code 93: the local storage operation FlushDirectory on 'node/wal' failed with "
                + "System.IO.IOException: The flush failed. (operating-system error 5)",
            failure.Describe());
    }

    [Fact]
    public void AnHResultWhichWrapsAWin32ErrorReportsTheErrorOnWindowsOnly()
    {
        var failure = new FatalError(FatalErrorSite.HandleWrite, "file", new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));

        Assert.Equal(OperatingSystem.IsWindows() ? 112 : null, failure.OperatingSystemError);
    }

    [Fact]
    public void OnUnixTheErrnoBeneathAWindowsHResultIsReported()
    {
        // .NET on Unix wraps EACCES's IOException in an UnauthorizedAccessException, whose HResult is E_ACCESSDENIED.
        var failure = new FatalError(FatalErrorSite.OpenFile, "file", new UnauthorizedAccessException("Access denied.", new IOException("Permission denied", 13)));

        Assert.Equal(OperatingSystem.IsWindows() ? 5 : 13, failure.OperatingSystemError);
    }

    [Fact]
    public void AnErrnoReportsItselfOnUnixOnly()
    {
        var failure = new FatalError(FatalErrorSite.HandleWrite, "file", new IOException("No space left on device", 28));

        Assert.Equal(OperatingSystem.IsWindows() ? null : 28, failure.OperatingSystemError);
    }

    [Fact]
    public void AGenericHResultReportsNoOperatingSystemError()
    {
        var failure = new FatalError(FatalErrorSite.HandleWrite, "file", new IOException("An I/O error."));

        Assert.Null(failure.OperatingSystemError);
    }

    [Fact]
    public void AFailureWhichIsNoIOExceptionReportsNoOperatingSystemError()
    {
        var failure = new FatalError(FatalErrorSite.Defect, null, new InvalidOperationException("A defect."), "WalOutcomeCallback");

        Assert.Null(failure.OperatingSystemError);
        Assert.DoesNotContain("operating-system error", failure.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AReportOfAFailureAlreadyReportedIsIgnored()
    {
        var since = FatalErrors.ReportedCount;
        var failure = new IOException("A failed write.");
        FatalErrorHandler.StopOnStorageFailure(FatalErrorSite.HandleWrite, "node/wal/1.clfl", failure);

        // The component which meets the seam's failure, itself or wrapped, reports no defect: one fact says it was reported.
        FatalErrorHandler.StopOnDefect("WalWriteGroup", failure);
        FatalErrorHandler.StopOnDefect("WalRoll", new InvalidOperationException("A wrapped failure.", failure));
        FatalErrorHandler.StopOnDefect("LedgerStoreDrop", new AggregateException(new TimeoutException(), failure));

        var reported = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal(FatalErrorSite.HandleWrite, reported.Site);
        Assert.Same(failure, reported.Failure.Exception);
    }

    [Fact]
    public void AFailureIsReportedOnlyOnceItsReportIsMade()
    {
        var since = FatalErrors.ReportedCount;
        var failure = new InvalidOperationException("A publication threw.");
        Assert.False(FatalErrorHandler.IsReported(failure));
        Assert.False(FatalErrorHandler.IsReported(new AggregateException(new IOException(), failure)));

        FatalErrorHandler.StopOnDefect("LedgerStoreRecordPublication", failure);

        Assert.True(FatalErrorHandler.IsReported(failure));
        Assert.True(FatalErrorHandler.IsReported(new IOException("Wrapped.", failure)));
        Assert.True(FatalErrorHandler.IsReported(new AggregateException(new IOException(), failure)));
        Assert.False(FatalErrorHandler.IsReported(new IOException("Another failure.")));
        var report = Assert.Single(FatalErrors.TakeMine(since));
        Assert.Equal((FatalErrorSite.Defect, "LedgerStoreRecordPublication"), (report.Site, report.Failure.Defect));
    }

    [Fact]
    public void ADefectsDescriptionNamesTheDefectWithoutAPath()
    {
        var defect = new FatalError(FatalErrorSite.Defect, null, new InvalidOperationException("The slot table threw."), "LedgerStoreSlotPublication");

        Assert.Equal(
            "Durable storage stopped the process with exit code 79: the defect LedgerStoreSlotPublication, after "
                + "System.InvalidOperationException: The slot table threw.",
            defect.Describe());
    }

    /// <summary>
    /// The probe, a process which opened a LedgerStore and a RocksDB state database, reports a failed flush. It exits
    /// with the site's code at once: whether its standard error takes the message, throws, or stalls; without running a
    /// <c>ProcessExit</c> handler, which here would hang; and without any component's shutdown writing storage.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(FatalErrorProbe.FailingError)]
    [InlineData(FatalErrorProbe.StalledError)]
    public async Task AReportStopsTheProcessAtOnceWithItsSitesExitCodeAndRunsNoShutdownWhichWritesStorage(string? error)
    {
        var root = Directory.CreateTempSubdirectory("clog-fatal-probe-").FullName;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var process = Process.Start(ProbeStart(root, error))!;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var described = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var listing = (await output).ReplaceLineEndings("\n").Split('\n');
            var message = (await described).Trim();
            Assert.True(
                process.ExitCode == (int)FatalErrorProbe.Site,
                string.Create(CultureInfo.InvariantCulture, $"The probe exited with {process.ExitCode}.\nOutput:\n{string.Join('\n', listing)}\nError:\n{message}"));
            Assert.Equal(
                error is null
                    ? new FatalError(FatalErrorProbe.Site, FatalErrorProbe.FailedPath(root), new IOException("The probe's flush failed.", new Win32Exception(5))).Describe()
                    : string.Empty,
                message);
            Assert.False(File.Exists(FatalErrorProbe.MarkerPath(root)), "The exit ran a ProcessExit handler.");

            // The files the probe listed just before it reported, with their contents, are the files the root holds after
            // the exit: no component's shutdown, such as a final checkpoint, wrote storage.
            var end = Array.IndexOf(listing, FatalErrorProbe.ListingEnd);
            Assert.True(end > 0, "The probe listed its node root before it reported.");
            var before = listing[..end];
            Assert.Contains(before, line => line.StartsWith("wal", StringComparison.Ordinal));
            Assert.Equal(before, FatalErrorProbe.List(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(FatalErrorProbe.MarkerPath(root));
        }
    }

    /// <summary>
    /// Returns how to start this test application as the probe of <paramref name="root"/>, whose standard error
    /// fails as <paramref name="error"/> names, if it does.
    /// </summary>
    private static ProcessStartInfo ProbeStart(string root, string? error)
    {
        var host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // Run through the muxer when this process was started by it rather than by the application's own host.
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        start.Environment[FatalErrorProbe.Variable] = root;
        if (error is not null)
        {
            start.Environment[FatalErrorProbe.ErrorVariable] = error;
        }

        return start;
    }
}
