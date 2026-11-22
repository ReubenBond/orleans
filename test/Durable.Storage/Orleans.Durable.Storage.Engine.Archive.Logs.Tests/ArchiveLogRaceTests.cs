using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// Racing writers of one key's chain (decision 18; archive-logs.md §8; LOG-I1, LOG-I2), <see cref="ArchiveLogRaces"/>,
/// over the memory store, whose requests fail before and after they land and whose retries meet their own landing, and
/// over the Azure store on the fake transport beneath the real SDK, whose requests also land after their own retry or
/// after their call gave up, and whose server fails after a write landed; its clients retry three times or not at all.
/// The fake checks what archive logs keep of its blobs after every write.
/// </summary>
public sealed class ArchiveLogRaceTests : IDisposable
{
    private static readonly FakeAzureFault[] AzureFaults =
        [FakeAzureFault.FailBefore, FakeAzureFault.FailAfter, FakeAzureFault.DeliverLater, FakeAzureFault.ServerErrorAfterLanding, FakeAzureFault.BreakMidBody];

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public Task RacingWritersOverTheMemoryStoreHoldTheChain() =>
        ArchiveLogRaces.Cases.SampleAsync(race => _leaks.InScopeAsync(() => ArchiveLogRaces.CheckAsync(race, Memory, Concurrently, new FakeTimeProvider())), iter: 200, threads: 1);

    [Fact]
    public Task RacingWritersOverTheAzureStoreHoldTheChain() =>
        ArchiveLogRaces.Cases.SampleAsync(race => _leaks.InScopeAsync(() => ArchiveLogRaces.CheckAsync(race, Azure, Concurrently, new FakeTimeProvider())), iter: 100, threads: 1);

    private static Task Concurrently(Func<Task> writer) => Task.Run(writer);

    private static Task Memory(int seed, Func<ArchiveLogRaces.RaceStore, Task> run)
    {
        var random = new Random(seed);
        var layout = new ArchiveLogLayout(MemoryArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 3, 100, 50, MemoryArchiveLogStore.NameRule);
        var store = new MemoryArchiveLogStore(layout, _ =>
        {
            lock (random)
            {
                return ArchiveLogRaces.IsChecking || random.NextDouble() >= 0.2 ? ArchiveLogFault.None : (ArchiveLogFault)random.Next(1, 4);
            }
        });
        return run(new(store, generation => store.Creations(ArchiveLogRaces.Log.Value, generation), static () => { }));
    }

    private static async Task Azure(int seed, Func<ArchiveLogRaces.RaceStore, Task> run)
    {
        var random = new Random(seed);
        using var service = new FakeAzureBlobService(
            faults: _ =>
            {
                lock (random)
                {
                    return ArchiveLogRaces.IsChecking || random.NextDouble() >= 0.2 ? FakeAzureFault.None : AzureFaults[random.Next(AzureFaults.Length)];
                }
            },
            checkInvariants: true);
        var layout = new ArchiveLogLayout(AzureArchiveLogStore.LayoutName, ArchiveLogPacking.Blocks, 3, 100, 50, AzureArchiveLogMetadata.NameRule);
        var store = new AzureArchiveLogStore(service.Client(seed % 4 == 0 ? 0 : 3), "json", layout);
        await run(new(
            store,
            generation => service.Records.Count(record => record.Operation == FakeAzureOperation.CreateAppendBlob && record.Status == 201
                && record.Headers.GetValueOrDefault("x-ms-meta-wal_generation") == generation.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            service.DeliverDeferred));
        Assert.True(service.Violations.Count == 0, string.Join(Environment.NewLine, service.Violations));
    }
}
