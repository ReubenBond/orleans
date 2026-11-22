using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Cuts a real file inside its final write group at every byte and checks that a scan returns exactly the
/// records whose pages lie wholly before the cut.
/// </summary>
public sealed class WalCutTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const int PageSize = 4096;

    /// <summary>
    /// A file which ends early loses exactly the records which reach the cut page, and reports the rest as torn.
    /// </summary>
    [Fact]
    public Task TruncatingTheFinalWriteGroupAtEveryByteRecoversExactlyTheCompletePrefix() =>
        CutEveryByteAsync(preallocate: false);

    /// <summary>
    /// A preallocated file whose final write reached the device only in part keeps zeros after the cut.
    /// </summary>
    [Fact]
    public Task ZeroingTheFinalWriteGroupFromEveryByteRecoversExactlyTheCompletePrefix() =>
        CutEveryByteAsync(preallocate: true);

    private static async Task CutEveryByteAsync(bool preallocate)
    {
        var root = Path.Combine(Path.GetTempPath(), "clog-wal-cut-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            var template = Path.Combine(root, "template");
            var options = Options(fileSize: 16 * PageSize) with { PreallocateFiles = preallocate };
            var storage = new PortableWalStorage();
            var payloads = new List<byte[]>();
            WalPosition burstStart;
            await using (var wal = await WriteAheadLog.CreateAsync(storage, template, options, TestContext.Current.CancellationToken))
            {
                for (var index = 0; index < 3; index++)
                {
                    payloads.Add(Bytes(700 + index * 150, index));
                    _ = await AppendAsync(wal, payloads[^1]);
                }

                // The final burst forms the last write group and spans a page boundary with a split record.
                var burst = Enumerable.Range(3, 3).Select(static index => Bytes(index == 4 ? 2600 : 200, index)).ToArray();
                payloads.AddRange(burst);
                var positions = await Task.WhenAll(burst.Select(payload => AppendAsync(wal, payload).AsTask()));
                burstStart = positions.Min();
            }

            var file = Path.Combine(template, WalFormat.GetFileName(1));
            var full = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
            var records = await ReadRecordsAsync(storage, CopyOf(template, root, "full"), options);
            Assert.Equal(payloads.Count, records.Count);
            var written = records.Max(static record => End(record));
            var writtenEnd = (written + PageSize - 1) / PageSize * PageSize;
            var firstCut = (long)burstStart.Offset;

            // Every byte of the group's records is cut, then the page end; the zero padding between them is cut only
            // at its first byte. Each cut opens a copy of the log, and independent copies run in parallel.
            var cuts = Enumerable.Range((int)firstCut, (int)(written - firstCut) + 1).Append((int)writtenEnd);
            var workers = Math.Clamp(Environment.ProcessorCount, 1, 16);
            var lanes = cuts.Select(static (cut, index) => (cut, index)).GroupBy(pair => pair.index % workers, static pair => pair.cut);
            var parallelism = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = TestContext.Current.CancellationToken };
            await Parallel.ForEachAsync(lanes, parallelism, async (lane, cancellationToken) =>
            {
                var directory = CopyOf(template, root, "lane-" + lane.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var published = Directory.GetFiles(directory).ToHashSet(StringComparer.Ordinal);
                foreach (var cut in lane)
                {
                    await CutAndCheckAsync(cut, directory, cancellationToken);
                    foreach (var created in Directory.GetFiles(directory).Where(file => !published.Contains(file)))
                    {
                        File.Delete(created);
                    }
                }
            });

            async Task CutAndCheckAsync(int cut, string directory, CancellationToken cancellationToken)
            {
                var bytes = preallocate ? (byte[])full.Clone() : full[..cut];
                if (preallocate)
                {
                    Array.Clear(bytes, cut, (int)(writtenEnd - cut));
                }

                await File.WriteAllBytesAsync(Path.Combine(directory, WalFormat.GetFileName(1)), bytes, cancellationToken);
                var replayed = await ReadRecordsAsync(storage, directory, options);

                // The first byte the cut changed lies in the first damaged page; every page before it is intact.
                var changed = cut;
                while (changed < full.Length && changed < writtenEnd && bytes.Length > changed && bytes[changed] == full[changed])
                {
                    changed++;
                }

                var complete = changed / PageSize * PageSize;
                var expected = records.Where(record => End(record) <= complete).ToList();
                Assert.True(
                    expected.Select(static record => (record.Position, Convert.ToHexString(record.Payload!)))
                        .SequenceEqual(replayed.Select(static record => (record.Position, Convert.ToHexString(record.Payload!)))),
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Cut at byte {cut} replayed {replayed.Count} records; expected {expected.Count}."));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Gets the offset just past a record's last byte, from the frame layout the writer uses.
    /// </summary>
    private static long End(Observed record)
    {
        var offset = (long)record.Position.Offset;
        var length = record.Payload!.Length;
        var inPage = offset % PageSize;
        if (inPage + WalFormat.FrameWordLength + length <= PageSize)
        {
            return offset + WalFormat.FrameWordLength + length;
        }

        var remaining = length - (PageSize - inPage - WalFormat.FirstFrameHeaderLength);
        var perPage = PageSize - WalFormat.CrcLength - WalFormat.FrameWordLength;
        var pages = (remaining + perPage - 1) / perPage;
        var last = remaining - (pages - 1) * perPage;
        return offset - inPage + pages * PageSize + WalFormat.CrcLength + WalFormat.FrameWordLength + last;
    }

    private static async Task<List<Observed>> ReadRecordsAsync(WalStorage storage, string directory, WalOptions options)
    {
        await using var wal = await WriteAheadLog.OpenAsync(
            storage, directory, options with { PreallocateFiles = false }, TestContext.Current.CancellationToken);
        var entries = await ReadAllAsync(wal);
        Assert.DoesNotContain(entries, static entry => entry.IsCorruption);
        return [.. entries.Where(static entry => entry.IsRecord)];
    }

    private static string CopyOf(string template, string root, string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(template, "*" + WalFormat.FileExtension))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        return directory;
    }
}
