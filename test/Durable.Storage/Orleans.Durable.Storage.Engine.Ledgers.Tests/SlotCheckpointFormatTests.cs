using System.Buffers.Binary;
using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the <c>CLSI</c> root slot and checkpoint reference layouts (ledgerstore.md §7.4), whose flags record the
/// corruption bound and whether the replica's seal record precedes the replay start (LS-I11), and checks that a root slot
/// which breaks any rule while keeping a valid CRC is unusable for that rule's reason.
/// </summary>
public sealed class SlotCheckpointFormatTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const int SlotSize = 4096;

    private static readonly CheckpointRoot Root = new(
        Generation: 7,
        Height: 2,
        ReplayStart: new WalPosition(3, 4_608),
        CorruptionEnd: new WalPage(5, 6),
        Sealed: true,
        Root: new CheckpointRef(4, 8_192, SlotCheckpoints.NodeSize, 0xDEADBEEF));

    [Fact]
    public void ARootSlotHasThePinnedLayout()
    {
        var slot = new byte[SlotSize];
        Root.Write(slot);
        Assert.Equal(Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, slot.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(slot));
        Assert.Equal("CLSI"u8.ToArray(), slot[4..8]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(8)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(12)));
        Assert.Equal(7ul, BinaryPrimitives.ReadUInt64LittleEndian(slot.AsSpan(16)));
        Assert.Equal(3ul, BinaryPrimitives.ReadUInt64LittleEndian(slot.AsSpan(24)));
        Assert.Equal(4_608ul, BinaryPrimitives.ReadUInt64LittleEndian(slot.AsSpan(32)));
        Assert.Equal(5ul, BinaryPrimitives.ReadUInt64LittleEndian(slot.AsSpan(40)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(48)));
        Assert.Equal(CheckpointRoot.CorruptionEndFlag | CheckpointRoot.SealedFlag, BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(52)));
        Assert.Equal(
            "0400000000000000" + "0020000000000000" + "00180000" + "efbeadde",
            Convert.ToHexStringLower(slot.AsSpan(56, CheckpointRef.Size)));
        Assert.False(slot.AsSpan(80).ContainsAnyExcept((byte)0));
        Assert.Equal(Root, CheckpointRoot.Decode(slot).Root());
    }

    /// <summary>
    /// Each flag stands alone: a root without a corruption bound, or unsealed, or both, round-trips with only its own
    /// flags set.
    /// </summary>
    [Theory]
    [InlineData(false, true, CheckpointRoot.SealedFlag)]
    [InlineData(true, false, CheckpointRoot.CorruptionEndFlag)]
    [InlineData(false, false, 0u)]
    public void EachRootFlagRoundTripsAlone(bool corrupt, bool isSealed, uint flags)
    {
        var root = Root with { CorruptionEnd = corrupt ? Root.CorruptionEnd : null, Sealed = isSealed };
        var slot = new byte[SlotSize];
        root.Write(slot);
        Assert.Equal(flags, BinaryPrimitives.ReadUInt32LittleEndian(slot.AsSpan(52)));
        Assert.Equal(corrupt, slot.AsSpan(40, 12).ContainsAnyExcept((byte)0));
        Assert.Equal(root, CheckpointRoot.Decode(slot).Root());
    }

    [Fact]
    public void AnEmptyRootRoundTrips()
    {
        var slot = new byte[SlotSize];
        var empty = new CheckpointRoot(1, 0, default, null, Sealed: false, default);
        empty.Write(slot);
        Assert.Equal(empty, CheckpointRoot.Decode(slot).Root());
    }

    /// <summary>
    /// A root slot which breaks one rule of ledgerstore.md §7.4 decodes as the rejection naming that rule, keeping its
    /// CRC valid for every rule after <c>crc-mismatch</c>.
    /// </summary>
    [Theory]
    [InlineData("crc-mismatch")]
    [InlineData("bad-magic")]
    [InlineData("bad-version")]
    [InlineData("nonzero-padding")]
    [InlineData("null-reference-fields")]
    [InlineData("height-out-of-range")]
    [InlineData("height-root-mismatch", "height 0 with a root")]
    [InlineData("height-root-mismatch", "a null root above height 0")]
    [InlineData("generation-zero")]
    [InlineData("generation-out-of-range", "generation 2^63")]
    [InlineData("generation-out-of-range", "a root reference in generation 2^63")]
    [InlineData("unknown-flags")]
    [InlineData("corruption-end-without-flag")]
    public void ARootSlotBreakingOneRuleIsUnusableForItsReason(string reason, string? variant = null)
    {
        var slot = new byte[SlotSize];
        Root.Write(slot);
        var span = slot.AsSpan();
        switch (variant ?? reason)
        {
            case "crc-mismatch":
                span[100] = 1;
                AssertRejected(slot, reason);
                return;
            case "bad-magic":
                span[4] = (byte)'X';
                break;
            case "bad-version":
                BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 2);
                break;
            case "nonzero-padding":
                span[SlotSize - 1] = 1;
                break;
            case "null-reference-fields":
                span.Slice(56, 8).Clear();
                break;
            case "height-out-of-range":
                BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 8);
                break;
            case "height 0 with a root":
                BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 0);
                break;
            case "a null root above height 0":
                span.Slice(56, CheckpointRef.Size).Clear();
                break;
            case "generation-zero":
                BinaryPrimitives.WriteUInt64LittleEndian(span[16..], 0);
                break;
            case "generation 2^63":
                BinaryPrimitives.WriteUInt64LittleEndian(span[16..], CheckpointRoot.GenerationLimit);
                break;
            case "a root reference in generation 2^63":
                BinaryPrimitives.WriteUInt64LittleEndian(span[56..], CheckpointRoot.GenerationLimit);
                break;
            case "unknown-flags":
                BinaryPrimitives.WriteUInt32LittleEndian(span[52..], 4 | CheckpointRoot.CorruptionEndFlag);
                break;
            default:
                Assert.Equal("corruption-end-without-flag", reason);
                BinaryPrimitives.WriteUInt32LittleEndian(span[52..], CheckpointRoot.SealedFlag);
                break;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, span[4..]));
        AssertRejected(slot, reason);
    }

    /// <summary>
    /// A slot shorter than its 80 bytes of fields is <c>truncated</c>, whatever it holds.
    /// </summary>
    [Fact]
    public void ASlotShorterThanItsFieldsIsTruncated()
    {
        var slot = new byte[SlotSize];
        Root.Write(slot);
        AssertRejected(slot.AsSpan(0, CheckpointRoot.FieldsLength - 1), "truncated");
    }

    [Fact]
    public void ANullReferenceIsAllZeros()
    {
        Assert.Null(CheckpointRef.Decode(new byte[CheckpointRef.Size], out var reference));
        Assert.Equal(default, reference);
        var bytes = new byte[CheckpointRef.Size];
        bytes[20] = 1;
        Assert.Equal("null-reference-fields", DecodeRejection.ReasonOf(Assert.IsType<InvalidDataException>(CheckpointRef.Decode(bytes, out _))));
    }

    private static void AssertRejected(ReadOnlySpan<byte> slot, string reason) =>
        Assert.Equal(reason, DecodeRejection.ReasonOf(Assert.IsType<InvalidDataException>(CheckpointRoot.Decode(slot).Value)));
}
