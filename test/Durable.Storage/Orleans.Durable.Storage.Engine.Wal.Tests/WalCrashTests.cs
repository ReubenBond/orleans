using System.Buffers.Binary;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Drives the real writer through seeded crash cuts and checks replay against the acknowledgments.
/// </summary>
public sealed class WalCrashTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, 120)];

    public static TheoryData<int> FailureSeeds => [.. Enumerable.Range(0, 40)];

    /// <summary>
    /// A write which fails after some of its sectors reached the device, while other write groups are in flight and
    /// complete in the seed's order, is reported once and stops the log (conventions.md §14). The restarted process's
    /// open in the same boot reads those sectors as the device holds them, reports no damage and no record no request
    /// wrote, finds every acknowledged record, and replays the same records after a later operating-system crash
    /// (WAL-I1, WAL-I4, WAL-I10, conventions §13).
    /// </summary>
    [Theory(Timeout = 60_000)]
    [MemberData(nameof(FailureSeeds))]
    public async Task AWriteWhichFailsAfterSomeSectorsLandLosesNoAnswerToASameBootReopenOrALaterCrash(int seed)
    {
        var random = new Random(seed);
        var groups = random.Next(1, 4);
        var options = Options(pageSize: 512, fileSize: 512 * random.Next(4, 16), window: random.Next(1, 6), groups: groups);
        options = options with { PreallocateFiles = random.Next(2) == 0 };
        var storage = new SimulatedWalStorage(seed, sectorSize: 128) { HoldWrites = true };

        // Thirty records of up to three pages need more than five writes of at most five pages each.
        var failAt = random.Next(1, 6);
        var writes = 0;
        var failedFile = 0ul;
        storage.TornWriteFault = (_, file, _, _) =>
        {
            if (Interlocked.Increment(ref writes) != failAt)
            {
                return null;
            }

            failedFile = file;
            return new IOException("The simulated device failed the write.");
        };
        var since = FatalErrors.ReportedCount;
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var appends = new List<(byte[] Payload, Task<WalPosition> Completion)>();
        for (var index = 0; index < 30; index++)
        {
            appends.Add(Append(wal, index, random.Next(0, 3 * 512)));
            if (random.Next(3) == 0)
            {
                _ = storage.CompleteWrite();
                await PauseAsync();
            }
        }

        while (!appends.TrueForAll(static append => append.Completion.IsCompleted))
        {
            if (!storage.CompleteWrite())
            {
                await PauseAsync();
            }
        }

        await SettleAsync(appends.Select(static append => append.Completion));
        Assert.True(Volatile.Read(ref writes) >= failAt, "The write fault never fired.");
        await wal.DisposeAsync();
        _ = AssertReported(since, FatalErrorSite.HandleWriteGather, LogFile(failedFile));

        // The failure stopped the process; the restarted process's open sees the same boot's page cache.
        var restarted = storage.Crash(operatingSystem: false);
        List<Observed> reported;
        await using (var reopened = await WriteAheadLog.OpenAsync(restarted, LogPath, options, TestContext.Current.CancellationToken))
        {
            reported = await ReadAllAsync(reopened);
        }

        Assert.DoesNotContain(reported, static entry => entry.IsCorruption);
        var records = reported.Where(static entry => entry.IsRecord).ToArray();
        Assert.Equal(records.Select(static entry => entry.Position).Order(), records.Select(static entry => entry.Position));
        var indexes = records.Select(entry => Identify(entry, appends)).ToArray();
        Assert.Equal(indexes.Order(), indexes);
        foreach (var (payload, completion) in appends.Where(static append => append.Completion.IsCompletedSuccessfully))
        {
            Assert.Contains(records, entry => entry.Position == completion.Result && entry.Payload.SequenceEqual(payload));
        }

        // The reopen made durable what it reported, so a later replay reports the same records.
        await using var replayed = await WriteAheadLog.OpenAsync(restarted.Crash(operatingSystem: true), LogPath, options, TestContext.Current.CancellationToken);
        Assert.Equal(
            records.Select(static entry => (entry.Position, Convert.ToHexString(entry.Payload!))),
            (await ReadAllAsync(replayed)).Where(static entry => entry.IsRecord).Select(static entry => (entry.Position, Convert.ToHexString(entry.Payload!))));
    }

    /// <summary>
    /// Every acknowledged record survives every crash cut, positions follow admission order, and a crash leaves only
    /// torn faults.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task EveryAcknowledgedRecordSurvivesACrash(int seed)
    {
        var random = new Random(seed);
        var pageSize = random.Next(2) == 0 ? 512 : 1024;
        var groups = random.Next(1, 4);
        var options = new WalOptions
        {
            PageSize = pageSize,
            FileSize = pageSize * random.Next(3, 12),
            WriteWindowPages = random.Next(1, 6),
            MinWriteGroupsInFlight = groups,
            MaxWriteGroupsInFlight = groups,
            PreallocateFiles = random.Next(2) == 0,
        };
        var storage = new SimulatedWalStorage(seed, sectorSize: 256) { HoldWrites = true };
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var appends = new List<(byte[] Payload, Task<WalPosition> Completion)>();
        var steps = random.Next(10, 80);
        for (var step = 0; step < steps; step++)
        {
            if (random.Next(3) == 0)
            {
                _ = storage.CompleteWrite();
            }
            else
            {
                appends.Add(Append(wal, appends.Count, random.Next(0, 3 * pageSize)));
            }

            await Task.Yield();
            if (random.Next(4) == 0)
            {
                await PauseAsync();
            }
        }

        var acknowledged = appends
            .Select(static (append, index) => (append, index))
            .Where(static pair => pair.append.Completion.IsCompletedSuccessfully)
            .ToDictionary(static pair => pair.index, static pair => pair.append.Completion.Result);
        var restarted = storage.Crash(operatingSystem: random.Next(2) == 0);
        await wal.DisposeAsync();

        await SettleAsync(appends.Select(static append => append.Completion));
        Assert.Equal(0, storage.OpenHandleCount);
        await using var reopened = await WriteAheadLog.OpenAsync(restarted, LogPath, options, TestContext.Current.CancellationToken);
        var entries = await ReadAllAsync(reopened);

        Assert.DoesNotContain(entries, static entry => entry.IsCorruption);
        var replayed = entries.Where(static entry => entry.IsRecord).ToArray();
        Assert.Equal(replayed.Select(static entry => entry.Position).Order(), replayed.Select(static entry => entry.Position));
        var indexes = replayed.Select(entry => Identify(entry, appends)).ToArray();
        Assert.Equal(indexes.Order(), indexes);
        foreach (var (index, position) in acknowledged)
        {
            Assert.Contains(replayed, entry => entry.Position == position && entry.Payload.SequenceEqual(appends[index].Payload));
        }

        // The replayed log keeps accepting records, and a later scan reports the same history.
        var appended = await AppendAsync(reopened, Bytes(pageSize, seed));
        Assert.True(replayed.Length == 0 || appended > replayed[^1].Position);
        Assert.Equal(
            entries.Select(static entry => entry.Position),
            (await ReadAllAsync(reopened)).Select(static entry => entry.Position).Take(entries.Count));
    }

    /// <summary>
    /// Across repeated rounds of appending, crashing with a torn tail, reopening, and appending again, every
    /// acknowledged record survives, every record a replay returned is returned identically by every later
    /// replay, and no record a replay did not return ever reappears.
    /// </summary>
    /// <remarks>
    /// This is the sequence behind microsoft/garnet#2299, where a replay after a torn write and a replacement append
    /// selected the abandoned suffix.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task RepeatedCrashesNeverLoseAnAcknowledgedRecordOrResurrectAnAbandonedOne(int seed)
    {
        var random = new Random(seed);
        var pageSize = random.Next(2) == 0 ? 512 : 1024;
        var groups = random.Next(1, 4);
        var options = new WalOptions
        {
            PageSize = pageSize,
            FileSize = pageSize * random.Next(3, 12),
            WriteWindowPages = random.Next(1, 6),
            MinWriteGroupsInFlight = groups,
            MaxWriteGroupsInFlight = groups,
            PreallocateFiles = random.Next(2) == 0,
        };
        var storage = new SimulatedWalStorage(seed, sectorSize: 256) { HoldWrites = true };
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var payloads = new Dictionary<int, byte[]>();
        var acknowledged = new Dictionary<int, WalPosition>();
        List<Observed>? previous = null;
        try
        {
            var rounds = random.Next(2, 6);
            for (var round = 0; round < rounds; round++)
            {
                var appends = new List<(int Id, Task<WalPosition> Completion)>();
                var steps = random.Next(5, 40);
                for (var step = 0; step < steps; step++)
                {
                    if (random.Next(3) == 0)
                    {
                        _ = storage.CompleteWrite();
                    }
                    else
                    {
                        var id = (round << 16) | appends.Count;
                        var payload = Bytes(Math.Max(random.Next(0, 3 * pageSize), sizeof(int)), id);
                        BinaryPrimitives.WriteInt32LittleEndian(payload, id);
                        payloads.Add(id, payload);
                        appends.Add((id, AppendAsync(wal, payload).AsTask()));
                    }

                    await Task.Yield();
                }

                foreach (var (id, completion) in appends.Where(static append => append.Completion.IsCompletedSuccessfully))
                {
                    acknowledged.Add(id, await completion);
                }

                var crashed = storage;
                storage = crashed.Crash(operatingSystem: random.Next(2) == 0);
                await wal.DisposeAsync();

                await SettleAsync(appends.Select(static append => append.Completion));
                Assert.Equal(0, crashed.OpenHandleCount);

                // Replay's hole-page writes complete as issued; the rounds hold only the appends.
                wal = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
                var entries = await ReadAllAsync(wal);
                storage.HoldWrites = true;

                Assert.DoesNotContain(entries, static entry => entry.IsCorruption);
                var replayed = entries.Where(static entry => entry.IsRecord).ToList();
                Assert.Equal(replayed.Select(static entry => entry.Position).Order(), replayed.Select(static entry => entry.Position));
                foreach (var entry in replayed)
                {
                    Assert.Equal(payloads[BinaryPrimitives.ReadInt32LittleEndian(entry.Payload)], entry.Payload);
                }

                foreach (var (id, position) in acknowledged)
                {
                    Assert.Contains(replayed, entry => entry.Position == position && entry.Payload.SequenceEqual(payloads[id]));
                }

                // Earlier rounds' records are exactly those the previous replay returned: none lost, none revived.
                var earlier = replayed.Where(entry => BinaryPrimitives.ReadInt32LittleEndian(entry.Payload) >> 16 < round).ToList();
                Assert.Equal(
                    (previous ?? []).Select(static entry => (entry.Position, Convert.ToHexString(entry.Payload!))),
                    earlier.Select(static entry => (entry.Position, Convert.ToHexString(entry.Payload!))));
                previous = replayed;
            }
        }
        finally
        {
            storage.HoldWrites = false;
            while (storage.CompleteWrite())
            {
                await Task.Yield();
            }

            await wal.DisposeAsync();
        }
    }

    /// <summary>
    /// Damage to a page the writer proved durable is corruption even after a crash.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task DamageOutsideTheCrashRegionIsCorruption(int seed)
    {
        var random = new Random(seed);
        var options = Options(pageSize: 512, window: random.Next(1, 4));
        var storage = new SimulatedWalStorage(seed);
        var count = random.Next(options.WriteWindowPages + 2, 20);
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, options, TestContext.Current.CancellationToken))
        {
            for (var index = 0; index < count; index++)
            {
                _ = await AppendAsync(wal, Bytes(504, index));
            }
        }

        var damaged = random.Next(0, count - options.WriteWindowPages);
        storage.Damage(LogPath, 1, 512L * (1 + damaged), Bytes(512, seed));

        await using var reopened = await WriteAheadLog.OpenAsync(storage, LogPath, options, TestContext.Current.CancellationToken);
        var entries = await ReadAllAsync(reopened);

        Assert.True(Assert.Single(entries, static entry => entry.Fault is not null).IsCorruption);
        Assert.Equal(count - 1, entries.Count(static entry => entry.IsRecord));
    }

    private static (byte[] Payload, Task<WalPosition> Completion) Append(WriteAheadLog wal, int index, int length)
    {
        var payload = Bytes(Math.Max(length, sizeof(int)), index);
        BinaryPrimitives.WriteInt32LittleEndian(payload, index);
        return (payload, AppendAsync(wal, payload).AsTask());
    }

    private static int Identify(Observed entry, List<(byte[] Payload, Task<WalPosition> Completion)> appends)
    {
        var index = BinaryPrimitives.ReadInt32LittleEndian(entry.Payload);
        Assert.Equal(appends[index].Payload, entry.Payload);
        return index;
    }
}
