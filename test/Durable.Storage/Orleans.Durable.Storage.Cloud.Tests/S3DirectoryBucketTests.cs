using System.Buffers.Binary;
using System.IO.Hashing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.S3.Archive;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Cloud.Tests;

/// <summary>
/// <see cref="S3ObjectStore"/> in directory-bucket mode (<c>--x-s3</c>) over <see cref="FakeS3DirectoryBucketService"/>,
/// whose <c>ListObjectsV2</c> returns entries unordered, refuses <c>StartAfter</c>, and requires a non-empty
/// <c>Prefix</c> to end in <c>/</c> (object-store.md §9, implementation plan row 7.2b).
/// </summary>
public sealed class S3DirectoryBucketTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Examples => ObjectStoreContract.Examples;

    public void Dispose() => _leaks.Dispose();

    [Theory]
    [InlineData(FakeS3ListingOrder.Shuffled, 1_000, false)]
    [InlineData(FakeS3ListingOrder.Reversed, 2, true)]
    public async Task TheStoreConformsInSequences(FakeS3ListingOrder order, int maxPageSize, bool retainEmptyDirectories)
    {
        var kit = Kit(seed => Target(order, seed, maxPageSize, retainEmptyDirectories), unknownOutcomes: false);
        await kit.SampleAsync(kit.Sequences(20), iterations: 40, threads: 4);
    }

    [Fact]
    public async Task TheStoreConformsInConcurrentGroups()
    {
        var kit = Kit(seed => Target(FakeS3ListingOrder.Shuffled, seed, maxPageSize: 2, retainEmptyDirectories: true), unknownOutcomes: false, listings: false);
        await kit.SampleAsync(kit.ConcurrentSequences(8), iterations: 20, threads: 4);
    }

    [Fact]
    public async Task TheStoreConformsInConcurrentGroupsThroughTransientFaults()
    {
        var tally = new OutcomeTally();
        var kit = Kit(seed => Target(FakeS3ListingOrder.Shuffled, seed, maxPageSize: 2, retainEmptyDirectories: true, tally, faultRate: 0.15), unknownOutcomes: true, listings: false);
        await kit.SampleAsync(kit.ConcurrentSequences(8), iterations: 20, threads: 4);
        tally.AssertMostlyDefinite();
    }

    [Fact]
    public async Task TheStoreConformsThroughTransientFaults()
    {
        var tally = new OutcomeTally();
        var kit = Kit(seed => Target(FakeS3ListingOrder.Shuffled, seed, maxPageSize: 2, retainEmptyDirectories: true, tally, faultRate: 0.2), unknownOutcomes: true);
        await kit.SampleAsync(kit.Sequences(20), iterations: 30, threads: 4);
        tally.AssertMostlyDefinite();
    }

    [Fact]
    public async Task TheStoreServesObjectsAcrossBufferPages()
    {
        var values = Gen.OneOfConst((byte)1, ObjectStoreSpec.LargeValue, (byte)(ObjectStoreSpec.LargeValue + 1));
        var lengths = Gen.OneOf(Gen.Int[1, 256], Gen.Const(ObjectStoreSpec.LargeLength));
        var kit = Kit(seed => Target(FakeS3ListingOrder.Reversed, seed, maxPageSize: 2, retainEmptyDirectories: false), unknownOutcomes: false, values: values, lengths: lengths);
        await kit.SampleAsync(kit.Sequences(8), iterations: 6, threads: 2);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task TheStoreKeepsTheContract(string example)
    {
        using var fake = new FakeS3DirectoryBucketService(listingOrder: FakeS3ListingOrder.Reversed, maxPageSize: 2, retainEmptyDirectories: true);
        using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);
        var store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName, prefix: "s3dir/");

        await ObjectStoreContract.RunAsync(example, store, refusesOverPlainHttp: true);
    }

    /// <summary>
    /// Random valid <see cref="ObjectName"/> sets (short 1–10 char names and max-length boundary 197–200 char names over
    /// <c>'-'</c>, <c>'/'</c>, <c>'0'</c>, <c>'_'</c>, <c>'a'</c>, with distinct payload lengths including 0 bytes),
    /// deletions leaving empty directories, random prefixes, and random <c>startAfter</c> values (<see langword="null"/>,
    /// <c>""</c>, and names) list identically to an ordinal-sorted <c>(Name, Length)</c> oracle under single-entry pages
    /// with cross-page duplication and shuffled order.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RandomObjectSetsPrefixesAndStartsListInExactOrdinalOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var alphabet = Gen.OneOfConst('-', '/', '0', '_', 'a');
        var shortName = alphabet.Array[1, 10].Select(ToValidName);
        var longName = alphabet.Array[197, 200].Select(ToValidName);
        var nameGen = Gen.Frequency((4, shortName), (1, longName));
        var entryGen = Gen.Select(nameGen, Gen.Int[0, 32], Gen.Bool, static (name, length, delete) => (Name: name, Length: length, Delete: delete));
        var queryPrefixGen = Gen.Frequency((2, Gen.Const(string.Empty)), (3, alphabet.Array[1, 6].Select(ToValidPrefix)));
        var queryStartGen = Gen.Frequency((2, Gen.Const((string?)null)), (1, Gen.Const((string?)string.Empty)), (3, alphabet.Array[1, 6].Select(static chars => (string?)ToValidPrefix(chars))));
        var caseGen = Gen.Select(
            Gen.Int[0, int.MaxValue],
            Gen.OneOfConst(string.Empty, "p/"),
            entryGen.Array[1, 12],
            queryPrefixGen.Array[1, 3],
            queryStartGen.Array[1, 3],
            static (seed, storePrefix, entries, prefixes, starts) => (Seed: seed, StorePrefix: storePrefix, Entries: entries, Prefixes: prefixes, Starts: starts));

        await caseGen.SampleAsync(async testCase =>
        {
            await _leaks.InScopeAsync(async () =>
            {
                using var fake = new FakeS3DirectoryBucketService(
                    listingOrder: FakeS3ListingOrder.Shuffled,
                    seed: testCase.Seed,
                    maxPageSize: 1,
                    retainEmptyDirectories: true,
                    duplicateAcrossPages: true);
                using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);
                var store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName, testCase.StorePrefix);

                var oracle = new Dictionary<string, long>(StringComparer.Ordinal);
                var deleted = new List<string>();
                foreach (var (name, length, delete) in testCase.Entries)
                {
                    if (!oracle.TryAdd(name, length))
                    {
                        continue;
                    }

                    using var content = length == 0 ? default : ArcBuffer.Wrap(new byte[length]);
                    Assert.IsType<ObjectCreateOutcome.Created>((await store.CreateAsync(new ObjectName(name), content, null, ct)).Value);
                    if (delete)
                    {
                        deleted.Add(name);
                    }
                }

                // Always leave at least one surviving object when possible while exercising retained empty directories.
                foreach (var name in deleted.Take(Math.Max(0, oracle.Count - 1)))
                {
                    await store.DeleteAsync(new ObjectName(name), ct);
                    _ = oracle.Remove(name);
                }

                var sortedOracle = oracle
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => (Name: pair.Key, Length: pair.Value))
                    .ToArray();

                // Include both generated prefixes/starts and prefixes/starts taken directly from objects in the set.
                var prefixes = testCase.Prefixes
                    .Concat(oracle.Keys.Take(2).Select(static key => key.Length > 3 ? key[..3] : key))
                    .Distinct(StringComparer.Ordinal);
                var starts = testCase.Starts
                    .Concat(oracle.Keys.Take(2).Select(static key => (string?)key))
                    .Distinct(StringComparer.Ordinal);

                foreach (var prefix in prefixes)
                {
                    foreach (var startAfter in starts)
                    {
                        var expected = sortedOracle
                            .Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal) && ObjectStoreArguments.IsAfter(item.Name, startAfter))
                            .ToArray();

                        var actual = (await store.ListAsync(prefix, startAfter, ct).ToListAsync(ct))
                            .Select(static entry => (Name: entry.Name.Value, entry.Length))
                            .ToArray();

                        Assert.Equal(expected, actual);
                    }
                }
            });
        }, iter: 30, threads: 4);

        static string ToValidName(char[] chars)
        {
            var copy = (char[])chars.Clone();
            if (copy[0] == '/')
            {
                copy[0] = 'a';
            }

            if (copy[^1] == '/')
            {
                copy[^1] = 'a';
            }

            for (var i = 1; i < copy.Length; i++)
            {
                if (copy[i] == '/' && copy[i - 1] == '/')
                {
                    copy[i] = 'a';
                }
            }

            return new string(copy);
        }

        static string ToValidPrefix(char[] chars)
        {
            var copy = (char[])chars.Clone();
            if (copy[0] == '/')
            {
                copy[0] = 'a';
            }

            for (var i = 1; i < copy.Length; i++)
            {
                if (copy[i] == '/' && copy[i - 1] == '/')
                {
                    copy[i] = 'a';
                }
            }

            return new string(copy);
        }
    }

    /// <summary>
    /// Names that share prefixes across chunk boundaries (<c>abc</c> vs <c>abcd</c>, <c>a</c> vs <c>a/b</c>, <c>-</c> vs
    /// <c>/</c> vs <c>0</c> vs <c>_</c> vs <c>a</c>), as well as a 200-character maximum-length name and a zero-byte
    /// object, never collide in the directory bucket and list with exact <c>(Name, Length)</c> in ordinal order across
    /// every prefix and <c>startAfter</c> boundary, jumping directly to exact 3-character boundaries and descending a
    /// duplicated <c>CommonPrefix</c> only once.
    /// </summary>
    [Theory]
    [InlineData(FakeS3ListingOrder.Reversed, 1)]
    [InlineData(FakeS3ListingOrder.Shuffled, 2)]
    public async Task CollidingPrefixesAndChunkBoundariesListInExactOrdinalOrder(FakeS3ListingOrder order, int maxPageSize)
    {
        using var fake = new FakeS3DirectoryBucketService(listingOrder: order, seed: 17, maxPageSize: maxPageSize, duplicateAcrossPages: true);
        using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);
        var store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName, prefix: "tenant/");

        (string Name, int Length)[] allEntries =
        [
            ("a", 0),
            ("a-b", 1),
            ("a/b", 2),
            ("a/bc", 3),
            ("a/bcd", 4),
            ("a0", 5),
            ("a_b", 6),
            ("ab", 7),
            ("ab/c", 8),
            ("abc", 9),
            ("abcd", 10),
            ("abcde", 11),
            ("abcdef", 12),
            ("abcdefg", 13),
            ("d/1/0/a1", 14),
            ("manifest", 15),
            (new string('z', ObjectName.MaxLength), 16),
        ];

        foreach (var (name, length) in allEntries)
        {
            using var content = length == 0 ? default : ArcBuffer.Wrap(new byte[length]);
            Assert.IsType<ObjectCreateOutcome.Created>((await store.CreateAsync(new ObjectName(name), content, null, Token)).Value);
        }

        string[] prefixes = [string.Empty, "a", "a/", "ab", "ab/", "abc", "abcd", "abcdef", "d/", "d/1/", "manifest", "missing/", new string('z', ObjectName.MaxLength)];
        string?[] starts = [null, string.Empty, "a", "a-", "a/", "a/b", "a0", "ab", "abc", "abcd", "abcdef", "manifest", "z", new string('z', ObjectName.MaxLength)];

        var sortedAll = allEntries.OrderBy(static item => item.Name, StringComparer.Ordinal).Select(static item => (item.Name, Length: (long)item.Length)).ToArray();
        foreach (var prefix in prefixes)
        {
            foreach (var startAfter in starts)
            {
                var expected = sortedAll
                    .Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal) && ObjectStoreArguments.IsAfter(item.Name, startAfter))
                    .ToArray();

                var listed = (await store.ListAsync(prefix, startAfter, Token).ToListAsync(Token))
                    .Select(static entry => (Name: entry.Name.Value, entry.Length))
                    .ToArray();

                Assert.Equal(expected, listed);
            }
        }

        // Exact 3-char jump: prefix "abcdef" starts directly at "tenant/abc/" (contains leaf "abcdef") or "tenant/abc/def/" for "abcdefg".
        fake.ResetListedPrefixes();
        var abcdefResults = await store.ListAsync("abcdef", cancellationToken: Token).ToListAsync(Token);
        Assert.Equal([("abcdef", 12L), ("abcdefg", 13L)], abcdefResults.Select(static e => (e.Name.Value, e.Length)));
        Assert.DoesNotContain("tenant/", fake.ListedPrefixes);
        Assert.Contains("tenant/abc/", fake.ListedPrefixes);

        // Single descent on duplicated CommonPrefixes: with maxPageSize == 1 and duplicateAcrossPages == true,
        // "tenant/abc/def/" holds only "abcdefg" so it must be listed once even when its CommonPrefix repeats across pages.
        fake.ResetListedPrefixes();
        _ = await store.ListAsync("abcd", cancellationToken: Token).ToListAsync(Token);
        Assert.Equal(1, fake.ListedPrefixes.Count(static p => p == "tenant/abc/def/"));
    }

    /// <summary>
    /// When <c>startAfter</c> already lies past every name starting with <c>prefix</c>, or no valid <see cref="ObjectName"/>
    /// can satisfy the range, <see cref="S3ObjectStore.ListAsync"/> makes zero <c>ListObjectsV2</c> requests.
    /// </summary>
    [Fact]
    public async Task AnImpossiblePrefixAndStartAfterRangeSendsNoRequest()
    {
        using var fake = new FakeS3DirectoryBucketService();
        using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);
        var store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName);

        Assert.Empty(await store.ListAsync("c/", "manifest", Token).ToListAsync(Token));
        Assert.Empty(await store.ListAsync("c/", "c0", Token).ToListAsync(Token));
        Assert.Empty(await store.ListAsync(new string('z', ObjectName.MaxLength), new string('z', ObjectName.MaxLength), Token).ToListAsync(Token));

        Assert.Empty(fake.ListedPrefixes);
    }

    /// <summary>
    /// Hierarchical directory routing (<c>research/papers/logdrive/paper.md</c> §3.2): reading the newest <c>K</c> slots of a
    /// ledger with hundreds of slots jumps directly to the ledger's slots directory and descends only the newest branch,
    /// without enumerating older slot subtrees; and <see cref="ObjectLedgerNames.AfterLedger"/> skips a ledger's entire slot
    /// subtree without entering it.
    /// </summary>
    [Fact]
    public async Task ListingPrunesUnrelatedSubtreesForLedgerTailAndAfterLedger()
    {
        using var fake = new FakeS3DirectoryBucketService(listingOrder: FakeS3ListingOrder.Shuffled, seed: 42);
        using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);
        var store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName);

        var ledger7 = new LedgerId(7);
        var ledger8 = new LedgerId(8);

        // Populate 250 slots spread across distinct 3-digit subdirectories (stride 1,000 -> 250 distinct 1,000-slot buckets).
        for (var i = 0; i < 250; i++)
        {
            var slot = new Slot(i * 1_000L);
            using var content = ArcBuffer.Wrap(new byte[] { (byte)(i & 0xFF) });
            Assert.IsType<ObjectCreateOutcome.Created>((await store.CreateAsync(ObjectLedgerNames.Slot(ledger7, slot), content, null, Token)).Value);
        }

        using (var other = ArcBuffer.Wrap("x"u8.ToArray()))
        {
            Assert.IsType<ObjectCreateOutcome.Created>((await store.CreateAsync(ObjectLedgerNames.Slot(ledger8, new Slot(0)), other, null, Token)).Value);
        }

        // Top-4 newest slots (smallest reverse keys) should touch only O(depth) directories (7 levels, at most 11 calls across 2 leaf dirs).
        fake.ResetListedPrefixes();
        var newest = new List<ListedObject>();
        await foreach (var entry in store.ListAsync(ObjectLedgerNames.Slots(ledger7), cancellationToken: Token))
        {
            newest.Add(entry);
            if (newest.Count == 4)
            {
                break;
            }
        }

        Assert.Equal(
            [
                ObjectLedgerNames.Slot(ledger7, new Slot(249_000)),
                ObjectLedgerNames.Slot(ledger7, new Slot(248_000)),
                ObjectLedgerNames.Slot(ledger7, new Slot(247_000)),
                ObjectLedgerNames.Slot(ledger7, new Slot(246_000)),
            ],
            newest.Select(static entry => entry.Name));
        Assert.InRange(fake.ListedPrefixes.Count, 1, 15);
        // Must jump directly into ledger 7's s/ directory (never list root or other ledgers).
        Assert.All(fake.ListedPrefixes, static listed => Assert.StartsWith("led/ger/s.l/.00/000/000/000/000/000/007/.s./", listed, StringComparison.Ordinal));

        // AfterLedger(7) must skip ledger 7's entire slots subtree and reach ledger 8 directly.
        fake.ResetListedPrefixes();
        var afterLedger7 = await store.ListAsync(ObjectLedgerNames.LedgersPrefix, ObjectLedgerNames.AfterLedger(ledger7), Token).ToListAsync(Token);
        var onlyLedger8 = Assert.Single(afterLedger7);
        Assert.Equal(ObjectLedgerNames.Slot(ledger8, new Slot(0)), onlyLedger8.Name);
        Assert.DoesNotContain(fake.ListedPrefixes, static listed => listed.Contains("007/.s./", StringComparison.Ordinal));
    }

    /// <summary>
    /// Deleting objects when empty directories remain in <c>CommonPrefixes</c>, and encountering foreign keys or directories
    /// under both empty and non-empty store prefixes, leaves ordinal listing exact. Also verifies <see cref="ObjectName.IsValid"/>
    /// rejects <c>'.'</c> and <c>'L'</c> (the layout's reserved characters).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("p/")]
    public async Task EmptyDirectoriesAndForeignEntriesAreSkippedCleanly(string storePrefix)
    {
        Assert.False(ObjectName.IsValid("a.b"));
        Assert.False(ObjectName.IsValid("aLb"));
        Assert.False(ObjectName.IsValid("."));
        Assert.False(ObjectName.IsValid("L"));

        using var fake = new FakeS3DirectoryBucketService(listingOrder: FakeS3ListingOrder.Reversed, maxPageSize: 2, retainEmptyDirectories: true);
        using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);
        var store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName, prefix: storePrefix);

        using (var payload = ArcBuffer.Wrap(new byte[] { 1, 2, 3 }))
        {
            _ = await store.CreateAsync(new ObjectName("alpha/one"), payload, null, Token);
            _ = await store.CreateAsync(new ObjectName("beta/two"), payload, null, Token);
            _ = await store.CreateAsync(new ObjectName("gamma/three"), payload, null, Token);
        }

        // Delete beta/two (leaving empty directories behind in the fake) and add extra empty directories and foreign keys.
        await store.DeleteAsync(new ObjectName("beta/two"), Token);
        fake.AddEmptyDirectory(storePrefix + "bbb/ccc/ddd/");
        fake.AddEmptyDirectory(storePrefix + ".ab/");
        fake.AddEmptyDirectory(storePrefix + "a../");
        fake.AddForeignObject(storePrefix + "L", [1]);
        fake.AddForeignObject(storePrefix + "abcdL", [2]);
        fake.AddForeignObject(storePrefix + "ab.L", [3]);
        fake.AddForeignObject(storePrefix + string.Concat(Enumerable.Repeat("aaa/", 66)) + "aaaL", [4]);
        fake.AddForeignObject(storePrefix + "notaleaf", [5]);
        fake.AddForeignObject(storePrefix + "BAD/keyL", [6]);
        fake.AddForeignObject(storePrefix + "too-long-seg/aL", [7]);
        fake.AddForeignObject(storePrefix + "alp/..L", [8]);

        var listed = (await store.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token))
            .Select(static entry => entry.Name.Value)
            .ToArray();

        Assert.Equal(["alpha/one", "gamma/three"], listed);
    }

    /// <summary>
    /// <see cref="PrefixedObjectStore"/> composes cleanly over a directory-bucket <see cref="S3ObjectStore"/>.
    /// </summary>
    [Fact]
    public async Task PrefixedObjectStoreComposesOverDirectoryBucketMode()
    {
        using var fake = new FakeS3DirectoryBucketService(listingOrder: FakeS3ListingOrder.Reversed, maxPageSize: 2);
        using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);
        var raw = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName);
        var clusterA = new PrefixedObjectStore(raw, "cluster-a/");
        var clusterB = new PrefixedObjectStore(raw, "cluster-b/");

        using (var payload = ArcBuffer.Wrap("payload"u8.ToArray()))
        {
            _ = await clusterA.CreateAsync(new ObjectName("manifest"), payload, null, Token);
            _ = await clusterA.CreateAsync(new ObjectName("d/1/0/a1"), payload, null, Token);
            _ = await clusterB.CreateAsync(new ObjectName("manifest"), payload, null, Token);
        }

        Assert.Equal(["d/1/0/a1", "manifest"], (await clusterA.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token)).Select(static entry => entry.Name.Value));
        Assert.Equal(["manifest"], (await clusterA.ListAsync(string.Empty, "d/1/0/a1", Token).ToListAsync(Token)).Select(static entry => entry.Name.Value));
        Assert.Equal(["manifest"], (await clusterB.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token)).Select(static entry => entry.Name.Value));
    }

    /// <summary>
    /// <see cref="S3ObjectStore.GetPropertiesAsync"/> checks bucket existence with <c>Delimiter = "/"</c> and
    /// <c>MaxKeys = 1</c> (omitting empty <c>Prefix</c>) in directory-bucket mode, distinguishing a missing object from a
    /// missing bucket; and cancellation stops a listing before any request as well as between buffered leaves.
    /// </summary>
    [Fact]
    public async Task MissingBucketAndCancelledListingAreHandledInDirectoryBucketMode()
    {
        using var fake = new FakeS3DirectoryBucketService(bucketName: "existing--usw2-az1--x-s3");
        using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);

        var rootStore = new S3ObjectStore(client, "existing--usw2-az1--x-s3");
        Assert.Null(await rootStore.GetPropertiesAsync(new ObjectName("absent"), Token));
        var rootProbe = Assert.Single(fake.ListRequests);
        Assert.Equal("/", rootProbe.Delimiter);
        Assert.Equal(1, rootProbe.MaxKeys);
        Assert.False(rootProbe.HasPrefixParameter);
        Assert.Equal(string.Empty, rootProbe.Prefix);

        fake.ResetListedPrefixes();
        var existingStore = new S3ObjectStore(client, "existing--usw2-az1--x-s3", prefix: "p/");
        Assert.Null(await existingStore.GetPropertiesAsync(new ObjectName("absent"), Token));
        var prefixedProbe = Assert.Single(fake.ListRequests);
        Assert.Equal("/", prefixedProbe.Delimiter);
        Assert.Equal(1, prefixedProbe.MaxKeys);
        Assert.Equal("p/", prefixedProbe.Prefix);

        var missingStore = new S3ObjectStore(client, "missing--usw2-az1--x-s3", prefix: "p/");
        var refused = await Assert.ThrowsAsync<ObjectStoreRefusedException>(async () => await missingStore.GetPropertiesAsync(new ObjectName("absent"), Token));
        Assert.False(refused.IsAuthorization);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        fake.ResetListedPrefixes();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await existingStore.ListAsync(string.Empty, cancellationToken: cancelled.Token).ToListAsync(Token));
        Assert.Empty(fake.ListedPrefixes);

        // Cancellation between buffered leaves of the same directory stops without yielding remaining leaves.
        using (var content = ArcBuffer.Wrap(new byte[] { 1 }))
        {
            _ = await existingStore.CreateAsync(new ObjectName("a"), content, null, Token);
            _ = await existingStore.CreateAsync(new ObjectName("b"), content, null, Token);
            _ = await existingStore.CreateAsync(new ObjectName("c"), content, null, Token);
        }

        using var midCancel = new CancellationTokenSource();
        var seen = new List<string>();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var entry in existingStore.ListAsync(string.Empty, cancellationToken: midCancel.Token))
            {
                seen.Add(entry.Name.Value);
                await midCancel.CancelAsync();
            }
        });
        Assert.Equal(["a"], seen);
    }

    /// <summary>
    /// <see cref="S3ObjectStore"/> enables directory-bucket mode if and only if the bucket name has a non-empty prefix
    /// before the lowercase <c>--x-s3</c> suffix.
    /// </summary>
    [Theory]
    [InlineData("ledgers--usw2-az1--x-s3", true)]
    [InlineData("a--x-s3", true)]
    [InlineData("ledgers", false)]
    [InlineData("--x-s3", false)]
    [InlineData("ledgers--usw2-az1--X-S3", false)]
    [InlineData("ledgers-x-s3", false)]
    [InlineData("ledgers--x-s3-extra", false)]
    [InlineData("ledgers--x-s30", false)]
    public async Task IsDirectoryBucketRecognizesOnlyDirectoryBucketNames(string bucketName, bool expectedDirectoryBucket)
    {
        using var transport = new TransportDouble { Respond = static _ => StubResponses.S3Written() };
        using var client = CloudClients.S3Client(StubResponses.Unserved, transport, disableS3ExpressSessionAuth: true);
        var store = new S3ObjectStore(client, bucketName);

        using var content = ArcBuffer.Wrap(new byte[] { 7 });
        Assert.IsType<ObjectCreateOutcome.Created>((await store.CreateAsync(new ObjectName("a"), content, null, Token)).Value);

        var request = Assert.Single(transport.Requests);
        Assert.EndsWith(expectedDirectoryBucket ? "/aL" : "/a", request.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// In directory-bucket mode over HTTPS, <see cref="S3ObjectStore"/> leaves the payload unsigned while sending the
    /// writer's CRC-32C header, and classifies a <c>400 BadDigest</c> refusal as <see cref="ObjectContentChangedException"/>.
    /// </summary>
    [Fact]
    public async Task HttpsDirectoryBucketSendsUnsignedCrc32CAndRejectsBadDigestAsContentChanged()
    {
        var payload = "directory-bucket-https"u8.ToArray();
        var crc = Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, payload);
        using var content = ArcBuffer.Wrap(payload);

        using (var okTransport = new TransportDouble { Respond = static _ => StubResponses.S3Written() })
        using (var okClient = CloudClients.S3Client(new Uri("https://127.0.0.1:9"), okTransport, disableS3ExpressSessionAuth: true))
        {
            var store = new S3ObjectStore(okClient, FakeS3DirectoryBucketService.DefaultBucketName);
            Assert.IsType<ObjectCreateOutcome.Created>((await store.CreateAsync(new ObjectName("d/1/0/a1"), content, crc, Token)).Value);

            var request = Assert.Single(okTransport.Requests);
            Assert.Equal(Uri.UriSchemeHttps, request.Uri.Scheme);
            Assert.EndsWith("/d.1/.0./a1L", request.Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal(Crc32CBase64(crc), request.Header("x-amz-checksum-crc32c"));
            Assert.Equal("UNSIGNED-PAYLOAD", request.Header("x-amz-content-sha256"));
            Assert.Null(request.Header("x-amz-trailer"));
            Assert.DoesNotContain("aws-chunked", request.Header("Content-Encoding") ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(payload, request.Body);
        }

        using var badDigestTransport = new TransportDouble { Respond = static _ => StubResponses.S3Error(HttpStatusCode.BadRequest, "BadDigest") };
        using var badDigestClient = CloudClients.S3Client(new Uri("https://127.0.0.1:9"), badDigestTransport, disableS3ExpressSessionAuth: true);
        var rejectingStore = new S3ObjectStore(badDigestClient, FakeS3DirectoryBucketService.DefaultBucketName);

        _ = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await rejectingStore.CreateAsync(new ObjectName("d/1/0/a1"), content, crc, Token));
        _ = await Assert.ThrowsAsync<ObjectContentChangedException>(async () => await rejectingStore.ReplaceAsync(ObjectName.Manifest, new ObjectVersion("\"v1\""), content, crc, Token));
    }

    /// <summary>
    /// A <c>409 ConditionalRequestConflict</c> on a directory-bucket <c>CreateAsync</c> or <c>ReplaceAsync</c> throws
    /// <see cref="AmazonS3Exception"/>, leaving the outcome unknown.
    /// </summary>
    [Fact]
    public async Task ConditionalRequestConflictLeavesOutcomeUnknown()
    {
        using var transport = new TransportDouble { Respond = static _ => StubResponses.S3Error(HttpStatusCode.Conflict, "ConditionalRequestConflict") };
        using var client = CloudClients.S3Client(StubResponses.Unserved, transport, disableS3ExpressSessionAuth: true);
        var store = new S3ObjectStore(client, FakeS3DirectoryBucketService.DefaultBucketName, prefix: "p/");
        using var content = ArcBuffer.Wrap("conflict"u8.ToArray());

        var createConflict = await Assert.ThrowsAsync<AmazonS3Exception>(async () => await store.CreateAsync(new ObjectName("manifest"), content, null, Token));
        Assert.Equal(HttpStatusCode.Conflict, createConflict.StatusCode);
        Assert.Equal("ConditionalRequestConflict", createConflict.ErrorCode);

        var replaceConflict = await Assert.ThrowsAsync<AmazonS3Exception>(async () => await store.ReplaceAsync(ObjectName.Manifest, new ObjectVersion("\"v1\""), content, null, Token));
        Assert.Equal(HttpStatusCode.Conflict, replaceConflict.StatusCode);
        Assert.Equal("ConditionalRequestConflict", replaceConflict.ErrorCode);

        Assert.All(transport.Requests, static req => Assert.EndsWith("/p/man/ife/stL", req.Uri.AbsolutePath, StringComparison.Ordinal));
    }

    /// <summary>
    /// Malformed S3 responses (truncated listing without continuation token, missing or negative size on a valid leaf,
    /// and blank ETag on <c>ReadVersionedAsync</c> or <c>CreateAsync</c>) throw <see cref="IOException"/> without
    /// silently truncating or leaking pooled buffers.
    /// </summary>
    [Fact]
    public async Task MalformedServiceResponsesFailWithIOExceptionAndReleaseBuffers()
    {
        const string truncatedXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
            "<Name>bucket</Name><Prefix></Prefix><KeyCount>0</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>true</IsTruncated>" +
            "</ListBucketResult>";

        foreach (var bucket in new[] { FakeS3DirectoryBucketService.DefaultBucketName, "general-purpose" })
        {
            using var truncatedTransport = new TransportDouble
            {
                Respond = static _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(truncatedXml, Encoding.UTF8, "application/xml"),
                },
            };
            using var truncatedClient = CloudClients.S3Client(StubResponses.Unserved, truncatedTransport, disableS3ExpressSessionAuth: true);
            var truncatedStore = new S3ObjectStore(truncatedClient, bucket);
            _ = await Assert.ThrowsAsync<IOException>(async () => await truncatedStore.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token));
        }

        const string negativeSizeXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
            "<Name>bucket</Name><Prefix></Prefix><Delimiter>/</Delimiter><KeyCount>1</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated>" +
            "<Contents><Key>aL</Key><Size>-1</Size></Contents>" +
            "</ListBucketResult>";
        const string missingSizeXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
            "<Name>bucket</Name><Prefix></Prefix><Delimiter>/</Delimiter><KeyCount>1</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated>" +
            "<Contents><Key>aL</Key></Contents>" +
            "</ListBucketResult>";

        foreach (var badSizeXml in new[] { negativeSizeXml, missingSizeXml })
        {
            using var sizeTransport = new TransportDouble
            {
                Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(badSizeXml, Encoding.UTF8, "application/xml"),
                },
            };
            using var sizeClient = CloudClients.S3Client(StubResponses.Unserved, sizeTransport, disableS3ExpressSessionAuth: true);
            var sizeStore = new S3ObjectStore(sizeClient, FakeS3DirectoryBucketService.DefaultBucketName);
            _ = await Assert.ThrowsAsync<IOException>(async () => await sizeStore.ListAsync(string.Empty, cancellationToken: Token).ToListAsync(Token));
        }

        // Blank ETag on ReadVersionedAsync and CreateAsync fails with IOException and leaks no ArcBuffer.
        using var blankEtagTransport = new TransportDouble
        {
            Respond = static request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = request.Method == HttpMethod.Get
                        ? new ByteArrayContent([1, 2, 3])
                        : new ByteArrayContent([]),
                };
                _ = response.Headers.TryAddWithoutValidation("ETag", "   ");
                return response;
            },
        };
        using var blankEtagClient = CloudClients.S3Client(StubResponses.Unserved, blankEtagTransport, disableS3ExpressSessionAuth: true);
        var blankStore = new S3ObjectStore(blankEtagClient, FakeS3DirectoryBucketService.DefaultBucketName);

        _ = await Assert.ThrowsAsync<IOException>(async () => await blankStore.ReadVersionedAsync(new ObjectName("a"), Token));
        using var upload = ArcBuffer.Wrap(new byte[] { 1, 2, 3 });
        _ = await Assert.ThrowsAsync<IOException>(async () => await blankStore.CreateAsync(new ObjectName("a"), upload, null, Token));
    }

    /// <summary>
    /// Verifies that <see cref="FakeS3DirectoryBucketService"/> itself enforces S3 Express One Zone constraints:
    /// rejecting any presence of <c>start-after</c> (including empty), non-<c>/</c> delimiters, non-<c>/</c>-terminated
    /// prefixes (<c>InvalidRequest</c>), negative <c>max-keys</c>, <c>max-keys=0</c>, continuation tokens mismatched on
    /// <c>(Prefix, Delimiter)</c>, file-versus-directory collisions (including retained empty directories), unique
    /// per-upload ETags, and faithful header/trailer CRC-32C validation and omission.
    /// </summary>
    [Fact]
    public async Task TheFakeEnforcesS3ExpressDirectoryBucketConstraints()
    {
        using var fake = new FakeS3DirectoryBucketService(maxPageSize: 1, retainEmptyDirectories: true);
        using var client = CloudClients.S3Client(StubResponses.Unserved, fake, disableS3ExpressSessionAuth: true);
        using var rawHttp = new HttpClient(fake, disposeHandler: false);

        var startAfterError = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Delimiter = "/", StartAfter = "a" }, Token));
        Assert.Equal("InvalidArgument", startAfterError.ErrorCode);

        using (var emptyStartAfter = await rawHttp.GetAsync(new Uri(StubResponses.Unserved, $"/{FakeS3DirectoryBucketService.DefaultBucketName}?list-type=2&delimiter=/&start-after="), Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, emptyStartAfter.StatusCode);
            Assert.Contains("InvalidArgument", await emptyStartAfter.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        var prefixError = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Delimiter = "/", Prefix = "abc" }, Token));
        Assert.Equal("InvalidRequest", prefixError.ErrorCode);

        var delimiterError = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Delimiter = "-" }, Token));
        Assert.Equal("InvalidArgument", delimiterError.ErrorCode);

        var negMaxKeysError = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Delimiter = "/", MaxKeys = -1 }, Token));
        Assert.Equal("InvalidArgument", negMaxKeysError.ErrorCode);

        // Unique per-upload ETags for identical content + omitted CRC-32C when PutObject did not supply one.
        var put1 = await client.PutObjectAsync(new PutObjectRequest { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Key = "dir/file", ContentBody = "x" }, Token);
        var put2 = await client.PutObjectAsync(new PutObjectRequest { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Key = "dir/file2", ContentBody = "x" }, Token);
        Assert.NotEqual(put1.ETag, put2.ETag);

        var headWithoutCrc = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Key = "dir/file", ChecksumMode = ChecksumMode.ENABLED }, Token);
        Assert.Null(headWithoutCrc.ChecksumCRC32C);

        // max-keys=0 returns 0 entries, IsTruncated=false, no token.
        var zeroPage = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Delimiter = "/", Prefix = "dir/", MaxKeys = 0 }, Token);
        Assert.Empty(zeroPage.S3Objects ?? []);
        Assert.Empty(zeroPage.CommonPrefixes ?? []);
        Assert.False(zeroPage.IsTruncated);
        Assert.Null(zeroPage.NextContinuationToken);

        // Continuation token is bound to (Prefix, Delimiter).
        var firstPage = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Delimiter = "/", Prefix = "dir/" }, Token);
        Assert.True(firstPage.IsTruncated);
        Assert.NotNull(firstPage.NextContinuationToken);
        var badTokenPrefix = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Delimiter = "/", Prefix = "other/", ContinuationToken = firstPage.NextContinuationToken }, Token));
        Assert.Equal("InvalidArgument", badTokenPrefix.ErrorCode);
        var badTokenDelimiter = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Prefix = "dir/", ContinuationToken = firstPage.NextContinuationToken }, Token));
        Assert.Equal("InvalidArgument", badTokenDelimiter.ErrorCode);

        // Path collision against an existing object prefix and against a retained empty directory.
        var collisionError = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            client.PutObjectAsync(new PutObjectRequest { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Key = "dir", ContentBody = "y" }, Token));
        Assert.Equal("InvalidRequest", collisionError.ErrorCode);

        fake.AddEmptyDirectory("empty/sub/");
        var emptyDirCollision = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            client.PutObjectAsync(new PutObjectRequest { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Key = "empty", ContentBody = "y" }, Token));
        Assert.Equal("InvalidRequest", emptyDirCollision.ErrorCode);

        // Faithful CRC-32C header mismatch and aws-chunked trailer validation + storage.
        var badHeaderCrc = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.PutObjectAsync(new PutObjectRequest { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Key = "crc-header", ContentBody = "abc", ChecksumCRC32C = Crc32CBase64(0x12345678u) }, Token));
        Assert.Equal("BadDigest", badHeaderCrc.ErrorCode);

        var chunkPayload = "hello"u8.ToArray();
        var goodCrc = Crc32CBase64(Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, chunkPayload));
        var badCrc = Crc32CBase64(Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, chunkPayload) ^ 1u);

        using (var goodTrailerReq = BuildAwsChunkedPut($"/{FakeS3DirectoryBucketService.DefaultBucketName}/crc-trailer", chunkPayload, goodCrc))
        using (var goodTrailerResp = await rawHttp.SendAsync(goodTrailerReq, Token))
        {
            Assert.Equal(HttpStatusCode.OK, goodTrailerResp.StatusCode);
        }

        var headWithTrailerCrc = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = FakeS3DirectoryBucketService.DefaultBucketName, Key = "crc-trailer", ChecksumMode = ChecksumMode.ENABLED }, Token);
        Assert.Equal(goodCrc, headWithTrailerCrc.ChecksumCRC32C);

        using var badTrailerReq = BuildAwsChunkedPut($"/{FakeS3DirectoryBucketService.DefaultBucketName}/crc-trailer-bad", chunkPayload, badCrc);
        using var badTrailerResp = await rawHttp.SendAsync(badTrailerReq, Token);
        Assert.Equal(HttpStatusCode.BadRequest, badTrailerResp.StatusCode);
        Assert.Contains("BadDigest", await badTrailerResp.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    private static HttpRequestMessage BuildAwsChunkedPut(string path, byte[] payload, string trailerCrc32C)
    {
        var bodyText = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{payload.Length:x}\r\n{Encoding.ASCII.GetString(payload)}\r\n0\r\nx-amz-checksum-crc32c:{trailerCrc32C}\r\n\r\n");
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(StubResponses.Unserved, path))
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes(bodyText)),
        };
        request.Content.Headers.ContentEncoding.Add("aws-chunked");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", "STREAMING-UNSIGNED-PAYLOAD-TRAILER");
        request.Headers.TryAddWithoutValidation("x-amz-trailer", "x-amz-checksum-crc32c");
        return request;
    }

    private static string Crc32CBase64(uint crc)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, crc);
        return Convert.ToBase64String(bytes);
    }

    private static Task<DirectoryBucketTarget> Target(
        FakeS3ListingOrder order,
        int seed,
        int maxPageSize,
        bool retainEmptyDirectories,
        OutcomeTally? tally = null,
        double faultRate = 0) =>
        Task.FromResult(new DirectoryBucketTarget(order, seed, maxPageSize, retainEmptyDirectories, tally, faultRate));

    private ConformanceKit<ObjectStoreState, DirectoryBucketTarget> Kit(
        Func<int, Task<DirectoryBucketTarget>> create,
        bool unknownOutcomes,
        bool listings = true,
        Gen<byte>? values = null,
        Gen<int>? lengths = null) =>
        new ConformanceKit<ObjectStoreState, DirectoryBucketTarget>(
                ObjectStoreSpec.Create(unknownOutcomes),
                static () => new ObjectStoreState(),
                create,
                ObjectStoreSpec.IsCallerAllowed,
                Token,
                _leaks.InScopeAsync,
                ObjectStoreSpec.Conflicts)
            .ObjectStoreOperations(static (target, request) => target.RunAsync(request), listings, values, lengths);

    private sealed class OutcomeTally
    {
        private int _definite;
        private int _unknown;

        public void Count(bool definite) => _ = definite ? Interlocked.Increment(ref _definite) : Interlocked.Increment(ref _unknown);

        public void AssertMostlyDefinite() =>
            Assert.True(_definite > 2 * _unknown, $"{_definite} calls answered and {_unknown} left their outcomes unknown.");
    }

    private sealed class DirectoryBucketTarget : IAsyncDisposable
    {
        private readonly FakeS3DirectoryBucketService _fake;
        private readonly TransportDouble _transport;
        private readonly AmazonS3Client _client;
        private readonly ObjectStoreExecutor _executor;
        private readonly OutcomeTally? _tally;

        public DirectoryBucketTarget(
            FakeS3ListingOrder order,
            int seed,
            int maxPageSize,
            bool retainEmptyDirectories,
            OutcomeTally? tally,
            double faultRate)
        {
            _fake = new FakeS3DirectoryBucketService(
                listingOrder: order,
                seed: seed,
                maxPageSize: maxPageSize,
                retainEmptyDirectories: retainEmptyDirectories,
                duplicateAcrossPages: maxPageSize < 10);
            _transport = new TransportDouble { InnerHandler = _fake };
            if (faultRate > 0)
            {
                _transport.FailRandomly(seed, faultRate);
            }

            _client = CloudClients.S3Client(StubResponses.Unserved, _transport, disableS3ExpressSessionAuth: true);
            var store = new S3ObjectStore(_client, FakeS3DirectoryBucketService.DefaultBucketName, prefix: "case/");
            _executor = new ObjectStoreExecutor(store, appendable: null);
            _tally = tally;
        }

        public async Task<ObjectOutcome> RunAsync(object request)
        {
            var faults = TransportDouble.TrackCall();
            try
            {
                var outcome = await _executor.RunAsync(request, Token);
                _tally?.Count(definite: true);
                return outcome;
            }
            catch (Exception exception) when (_tally is not null && (exception is IOException and not EndOfStreamException || faults.Value > 0 && IsFaultFailure(exception)))
            {
                _tally.Count(definite: false);
                return ObjectOutcome.Unknown;
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            _transport.Dispose();
            _fake.Dispose();
            return ValueTask.CompletedTask;
        }

        private static bool IsFaultFailure(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is TransportDoubleException or AmazonServiceException { StatusCode: HttpStatusCode.ServiceUnavailable })
                {
                    return true;
                }
            }

            return false;
        }
    }
}
