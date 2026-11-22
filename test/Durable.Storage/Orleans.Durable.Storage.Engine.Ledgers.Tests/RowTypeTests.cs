using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Pins the domains of rows and tags at each limit, from both sides (ledgers.md §2.2, §4; identity.md §6), and the tag
/// order (LG-I6's order).
/// </summary>
public sealed class RowTypeTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void ARowsKeyIsOneTo65535Bytes()
    {
        using var payload = Payload([1]);
        using (var shortest = new Row([0x01], RowKind.Append, payload))
        {
            Assert.Equal([0x01], shortest.Key.ToArray());
        }

        using (var longest = new Row(new byte[KeyRange.MaxKeyLength], RowKind.Append, payload))
        {
            Assert.Equal(KeyRange.MaxKeyLength, longest.Key.Length);
        }

        Assert.Equal("key", Assert.Throws<ArgumentException>(() => new Row([], RowKind.Append, payload)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => new Row(new byte[KeyRange.MaxKeyLength + 1], RowKind.Append, payload)).ParamName);
    }

    /// <summary>
    /// ledgers.md §2.2, §4: a token holds at most 32 bytes, compares by its bytes, and the default token is the empty one; a
    /// version's next counts one more under the same base, and a count at the <c>u64</c> maximum has no next.
    /// </summary>
    [Fact]
    public void ATokenHoldsAtMost32BytesAndAVersionCountsWithinU64()
    {
        var longest = new RowToken(new byte[RowToken.MaxLength]);
        Assert.Equal(RowToken.MaxLength, longest.Length);
        Assert.Equal("bytes", Assert.Throws<ArgumentException>(() => new RowToken(new byte[RowToken.MaxLength + 1])).ParamName);
        Assert.Equal(new RowToken([1, 2]), new RowToken([1, 2]));
        Assert.NotEqual(new RowToken([1, 2]), new RowToken([1, 2, 0]));
        Assert.Equal(new RowToken([]), default);
        Assert.True(default(RowToken).IsEmpty);

        var version = new RowVersion(new RowToken([7]), 41);
        Assert.Equal(new RowVersion(new RowToken([7]), 42), version.Next());
        _ = Assert.Throws<OverflowException>(() => new RowVersion(default, ulong.MaxValue).Next());
    }

    [Fact]
    public void ARowsKindIsDefined()
    {
        using var payload = Payload([1]);
        using (var row = new Row([0x01], RowKind.Delete, payload))
        {
            Assert.Equal(RowKind.Delete, row.Kind);
        }

        Assert.Equal("kind", Assert.Throws<ArgumentException>(() => new Row([0x01], (RowKind)3, payload)).ParamName);
    }

    /// <summary>
    /// A row copies its key and retains its own lease on its payload, possibly empty, which its disposal releases; the
    /// caller keeps and releases its own lease (conventions.md §1).
    /// </summary>
    [Fact]
    public void ARowRetainsItsOwnLeaseOnItsPayloadAndCopiesItsKey()
    {
        byte[] key = [1, 2, 3];
        var payload = Payload([4, 5]);
        var row = new Row(key, RowKind.Replace, payload);
        payload.Dispose();
        key[0] = 0;
        Assert.Equal([1, 2, 3], row.Key.ToArray());
        Assert.Equal([4, 5], row.Body.ToArray());
        var lease = row.Body;
        row.Dispose();
        _ = Assert.Throws<InvalidOperationException>(() => lease.Length);

        _ = Assert.Throws<InvalidOperationException>(() => new Row([1], RowKind.Append, payload));
        using var empty = Payload([]);
        using var emptyRow = new Row([1], RowKind.Append, empty);
        Assert.Equal(0, emptyRow.Body.Length);
    }

    [Fact]
    public void ATagsIndexStartsAtZeroAndItNamesALedger()
    {
        var tag = new Tag(new LedgerId(3), new Slot(4), 0);

        Assert.Equal((new LedgerId(3), new Slot(4), 0), (tag.Ledger, tag.Slot, tag.Index));
        Assert.Equal("index", Assert.Throws<ArgumentOutOfRangeException>(() => new Tag(new LedgerId(3), new Slot(4), -1)).ParamName);
        Assert.Equal("ledger", Assert.Throws<ArgumentException>(() => new Tag(default, new Slot(4), 0)).ParamName);
    }

    /// <summary>Tag order agrees with comparing <c>(Ledger, Slot, Index)</c> as a tuple and is consistent with equality.</summary>
    [Fact]
    public void TagsOrderAsTheirLedgerSlotAndIndexTuples()
    {
        var parts = Gen.Select(
            Gen.Frequency((3, Gen.ULong[1, 3]), (1, Gen.ULong[1, ulong.MaxValue])),
            Gen.Frequency((3, Gen.Long[0, 3]), (1, Gen.Long[0, long.MaxValue - 1])),
            Gen.Frequency((3, Gen.Int[0, 3]), (1, Gen.Int[0, int.MaxValue])));
        Gen.Select(parts, parts, parts).Sample(static (a, b, c) =>
        {
            var (x, y, z) = (ToTag(a), ToTag(b), ToTag(c));
            var order = a.CompareTo(b);
            Assert.Equal(Math.Sign(order), Math.Sign(x.CompareTo(y)));
            Assert.Equal(-Math.Sign(y.CompareTo(x)), Math.Sign(x.CompareTo(y)));
            Assert.Equal((order < 0, order <= 0, order > 0, order >= 0), (x < y, x <= y, x > y, x >= y));
            Assert.Equal(a == b, x == y);
            Assert.Equal(x == y, x.CompareTo(y) == 0);
            if (x == y)
            {
                Assert.Equal(x.GetHashCode(), y.GetHashCode());
            }

            if (x <= y && y <= z)
            {
                Assert.True(x <= z);
            }
        });

        static Tag ToTag((ulong Ledger, long Slot, int Index) parts) => new(new LedgerId(parts.Ledger), new Slot(parts.Slot), parts.Index);
    }

    private static ArcBuffer Payload(byte[] bytes) => PagedBuffers.Create(bytes, []);
}
