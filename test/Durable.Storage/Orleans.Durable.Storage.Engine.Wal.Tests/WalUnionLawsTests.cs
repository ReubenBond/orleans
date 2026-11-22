using System.Diagnostics;
using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Holds the WAL's hand-written unions, <see cref="WalEntry"/> and <see cref="WalRead"/>, to the union laws
/// (conventions.md §15).
/// </summary>
public sealed class WalUnionLawsTests : IDisposable
{
    private static readonly Gen<WalPosition> Positions = Gen.Select(Gen.ULong[0, 1_000], Gen.ULong[0, 1UL << 40], static (file, offset) => new WalPosition(file, offset));

    private static readonly Gen<Func<object>> Buffers = Gen.Byte.Array[0, 9_000].Select(static bytes => (Func<object>)(() => Buffer(bytes)));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void WalEntryKeepsTheUnionLaws() => UnionLaws.Check(new WalEntryAdapter(), _leaks);

    [Fact]
    public void WalReadKeepsTheUnionLaws() => UnionLaws.Check(new WalReadAdapter(), _leaks);

    private static ArcBuffer Buffer(byte[] bytes)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        return writer.ConsumeSlice(bytes.Length);
    }

    private static Gen<Func<object>> Ranges(Func<WalPosition, WalPosition, object> create) =>
        Gen.Select(Positions, Positions, (start, end) => (Func<object>)(() => create(start, end)));

    private sealed class WalEntryAdapter : IUnionLawsAdapter<WalEntry>
    {
        public IReadOnlyList<UnionCase<WalEntry>> Cases { get; } =
        [
            new(
                nameof(WalRecord),
                Gen.Select(Positions, Gen.ULong[0, 1_000], Gen.UInt, Buffers, static (position, file, index, buffer) =>
                    (Func<object>)(() => new WalRecord(position, new WalPage(file, index), (ArcBuffer)buffer()))),
                static value => new WalEntry((WalRecord)value),
                static value => ((WalRecord)value).Payload.Dispose()),
            new(nameof(WalTorn), Ranges(static (start, end) => new WalTorn(start, end)), static value => new WalEntry((WalTorn)value)),
            new(nameof(WalCorruption), Ranges(static (start, end) => new WalCorruption(start, end)), static value => new WalEntry((WalCorruption)value)),
        ];

        public IEnumerable<(Func<WalEntry> Left, Func<WalEntry> Right)> EqualFieldsInDifferentCases =>
        [
            (static () => new WalEntry(new WalTorn(new WalPosition(1, 0), new WalPosition(1, 4096))), static () => new WalEntry(new WalCorruption(new WalPosition(1, 0), new WalPosition(1, 4096)))),
        ];

        public int Switch(WalEntry value) => value switch
        {
            WalRecord => 0,
            WalTorn => 1,
            WalCorruption => 2,
            null => throw new UnreachableException(),
        };
    }

    private sealed class WalReadAdapter : IUnionLawsAdapter<WalRead>
    {
        public IReadOnlyList<UnionCase<WalRead>> Cases { get; } =
        [
            new(nameof(ArcBuffer), Buffers, static value => new WalRead((ArcBuffer)value), static value => ((ArcBuffer)value).Dispose()),
            new(nameof(WalCorruption), Ranges(static (start, end) => new WalCorruption(start, end)), static value => new WalRead((WalCorruption)value)),
        ];

        public int Switch(WalRead value) => value switch
        {
            ArcBuffer => 0,
            WalCorruption => 1,
            null => throw new UnreachableException(),
        };
    }
}
