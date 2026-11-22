using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Fuzz.Orleans;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using static Orleans.Durable.Storage.Testing.SharedVectors;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Checks the shared node registry vectors under <c>tests/shared-vectors/orleans</c> (orleans.md §7.3): each value encodes
/// to its bytes and its bytes decode to it, contiguous and from segments; each rejection is rejected for the reason of the
/// first rule it breaks, contiguous, from segments, and through a register's read, as the independent reading of the rules
/// names it; and together the rejections cover §7.3's table of reasons, in its order, with pairs of adjacent rules which
/// pin the order of checks.
/// </summary>
public sealed partial class NodeRegistryVectorTests : IDisposable
{
    private const string Area = "orleans";

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void RegistryVectorsMatchTheCodecInBothDirections()
    {
        foreach (var vector in Cases(Area, "registries.json"))
        {
            var value = FromVector(vector);
            AssertBytes(vector, NodeRegistryCodec.Encode(value));
            Assert.Equal(Describe(value), Describe(Assert.IsType<NodeRegistryValue>(NodeRegistryCodec.Decode(Hex(vector).AsSpan()).Value)));
            Assert.Equal(Describe(value), Describe(Assert.IsType<NodeRegistryValue>(NodeRegistryCodec.Decode(Segments(Hex(vector))).Value)));
            Assert.Null(NodeRegistryFormatRules.Read(Hex(vector)).Reason);
        }
    }

    /// <summary>
    /// Every kind of <see cref="ArchiveStoreKind"/> has an accepted vector, whose <c>archive_store</c>, as the independent
    /// reading of §7.3 finds it in the bytes, is the kind's pinned value: <c>ARCHIVE_STORE_LSM</c> 1, <c>ARCHIVE_STORE_LOGS</c>
    /// 2, and <c>ARCHIVE_STORE_MEMORY</c> 3, which <c>ArchiveStoreRecordTests</c> pins on the enum; so the Archive's kinds and
    /// the registry's cannot drift apart, and a kind added to one fails until the other records it.
    /// </summary>
    [Fact]
    public void EveryArchiveStoreKindIsRecordedAsItsPinnedValue()
    {
        var recorded = new SortedDictionary<ArchiveStoreKind, int>();
        foreach (var vector in Cases(Area, "registries.json").Where(static vector => vector.GetProperty("archiveStore").ValueKind != JsonValueKind.Null))
        {
            recorded[Kind(Text(vector.GetProperty("archiveStore"), "kind"))] = NodeRegistryFormatRules.Read(Hex(vector)).Registry!.ArchiveStore;
        }

        Assert.Equal(Enum.GetValues<ArchiveStoreKind>(), recorded.Keys);
        Assert.Equal([1, 2, 3], recorded.Values);
        Assert.All(recorded, static pair => Assert.Equal((int)pair.Key, pair.Value));
    }

