using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Pins the version 1 file layout.
/// </summary>
public sealed class WalFormatTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void AnAllZeroPageFailsValidation(int pageSize) => Assert.False(WalFormat.IsValidPage(new byte[pageSize]));

    [Theory]
    [InlineData(2, 4080, 0x80000FF0u)]
    [InlineData(3, 4088, 0xC0000FF8u)]
    [InlineData(3, 832, 0xC0000340u)]
    [InlineData(1, 0, 0x40000000u)]
    public void FrameWordsPackKindAndLength(int kindValue, int length, uint word)
    {
        var kind = (FrameKind)kindValue;
        Assert.Equal(word, WalFormat.EncodeFrame(kind, length));
        Assert.Equal(kind, WalFormat.GetKind(word));
        Assert.Equal(length, WalFormat.GetLength(word));
    }

    [Fact]
    public void FileHeaderLayout()
    {
        var page = new byte[4096];
        WalFormat.WriteHeader(page, writeWindowPages: 256, firstOfOpen: true);

        Assert.Equal("43-4C-46-4C-01-00-00-00-00-10-00-00-00-01-00-00-01-00-00-00", BitConverter.ToString(page, 4, 20));
        Assert.All(page[24..], static value => Assert.Equal(0, value));
        Assert.Equal(Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, page.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(page));
        Assert.Equal(new WalFileHeader(4096, 256, FirstOfOpen: true), WalTestHelpers.ReadHeader(page));
        Assert.Equal("B5795261", Crc(page));

        page[30] = 1;
        Assert.Null(WalTestHelpers.ReadHeader(page));
    }

    [Fact]
    public void HolePageLayout()
    {
        var page = new byte[4096];
        WalFormat.WriteHolePage(page);

        Assert.Equal("F8-0F-00-00", BitConverter.ToString(page, 4, 4));
        Assert.All(page[8..], static value => Assert.Equal(0, value));
        Assert.Equal("2481680C", Crc(page));
        Assert.True(WalFormat.IsValidPage(page));
        Assert.True(WalFormat.IsHolePage(page));
        Assert.False(WalFormat.IsWellFormed(page));

        page[100] = 1;
        WalFormat.SealPage(page);
        Assert.False(WalFormat.IsHolePage(page));
        Assert.False(WalFormat.IsHolePage(EmptyValidPage()));

        static byte[] EmptyValidPage()
        {
            var empty = new byte[4096];
            WalFormat.SealPage(empty);
            return empty;
        }
    }

    [Fact]
    public void AnUnknownVersionIsUnsupported()
    {
        var page = new byte[4096];
        WalFormat.WriteHeader(page, 256, firstOfOpen: false);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), 2);
        WalFormat.SealPage(page);

        Assert.Throws<WalUnsupportedFormatException>(() => WalTestHelpers.ReadHeader(page));
    }

    [Fact]
    public void FileNamesAreFixedWidth()
    {
        Assert.Equal("00000000000000000007.clfl", WalFormat.GetFileName(7));
        Assert.True(WalFormat.TryParseFileName("00000000000000000007.clfl", out var file));
        Assert.Equal(7UL, file);
        Assert.False(WalFormat.TryParseFileName("7.clfl", out _));
        Assert.False(WalFormat.TryParseFileName("00000000000000000007.clfl.tmp", out _));
    }

    /// <summary>
    /// Reproduces the 9000-byte example: file 7, starting at data page 4.
    /// </summary>
    [Fact]
    public async Task NineThousandByteRecordGoldenLayout()
    {
        var storage = new SimulatedWalStorage();
        var options = WalTestHelpers.Options() with { PreallocateFiles = false };
        await (await WriteAheadLog.CreateAsync(storage, WalTestHelpers.LogPath, options, TestContext.Current.CancellationToken)).DisposeAsync();
        for (var open = 2; open < 7; open++)
        {
            await (await WriteAheadLog.OpenAsync(storage, WalTestHelpers.LogPath, options, TestContext.Current.CancellationToken)).DisposeAsync();
        }

        var payload = WalTestHelpers.Bytes(9000, seed: 9000);
        await using (var wal = await WriteAheadLog.OpenAsync(storage, WalTestHelpers.LogPath, options, TestContext.Current.CancellationToken))
        {
            for (var page = 0; page < 4; page++)
            {
                _ = await WalTestHelpers.AppendAsync(wal, WalTestHelpers.Bytes(4088, page));
            }

            var position = await WalTestHelpers.AppendAsync(wal, payload);
            Assert.Equal(new WalPosition(7, 0x5004), position);
            await WalTestHelpers.WaitForFrontierAsync(wal, position);
            Assert.Equal(new WalPosition(7, 0x7348), wal.DurableFrontier);
            Assert.Equal(payload, (await wal.ReadAsync(new WalPosition(7, 0x5004), TestContext.Current.CancellationToken)).ToBytes());
        }

        var file = storage.ReadDurable(WalTestHelpers.LogPath, 7);
        Assert.Equal(0x8000, file.Length);
        Assert.Equal("F0-0F-00-80", BitConverter.ToString(file, 0x5004, 4));
        Assert.Equal("28-23-00-00-00-00-00-00", BitConverter.ToString(file, 0x5008, 8));
        Assert.Equal(payload[..4080], file[0x5010..0x6000]);
        Assert.Equal("F8-0F-00-C0", BitConverter.ToString(file, 0x6004, 4));
        Assert.Equal(payload[4080..8168], file[0x6008..0x7000]);
        Assert.Equal("40-03-00-C0", BitConverter.ToString(file, 0x7004, 4));
        Assert.Equal(payload[8168..], file[0x7008..0x7348]);
        Assert.All(file[0x7348..0x8000], static value => Assert.Equal(0, value));
        for (var page = 0x1000; page < 0x8000; page += 0x1000)
        {
            Assert.True(WalFormat.IsValidPage(file.AsSpan(page, 0x1000)));
        }

        Assert.Equal("E16B5C3F", Crc(file.AsSpan(0x7000, 0x1000)));
        Assert.Equal(new WalFileHeader(4096, 256, FirstOfOpen: true), WalTestHelpers.ReadHeader(file.AsSpan(0, 4096)));
        Assert.Equal(
            "00908669C6651B87ADE2A186A159DD63396B831FC2B539D60234E013E25BB833",
            Convert.ToHexString(SHA256.HashData(file)));
    }

    /// <summary>
    /// Records which arrive while every write group is in flight pack into one page.
    /// </summary>
    [Fact]
    public async Task SmallRecordsPackIntoOnePage()
    {
        var storage = new SimulatedWalStorage { HoldWrites = true };
        WalPosition[] positions;
        await using (var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), TestContext.Current.CancellationToken))
        {
            var first = AppendAsync(wal, [1]).AsTask();
            await WaitForPendingAsync(storage, 1);
            var second = AppendAsync(wal, [2]).AsTask();
            await WaitForPendingAsync(storage, 2);
            var packed = Enumerable.Range(0, 29).Select(index => AppendAsync(wal, Bytes(128, index)).AsTask()).ToArray();
            while (!Task.WhenAll([first, second, .. packed]).IsCompleted)
            {
                _ = storage.CompleteWrite();
                await PauseAsync();
            }

            positions = await Task.WhenAll(packed);
            _ = await first;
            _ = await second;
        }

        Assert.All(positions, static position => Assert.Equal(new WalPosition(1, 0x3000).File, position.File));
        Assert.Equal(Enumerable.Range(0, 29).Select(static index => new WalPosition(1, (ulong)(0x3004 + index * 132))), positions);

        var page = storage.ReadDurable(WalTestHelpers.LogPath, 1).AsSpan(0x3000, 0x1000);
        Assert.True(WalFormat.IsValidPage(page));
        Assert.Equal(WalFormat.EncodeFrame(FrameKind.Full, 128), BinaryPrimitives.ReadUInt32LittleEndian(page[4..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(page[(4 + 29 * 132)..]));
    }

    internal static async Task WaitForPendingAsync(SimulatedWalStorage storage, int count)
    {
        while (storage.PendingWriteCount < count)
        {
            await PauseAsync();
        }
    }

    private static string Crc(ReadOnlySpan<byte> page) =>
        BinaryPrimitives.ReadUInt32LittleEndian(page).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}
