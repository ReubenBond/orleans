using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

public sealed class FileNodeRootClaimTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const string ClaimHostRoot = "CLOG_CLAIM_HOST_ROOT";
    private const string ClaimHostIndex = "CLOG_CLAIM_HOST_INDEX";
    private const string ClaimHostFlush = "CLOG_CLAIM_HOST_FLUSH";
    private const int Rounds = 256;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// LS-I12: a flushed claim's creation, in a node root which exists durably, makes the claim durable before it
    /// returns. Every crash state the file system allows after it returns holds the complete claim, and none holds a
    /// partial one; no flush reaches above the root.
    /// </summary>
    [Fact]
    public Task AFlushedClaimIsCompleteInEveryCrashStateAndDurableOnceItsCreationReturns() => SeededSimulation.RunAsync(0x5e66, async run =>
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-node"));
        var fileSystem = new RecordingFileSystem(root);
        var content = LedgerStoreClusterClaims.Encode(LedgerReplicaTestData.Cluster);
        Assert.True(await FileNodeRootClaim.Claim(root, flushToDisk: true, fileSystem).CreateIfAbsentAsync(content, Token));
        var returned = fileSystem.OperationCount;

        var enumerator = CrashStateEnumerator.Of(fileSystem, run.Seed);
        run.UseHistory(() => enumerator.Operations.Select(static (operation, index) => string.Create(CultureInfo.InvariantCulture, $"#{index} {operation}")));
        var states = enumerator.Enumerate(0, enumerator.Operations.Count, 10_000);
        foreach (var state in states.States)
        {
            var claim = enumerator.Build(state).ReadFile(LedgerStoreClusterClaims.Name);
            Assert.True(
                claim is null ? state.Point < returned : claim.AsSpan().SequenceEqual(content),
                enumerator.Describe(state));
        }

        TestDiagnostics.WriteLine(states.ToString());
    });

    /// <summary>
    /// LS-I12: a claim a flushed read returns is durable. A process which crashed after publishing the claim and before
    /// flushing the root left the claim's name unsynced; a restarted process's read flushes the root before it answers,
    /// so every crash state after the read holds the claim it returned.
    /// </summary>
    [Fact]
    public async Task AClaimAFlushedReadReturnsIsDurable()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-node"));
        var content = LedgerStoreClusterClaims.Encode(LedgerReplicaTestData.Cluster);
        var crashed = new RecordingFileSystem(root);

        // The temporary's creation, write, flush, and the link; the process crashes after the link.
        crashed.CrashAfter = 4;
        _ = await Assert.ThrowsAsync<CrashedProcessException>(async () => await FileNodeRootClaim.Claim(root, flushToDisk: true, crashed).CreateIfAbsentAsync(content, Token));
        Assert.Equal(FileSystemOperationKind.Link, crashed.Operations[^1].Kind);

        var restarted = crashed.Restart();
        Assert.Equal(content, await FileNodeRootClaim.Claim(root, flushToDisk: true, restarted).ReadAsync(Token));
        var point = restarted.OperationCount;
        var enumerator = CrashStateEnumerator.Of(restarted, seed: 0);
        foreach (var state in enumerator.Enumerate(point, point, int.MaxValue).States)
        {
            Assert.True(enumerator.Build(state).ReadFile(LedgerStoreClusterClaims.Name) is { } claim && claim.AsSpan().SequenceEqual(content), enumerator.Describe(state));
        }
    }

    /// <summary>
    /// Regression test, LS-I12 and conventions.md §13: a flushed read answers only with a claim whose name is durable. A
    /// competing process links its claim, its content synced and the root not yet flushed, just before the read reads
    /// it. A read which flushed the root before reading answered that claim with its name unsynced, so a crash after the
    /// answer could lose the claim it reported; the read flushes after reading, so every crash state from its answer on
    /// holds the claim.
    /// </summary>
    [Fact]
    public async Task AClaimLinkedJustBeforeAFlushedReadIsDurableOnceTheReadAnswers()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "clog-recorded-node"));
        var content = LedgerStoreClusterClaims.Encode(LedgerReplicaTestData.Cluster);
        var fileSystem = new RecordingFileSystem(root);
        fileSystem.BeforeRead = async path =>
        {
            // The competing process: its claim's content is synced before the link, and its root flush has not run.
            fileSystem.BeforeRead = null;
            var temporary = Path.Combine(root, $".{LedgerStoreClusterClaims.Name}.competitor.tmp");
            using (var file = fileSystem.CreateFile(temporary, replace: false, writeThrough: false, preallocationSize: 0))
            {
                await file.WriteAsync(content, 0, Token);
                file.Flush();
            }

            fileSystem.LinkFile(temporary, Path.Combine(root, LedgerStoreClusterClaims.Name));
        };

        Assert.Equal(content, await FileNodeRootClaim.Claim(root, flushToDisk: true, fileSystem).ReadAsync(Token));
        Assert.Null(fileSystem.BeforeRead);
        var point = fileSystem.OperationCount;
        var enumerator = CrashStateEnumerator.Of(fileSystem, seed: 0);
        foreach (var state in enumerator.Enumerate(point, point, int.MaxValue).States)
        {
            Assert.True(enumerator.Build(state).ReadFile(LedgerStoreClusterClaims.Name) is { } claim && claim.AsSpan().SequenceEqual(content), enumerator.Describe(state));
        }
    }

    /// <summary>
    /// The node root is an operator's precondition: a claim in a root which does not exist fails, naming the root, and
    /// creates nothing, as does a store's open on it.
    /// </summary>
    [Fact]
    public async Task AMissingNodeRootIsRefusedAndNotCreated()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "node");
        var claim = FileNodeRootClaim.Claim(root, flushToDisk: true);
        var created = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await claim.CreateIfAbsentAsync(LedgerStoreClusterClaims.Encode(LedgerReplicaTestData.Cluster), Token));
        Assert.Contains(root, created.Message, StringComparison.Ordinal);
        _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await claim.ReadAsync(Token));
        _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => LedgerStore.OpenAsync(
            new PortableWalStorage(),
            new LedgerStoreOptions { DirectoryPath = root, FileSize = 1 << 20 },
            LedgerReplicaTestData.Cluster,
            create: true,
            new LedgerStoreCheckpointOptions(),
            TimeProvider.System,
            Token).AsTask());
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingProcesses_OneClusterWinsAndBothProcessesReuseItsClaim(bool flushToDisk)
    {
        using var directory = new TemporaryDirectory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var first = StartClaimHost(directory.Path, 0, flushToDisk);
        using var second = StartClaimHost(directory.Path, 1, flushToDisk);
        var firstOutput = first.StandardOutput.ReadToEndAsync(timeout.Token);
        var firstError = first.StandardError.ReadToEndAsync(timeout.Token);
        var secondOutput = second.StandardOutput.ReadToEndAsync(timeout.Token);
        var secondError = second.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(first.WaitForExitAsync(timeout.Token), second.WaitForExitAsync(timeout.Token));
            Assert.True(first.ExitCode == 0, $"{await firstOutput}\n{await firstError}");
            Assert.True(second.ExitCode == 0, $"{await secondOutput}\n{await secondError}");

            var firstResults = await File.ReadAllLinesAsync(Path.Combine(directory.Path, "results-0"), Token);
            var secondResults = await File.ReadAllLinesAsync(Path.Combine(directory.Path, "results-1"), Token);
            Assert.Equal(Rounds, firstResults.Length);
            Assert.Equal(Rounds, secondResults.Length);
            for (var round = 0; round < Rounds; round++)
            {
                var root = RoundRoot(directory.Path, round);
                var winner = await File.ReadAllTextAsync(Path.Combine(root, LedgerStoreClusterClaims.Name), Token);
                Assert.Equal(["lost", "won"], new[] { firstResults[round], secondResults[round] }.Order(StringComparer.Ordinal));
                Assert.Equal(
                    LedgerStoreClusterClaims.Format(firstResults[round] == "won" ? LedgerReplicaTestData.Cluster : LedgerReplicaTestData.OtherCluster),
                    winner);
                Assert.Equal(["cluster"], Directory.GetFiles(root).Select(Path.GetFileName));
            }
        }
        finally
        {
            await StopClaimHostAsync(first);
            await StopClaimHostAsync(second);
        }
    }

    [Fact]
    public async Task ClaimHost_RacesAndReusesClaims()
    {
        var root = Environment.GetEnvironmentVariable(ClaimHostRoot);
        Assert.SkipWhen(root is null, "Runs as a child of the cross-process cluster claim test.");
        var index = int.Parse(Environment.GetEnvironmentVariable(ClaimHostIndex)!, CultureInfo.InvariantCulture);
        var flushToDisk = bool.Parse(Environment.GetEnvironmentVariable(ClaimHostFlush)!);
        var cluster = index == 0 ? LedgerReplicaTestData.Cluster : LedgerReplicaTestData.OtherCluster;
        var competitor = index == 0 ? LedgerReplicaTestData.OtherCluster : LedgerReplicaTestData.Cluster;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        var results = new string[Rounds];
        for (var round = 0; round < Rounds; round++)
        {
            var roundRoot = RoundRoot(root, round);
            _ = Directory.CreateDirectory(roundRoot);
            await File.WriteAllTextAsync(Marker(root, round, index, "ready"), string.Empty, timeout.Token);
            await WaitForFileAsync(Marker(root, round, 1 - index, "ready"), timeout.Token);
            try
            {
                await FileNodeRootClaim.EnsureClaimAsync(new(), roundRoot, cluster, flushToDisk, timeout.Token);
                results[round] = "won";
            }
            catch (LedgerStoreClusterMismatchException error)
            {
                Assert.Equal(cluster, error.Cluster);
                Assert.Equal(LedgerStoreClusterClaims.Format(competitor), error.ClaimedCluster);
                results[round] = "lost";
            }

            await File.WriteAllTextAsync(Marker(root, round, index, "done"), string.Empty, timeout.Token);
            await WaitForFileAsync(Marker(root, round, 1 - index, "done"), timeout.Token);
            var winner = results[round] == "won" ? cluster : competitor;
            await FileNodeRootClaim.EnsureClaimAsync(new(), roundRoot, winner, flushToDisk, timeout.Token);
        }

        await File.WriteAllLinesAsync(Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"results-{index}")), results, timeout.Token);
    }

    /// <summary>
    /// LS-I12, conventions.md §14: a directory occupying the claim's name is no claim. The creation finds the name taken,
    /// an answer, and deletes its temporary; the read of the occupant then fails, which is reported and stops the process,
    /// and the occupant stays.
    /// </summary>
    [Fact]
    public async Task FailedPublication_PreservesTheOccupantAndCleansUpTheTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var claimPath = Path.Combine(directory.Path, "cluster");
        _ = Directory.CreateDirectory(claimPath);
        var reported = FatalErrors.ReportedCount;

        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => FileNodeRootClaim.EnsureClaimAsync(new(), directory.Path, LedgerReplicaTestData.Cluster, flushToDisk: false, Token).AsTask());
        _ = StorageFailures.TakeOne(reported, FatalErrorSite.ReadFile, claimPath);

        Assert.True(Directory.Exists(claimPath));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ClaimVerification_SharesNamespaceAccessOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file sharing semantics.");
        using var directory = new TemporaryDirectory();
        _ = Directory.CreateDirectory(directory.Path);
        var claimPath = Path.Combine(directory.Path, "cluster");
        await File.WriteAllBytesAsync(claimPath, LedgerStoreClusterClaims.Encode(LedgerReplicaTestData.Cluster), Token);

        // DeleteOnClose requests the delete access used by Windows namespace operations.
        await using var namespaceHandle = new FileStream(
            claimPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        Assert.Equal(
            LedgerStoreClusterClaims.Encode(LedgerReplicaTestData.Cluster),
            await FileNodeRootClaim.Claim(directory.Path, flushToDisk: true).ReadAsync(Token));
    }

    [Fact]
    public async Task CancelledWrite_LeavesTheRootUnclaimedAndCleansUpTheTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileNodeRootClaim.EnsureClaimAsync(new(), directory.Path, LedgerReplicaTestData.Cluster, flushToDisk: false, cancelled.Token).AsTask());

        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    private static Process StartClaimHost(string root, int index, bool flushToDisk)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(FileNodeRootClaimTests).Assembly.Location);
        }

        start.ArgumentList.Add("--filter-method");
        start.ArgumentList.Add($"*{nameof(ClaimHost_RacesAndReusesClaims)}");
        start.Environment[ClaimHostRoot] = root;
        start.Environment[ClaimHostIndex] = index.ToString(CultureInfo.InvariantCulture);
        start.Environment[ClaimHostFlush] = flushToDisk.ToString(CultureInfo.InvariantCulture);
        return Process.Start(start) ?? throw new InvalidOperationException("The cluster claim host did not start.");
    }

    private static async Task StopClaimHostAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static async Task WaitForFileAsync(string path, CancellationToken token)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, token);
        }
    }

    private static string RoundRoot(string root, int round) => Path.Combine(root, round.ToString(CultureInfo.InvariantCulture));

    private static string Marker(string root, int round, int index, string phase)
        => Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"{round}-{index}-{phase}"));
}
