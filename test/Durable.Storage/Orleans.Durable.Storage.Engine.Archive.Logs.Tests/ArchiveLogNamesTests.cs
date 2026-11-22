using System.IO.Hashing;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>Archive-log names (archive-logs.md §2.1, §7.1, LOG-I5): an injective mapping outside <c>durable-storage/</c>, with the key's xxHash64.</summary>
[NoPageLeakCheck("Names are strings; the tests rent no pages.")]
public sealed class ArchiveLogNamesTests
{
    [Fact]
    public void TheDefaultNamesAKeyByItsHexadecimalDigitsWithItsHash()
    {
        var name = ArchiveLogNames.Hexadecimal.Of([0x6a, 0x31, 0xff]);

        Assert.Equal("k/6a31ff", name.Value);
        Assert.Equal(XxHash64.HashToUInt64([0x6a, 0x31, 0xff]), name.KeyHash);
        Assert.Equal(0xd80557dacbe088f2ul, ArchiveLogNames.KeyHash("journal-1"u8));
    }

    [Fact]
    public void AMappingWhichNamesUnderTheReservedPrefixIsADefect()
    {
        var names = new ArchiveLogNames("test-1", static _ => "durable-storage/x");

        _ = Assert.Throws<InvalidOperationException>(() => names.Of([1]));
        _ = Assert.Throws<InvalidOperationException>(() => new ArchiveLogNames("test-1", static _ => string.Empty).Of([1]));
    }

    [Fact]
    public void AKeyIsOneTo65535Bytes()
    {
        _ = Assert.Throws<ArgumentException>(() => ArchiveLogNames.Hexadecimal.Of([]));
        _ = Assert.Throws<ArgumentException>(() => ArchiveLogNames.Hexadecimal.Of(new byte[KeyRange.MaxKeyLength + 1]));
    }

    /// <summary>
    /// A mapping which names no log for a key declines it: <c>TryOf</c> answers none, <c>Of</c> throws, and the store
    /// admits no such key, so no write ever needs its name.
    /// </summary>
    [Fact]
    public void AKeyTheMappingDeclinesIsNotAdmitted()
    {
        var names = new ArchiveLogNames("test-1", static key => key[0] == 0 ? null : "k/" + Convert.ToHexStringLower(key));
        var store = new LogArchiveStore(new MemoryArchiveLogStore(), new InMemoryObjectStore(), names);

        Assert.Null(names.TryOf([0, 1]));
        _ = Assert.Throws<ArgumentException>(() => names.Of([0, 1]));
        Assert.False(store.AdmitsKey([0, 1]));
        Assert.Equal("k/0101", names.TryOf([1, 1])?.Value);
        Assert.True(store.AdmitsKey([1, 1]));
    }

    /// <summary>
    /// A drain marker is <c>durable-storage/d/{S:D20}/{start:D20}</c> with its segment's end in 20 decimal digits (§7.5), zero-padded so
    /// names sort as their numbers; parsing is strict, and every other name parses as none.
    /// </summary>
    [Fact]
    public void ADrainMarkerNamesItsLedgerAndStartInTwentyDigits()
    {
        var name = ArchiveLogNames.DrainMarker(new LedgerId(7), new Slot(12));

        Assert.Equal("durable-storage/d/00000000000000000007/00000000000000000012", name);
        Assert.StartsWith(ArchiveLogNames.DrainMarkersOf(new LedgerId(7)), name, StringComparison.Ordinal);
        Assert.Equal((new LedgerId(7), new Slot(12)), ArchiveLogNames.ParseDrainMarker(name));
        Assert.True(string.CompareOrdinal(ArchiveLogNames.DrainMarker(new LedgerId(7), new Slot(9)), ArchiveLogNames.DrainMarker(new LedgerId(7), new Slot(10))) < 0);
        foreach (var other in new[] { "durable-storage/d/7/12", "durable-storage/d/00000000000000000000/00000000000000000012", "durable-storage/d/00000000000000000007/0000000000000000001a", "durable-storage/d/00000000000000000007/000000000000000000120", "durable-storage/store", "k/00" })
        {
            Assert.Null(ArchiveLogNames.ParseDrainMarker(other));
        }

        Assert.Equal("00000000000000000040"u8.ToArray(), ArchiveLogNames.DrainMarkerContent(new Slot(40)));
        Assert.Equal(new Slot(40), ArchiveLogNames.ParseDrainMarkerContent("00000000000000000040"u8));
        Assert.Null(ArchiveLogNames.ParseDrainMarkerContent("40"u8));
        Assert.Null(ArchiveLogNames.ParseDrainMarkerContent("0000000000000000004x"u8));
    }
}