    /// <summary>
    /// The hand-made inputs the generator yields as fuzz seeds are committed as vectors, each by its name, so a corpus
    /// merge, which drops every input a seed covers, never loses one: the generator writes the vector files whole, and
    /// these are its seeds.
    /// </summary>
    [Fact]
    public void EveryHandMadeSeedTheGeneratorYieldsIsCommitted()
    {
        string[] seeds = ["archive-logs-store", "ipv6-loopback-endpoint", "largest-revision", "unknown-length-delimited-field", "group-wire-type", "eleven-byte-varint"];
        var committed = Cases(Area, "registries.json").Concat(Cases(Area, "registry-rejections.json"))
            .Select(static vector => Text(vector, "name"))
            .Where(static name => name.StartsWith("Seed ", StringComparison.Ordinal))
            .Select(static name => name["Seed ".Length..name.IndexOf(':', StringComparison.Ordinal)]);

        Assert.Equal(seeds.Order(StringComparer.Ordinal), committed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RegistryRejectionVectorsAreRejectedForTheirReason()
    {
        foreach (var vector in Cases(Area, "registry-rejections.json"))
        {
            AssertRejected(vector, NodeRegistryCodec.Decode(Hex(vector).AsSpan()).Value as Exception);
            AssertRejected(vector, NodeRegistryCodec.Decode(Segments(Hex(vector))).Value as Exception);
            Assert.Equal(Text(vector, "reason"), NodeRegistryFormatRules.Read(Hex(vector)).Reason);
        }

        string[] reasons = [.. Cases(Area, "registry-rejections.json").Select(static vector => Text(vector, "reason")).Distinct(StringComparer.Ordinal)];
        Assert.Equal(SpecReasons(), reasons);
    }

    /// <summary>A register holding a rejected value fails its read with the rejection, and its entry is disposed.</summary>
    [Fact]
    public async Task AReadOfARejectedValueThrowsItsRejection()
    {
        foreach (var vector in Cases(Area, "registry-rejections.json").Where(static vector => Hex(vector).Length > 0))
        {
            await _leaks.InScopeAsync(async () =>
            {
                var register = new ScriptedConditionalRegister();
                register.Put(Hex(vector));
                AssertRejected(vector, await Record.ExceptionAsync(() => NodeRegistryCodec.ReadAsync(register, TestContext.Current.CancellationToken)));
            });
        }
    }

    [Fact]
    public async Task AnAbsentRegisterReadsAsTheEmptyRegistry()
    {
        var register = new ScriptedConditionalRegister();
        var (value, version) = await NodeRegistryCodec.ReadAsync(register, TestContext.Current.CancellationToken);
        Assert.Same(NodeRegistryValue.Empty, value);
        Assert.Null(version);
    }

    private static NodeRegistryValue FromVector(JsonElement vector)
    {
        var store = vector.GetProperty("archiveStore");
        ArchiveStoreIdentity? archiveStore = store.ValueKind == JsonValueKind.Null ? null : new(Kind(Text(store, "kind")), new ArchiveStorePrefix(Text(store, "prefix")), ArchiveStoreId.TryParse(Text(store, "id")) ?? throw new InvalidDataException("An archive store vector's ID is a store ID."));
        ObjectLedgerStoreId? objectStore = vector.TryGetProperty("objectLedgerStoreId", out var objectStoreElement) && objectStoreElement.ValueKind != JsonValueKind.Null
            ? ObjectLedgerStoreId.TryParse(objectStoreElement.GetString()) ?? throw new InvalidDataException("An object-ledger store vector's ID is a store ID.")
            : null;
        var nodes = vector.GetProperty("nodes").EnumerateArray().Select(static node => new NodeRegistryEntry(
            new NodeId(Guid.Parse(Text(node, "node"), CultureInfo.InvariantCulture)),
            SiloAddress.New(System.Net.IPEndPoint.Parse(Text(node, "siloEndpoint")), node.GetProperty("generation").GetInt32()),
            node.GetProperty("ordinal").ValueKind == JsonValueKind.Null ? null : node.GetProperty("ordinal").GetInt32(),
            node.GetProperty("failureDomain").GetString(),
            UInt64(node, "retiredRevision"),
            node.TryGetProperty("diskless", out var diskless) && diskless.GetBoolean()));
        var drainAllRevision = vector.TryGetProperty("drainAllRevision", out _) ? UInt64(vector, "drainAllRevision") : 0UL;
        return new NodeRegistryValue(UInt64(vector, "revision"), [.. nodes], archiveStore, objectStore, drainAllRevision);
    }

    private static ArchiveStoreKind Kind(string kind) => kind switch
    {
        "lsm" => ArchiveStoreKind.Lsm,
        "logs" => ArchiveStoreKind.Logs,
        "memory" => ArchiveStoreKind.Memory,
        _ => throw new InvalidDataException($"An archive store vector's kind is lsm, logs, or memory, not {kind}."),
    };

    private static string Describe(NodeRegistryValue value) =>
        $"r{value.Revision} d{value.DrainAllRevision} {value.ArchiveStore} {value.ObjectLedgerStore} [{string.Join("; ", value.Nodes.Select(static node => $"{node.Node} {node.Silo} {node.Ordinal} {node.FailureDomain} {node.RetiredRevision} {node.Diskless}"))}]";

    /// <summary>Lays <paramref name="bytes"/> out in segments of three bytes, as a register's read may lend them.</summary>
    private static ReadOnlySequence<byte> Segments(byte[] bytes)
    {
        if (bytes.Length <= 3)
        {
            return new(bytes);
        }

        var first = new Segment(bytes.AsMemory(0, 3), 0);
        var last = first;
        for (var start = 3; start < bytes.Length; start += 3)
        {
            last = last.Append(bytes.AsMemory(start, Math.Min(3, bytes.Length - start)));
        }

        return new(first, 0, last, last.Memory.Length);
    }

    /// <summary>Returns the reason codes of orleans.md §7.3's table, in order.</summary>
    private static string[] SpecReasons()
    {
        var spec = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "spec", "compositions", "orleans.md"));
        var start = spec.IndexOf("### 7.3 Node registry", StringComparison.Ordinal);
        var section = spec[start..spec.IndexOf("## 8. Operation", start, StringComparison.Ordinal)];
        return [.. ReasonRow().Matches(section).Select(static match => match.Groups["reason"].Value)];
    }

    [GeneratedRegex(@"^\| `(?<reason>[a-z-]+)` \|", RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 10_000)]
    private static partial Regex ReasonRow();

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
