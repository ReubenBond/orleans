using System.Buffers.Binary;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Tests;

/// <summary>
/// LG-I15 guards in the LSM layer: <see cref="DrainWorker"/> fails closed with <see cref="ArchiveCorruptException"/> on
/// <see cref="RowKind.LocalAppend"/> without a drain barrier (until J2), and <see cref="RunObjectFormat.Decode"/> rejects
/// any run object row carrying <see cref="RowKind.LocalAppend"/> (<c>3</c>) as <c>unknown-kind</c>.
/// </summary>
public sealed class LocalAppendGuardTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task DrainWorkerFailsClosedOnLocalAppendWithoutBarrier()
    {
        await using var world = await ArchiveWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        var localKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, LocalStreamName.Parse("orleans.wakeup"));
        var writer = world.Ledgers.Registry.TryGet(ledger)!;
        using (var batch = await writer.CreateBatchAsync(1, 1024, Token))
        {
            _ = batch.BeginRow(localKey, RowKind.LocalAppend, default, default, 2, "wk"u8);
            Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, Token)).Value);
        }

        _ = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var worker = world.Worker();

        var tail = await world.Ledgers.ConfirmOpenAsync(ledger, Token);
        var ex = await Assert.ThrowsAsync<ArchiveCorruptException>(() =>
            world.DrainOnceAsync(worker, world.Ledgers.Ledger(ledger), tail, Token));
        Assert.Contains("LocalAppend", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunObjectFormatDecodeRejectsRowKindLocalAppend()
    {
        // Take the first valid single-row run object vector, overwrite its row kind byte with RowKind.LocalAppend (3),
        // recompute the block CRC-32C (first block starts at section.Offset), and verify RunObjectFormat.Decode rejects it.
        var vector = Cases("archive", "run-objects.json").First();
        var original = Hex(vector);
        using var originalBuf = ArcBuffer.Wrap(original);
        using var decoded = Assert.IsType<RunObject>(RunObjectFormat.Decode(originalBuf).Value);
        var section = decoded.Trailer.Sections[0];
        var blockStart = (int)section.Offset;
        var blockEnd = (int)section.End;
        var blockCrcOffset = blockEnd - sizeof(uint);

        var mutated = (byte[])original.Clone();
        // Inside the first block, a row is: keyLen(u16 LE), keyBytes, ledger(u64), slot(u64), index(u32), kind(u8), payloadLen(u32), payload.
        var rowKeyLen = BinaryPrimitives.ReadUInt16LittleEndian(mutated.AsSpan(blockStart, sizeof(ushort)));
        Assert.InRange(rowKeyLen, 1, 1024);
        var kindOffset = blockStart + sizeof(ushort) + rowKeyLen + sizeof(ulong) + sizeof(ulong) + sizeof(uint);
        Assert.True(mutated[kindOffset] <= (byte)RowKind.Delete);
        mutated[kindOffset] = (byte)RowKind.LocalAppend;

        var newBlockCrc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, mutated.AsSpan(blockStart, blockCrcOffset - blockStart));
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(blockCrcOffset, sizeof(uint)), newBlockCrc);

        using var mutatedBuf = ArcBuffer.Wrap(mutated);
        var outcome = RunObjectFormat.Decode(mutatedBuf);
        var rejection = Assert.IsType<InvalidDataException>(outcome.Value);
        Assert.Equal(RunObjectFormat.UnknownKind, DecodeRejection.ReasonOf(rejection));
    }
}
