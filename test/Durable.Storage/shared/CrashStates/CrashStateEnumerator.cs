using System.Globalization;
using System.Text;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Enumerates the crash states a recorded trace allows under an explicit persistence model, after ALICE (Pillai et al.,
/// OSDI '14) and CrashMonkey (Mohan et al., OSDI '18) (verification.md §7.5).
/// </summary>
/// <remarks>
/// <para>
/// The persistence model is the POSIX <c>fsync</c> contract over sector-atomic writes, which the WAL's flushes meet on
/// every platform: <c>fsync</c> on Linux and macOS, and <c>FlushFileBuffers</c> of a file's or a directory's handle on
/// Windows, on NTFS (wal.md §9):
/// </para>
/// <list type="number">
/// <item><description><b>Inodes.</b> Directories map names to inodes and files hold bytes. Every operation names the
/// inodes it changes: entry operations change one directory (create, mkdir, link, rename within it, unlink, rmdir), and
/// content operations change one file (write, truncate).</description></item>
/// <item><description><b>Durability.</b> A content operation is durable once a later <c>fsync</c> of its file
/// completes; an entry operation is durable once a later <c>fsync</c> of its directory completes. An <c>fsync</c>
/// covers only the writes which completed before it. A write-through write's bytes are durable once it completes,
/// independently of the file's other writes, and the length it extends once a later <c>fsync</c> of its file completes,
/// as Windows' write-through writes require; <c>O_DSYNC</c> makes the length durable too, which this model does not
/// rely on. A write-through write which fails, or whose process crashes, is durable once a later <c>fsync</c> of its
/// file completes: its I/O is direct, so the device holds what reads of it return. Nothing else makes an operation
/// durable: a file's <c>fsync</c> leaves its name unsynced, and a directory's <c>fsync</c> leaves its own name in its
/// parent unsynced.</description></item>
/// <item><description><b>Loss and order.</b> An operating-system crash after operation <c>k</c> keeps every operation
/// durable by then and any subset of the others: each unsynced operation is independently lost or kept. Kept
/// operations take effect in issue order, so a later operation can survive an earlier one's loss, which is how unsynced
/// operations reorder.</description></item>
/// <item><description><b>Atomicity.</b> An entry operation and a truncation are atomic; a rename replaces its target
/// atomically. An unsynced write is lost, kept whole, torn, or zero-extended. A torn write persists some of the 512-byte
/// sectors it covers: each proper prefix, the last sector alone, and one further subset chosen by the seed, with the file
/// extended to the end of each persisted sector. A zero-extended write persists the file's new length but none of its
/// bytes, so the file grows with zeros, as ext4's <c>data=writeback</c> allows. A completed write-through write whose
/// length is unsynced persists with or without that extension.</description></item>
/// <item><description><b>Visibility.</b> The crash state is what the root reaches. An operation in a directory whose
/// removal is durable, or on a file created in one, is unreachable whatever happens to it.</description></item>
/// </list>
/// <para>
/// A process crash loses nothing: the page cache survives it, so a trace continued by a restarted process is enumerated
/// as one trace. A crash point lies between any two recorded operations. At each point the enumerator takes every
/// combination of the unsynced operations' outcomes, up to <see cref="PointLimit"/> combinations per point, keeping the
/// point's strictest state (every unsynced operation lost) and most complete state (every one kept) and sampling the
/// rest with the seed beyond it, and deduplicates the point's images. Beyond a caller's limit, which is raised to two
/// states per point, it keeps every point's strictest and most complete states and samples the others with the
/// seed.
/// </para>
/// </remarks>
internal sealed class CrashStateEnumerator
{
    /// <summary>
    /// The unit in which a crash tears a write.
    /// </summary>
    public const int SectorSize = 512;

    /// <summary>
    /// The most combinations of unsynced operations taken at one crash point; the seed samples beyond it.
    /// </summary>
    public const int PointLimit = 1 << 10;

    private const int Lost = 0;
    private const int Kept = 1;
    private const int ZeroExtended = 2;
    private const int FirstTear = 3;

    // The ranks of a point's states: the strictest, the most complete, and the others.
    private const int Strictest = 0;
    private const int MostComplete = 1;
    private const int Sampled = 2;

    private readonly FileSystemImage _initial;
    private readonly int[] _durableFrom;
    private readonly int[] _bytesDurableFrom;
    private readonly Dictionary<int, int> _fileDirectory = [];
    private readonly int _seed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrashStateEnumerator"/> class over a trace which starts from
    /// <paramref name="initial"/>, durable.
    /// </summary>
    public CrashStateEnumerator(FileSystemImage initial, IReadOnlyList<FileSystemOperation> operations, int seed)
    {
        _initial = initial.Clone();
        Operations = operations;
        _seed = seed;

        // The flush which makes each operation durable: the first flush of the inode it names after it completed. A
        // write-through write completes at its SyncWrite, which makes its bytes durable; the first flush of its file
        // after that makes the length it extends durable. A write-through write which fails, or whose process crashes,
        // ends at its FailWrite, and the first flush of its file after that makes it durable whole: the device holds
        // what reads of it return.
        _durableFrom = new int[operations.Count];
        _bytesDurableFrom = new int[operations.Count];
        Array.Fill(_bytesDurableFrom, int.MaxValue);
        var flushAfter = new int[operations.Count];
        var nextFlush = new Dictionary<int, int>();
        for (var index = operations.Count - 1; index >= 0; index--)
        {
            var operation = operations[index];
            var flushedBy = operation.FlushedBy >= 0 ? operation.FlushedBy : operation.Node;
            flushAfter[index] = nextFlush.TryGetValue(flushedBy, out var flush) ? flush + 1 : int.MaxValue;
            _durableFrom[index] = operation.FlushedBy >= 0 && !operation.WriteThrough ? flushAfter[index] : int.MaxValue;
            if (operation.Kind is FileSystemOperationKind.Flush or FileSystemOperationKind.FlushDirectory)
            {
                nextFlush[operation.Node] = index;
            }
        }

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (operation.Kind == FileSystemOperationKind.SyncWrite)
            {
                _bytesDurableFrom[operation.CompletedWrite] = index + 1;
                _durableFrom[operation.CompletedWrite] = flushAfter[index];
            }
            else if (operation.Kind == FileSystemOperationKind.FailWrite)
            {
                _durableFrom[operation.CompletedWrite] = flushAfter[index];
            }
            else if (operation.Kind == FileSystemOperationKind.CreateFile)
            {
                _fileDirectory[operation.Node] = operation.Directory;
            }
        }
    }

    /// <summary>
    /// Gets the recorded operations.
    /// </summary>
    public IReadOnlyList<FileSystemOperation> Operations { get; }

    /// <summary>
    /// Creates an enumerator over everything <paramref name="fileSystem"/> recorded.
    /// </summary>
    public static CrashStateEnumerator Of(RecordingFileSystem fileSystem, int seed) =>
        new(fileSystem.InitialImage, fileSystem.Operations, seed);

    /// <summary>
    /// Returns the first crash point at which operation <paramref name="operation"/> is durable, or
    /// <see cref="int.MaxValue"/> when it never becomes durable.
    /// </summary>
    public int DurableFrom(int operation) => _durableFrom[operation];

    /// <summary>
    /// Returns the distinct crash states after each of operations <paramref name="from"/> through
    /// <paramref name="to"/> (a crash at point <c>k</c> follows the first <c>k</c> operations), keeping at most
    /// <paramref name="limit"/> or two per point, whichever is more.
    /// </summary>
    /// <remarks>
    /// Images are deduplicated within each point, never across points: an image is judged against what had been
    /// acknowledged at its own point, and a later point has acknowledged more. Beyond the limit, every point's strictest
    /// state (every unsynced operation lost) and most complete state (every one kept) are kept, and the seed samples
    /// the others.
    /// </remarks>
    public CrashStateSet Enumerate(int from, int to, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(to, Operations.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var points = to - from + 1;
        var distinct = new List<(CrashState State, int Rank)>();
        var enumerated = 0;
        var sampledPoints = 0;
        for (var point = from; point <= to; point++)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var sampled = false;
            foreach (var (state, rank) in StatesAt(point))
            {
                enumerated++;
                sampled |= state.Sampled;
                var fingerprint = Build(state).Fingerprint();
                if (seen.TryGetValue(fingerprint, out var index))
                {
                    distinct[index] = (distinct[index].State, Math.Min(distinct[index].Rank, rank));
                }
                else
                {
                    seen.Add(fingerprint, distinct.Count);
                    distinct.Add((state, rank));
                }
            }

            sampledPoints += sampled ? 1 : 0;
        }

        var cap = Math.Max(limit, 2L * points);
        var selected = distinct;
        if (distinct.Count > cap)
        {
            // Each point's strictest and most complete images, at most two per point, fit within the cap.
            var others = distinct.Where(static state => state.Rank == Sampled).ToList();
            new Random(Mix(_seed, from, to)).Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(others));
            selected = [.. distinct.Where(static state => state.Rank != Sampled)];
            selected = [.. selected.Concat(others.Take((int)(cap - selected.Count))).OrderBy(static state => state.State.Point)];
        }

        return new CrashStateSet(points, sampledPoints, enumerated, distinct.Count, [.. selected.Select(static state => state.State)]);
    }

    /// <summary>
    /// Builds the page cache after the first <paramref name="point"/> operations: every operation kept whole.
    /// </summary>
    public FileSystemImage Through(int point)
    {
        var image = _initial.Clone();
        for (var index = 0; index < point; index++)
        {
            image.Apply(Operations[index]);
        }

        return image;
    }

    /// <summary>
    /// Builds a crash state's image.
    /// </summary>
    public FileSystemImage Build(CrashState state)
    {
        var image = _initial.Clone();
        var outcomes = state.Unsynced.Zip(state.Outcomes).ToDictionary(static pair => pair.First, static pair => pair.Second);
        for (var index = 0; index < state.Point; index++)
        {
            var operation = Operations[index];
            if (_durableFrom[index] <= state.Point)
            {
                image.Apply(operation);
                continue;
            }

            if (_bytesDurableFrom[index] <= state.Point)
            {
                // A completed write-through write: only the length it extends is unsynced.
                if (outcomes.GetValueOrDefault(index, Lost) == Kept)
                {
                    image.Apply(operation);
                }
                else
                {
                    image.ApplyData(operation);
                }

                continue;
            }

            switch (outcomes.GetValueOrDefault(index, Lost))
            {
                case Lost:
                    break;
                case Kept:
                    image.Apply(operation);
                    break;
                case ZeroExtended:
                    image.ApplyZeroExtension(operation);
                    break;
                case var tear:
                    image.Apply(operation, TornSectors(state.Point, index, tear - FirstTear));
                    break;
            }
        }

        return image;
    }

    /// <summary>
    /// Describes a crash state: its point and what became of each unsynced operation.
    /// </summary>
    public string Describe(CrashState state)
    {
        var text = new StringBuilder();
        _ = text.Append(CultureInfo.InvariantCulture, $"Crash after {state.Point} of {Operations.Count} operations");
        _ = state.Point > 0
            ? text.Append(CultureInfo.InvariantCulture, $", the last {Operations[state.Point - 1]}.").AppendLine()
            : text.Append('.').AppendLine();
        for (var position = 0; position < state.Unsynced.Count; position++)
        {
            var index = state.Unsynced[position];
            _ = text.Append(CultureInfo.InvariantCulture, $"  {Outcome(state.Point, index, state.Outcomes[position])}: #{index} {Operations[index]}").AppendLine();
        }

        return text.Append(Build(state).Describe()).ToString();
    }

    private static int Mix(int seed, long first, long second)
    {
        unchecked
        {
            var value = ((uint)seed * 0x9E3779B97F4A7C15UL) ^ ((ulong)first * 0xBF58476D1CE4E5B9UL) ^ ((ulong)second * 0x94D049BB133111EBUL);
            value ^= value >> 31;
            return (int)value ^ (int)(value >> 32);
        }
    }

    /// <summary>
    /// Returns the number of tears a write over <paramref name="sectors"/> sectors may persist: each proper prefix,
    /// the last sector alone, and, over three or more sectors, one seeded subset.
    /// </summary>
    private static int Tears(long sectors) => sectors switch
    {
        < 2 => 0,
        2 => 2,
        _ => (int)sectors + 1,
    };

    private string Outcome(int point, int index, int outcome)
    {
        if (_bytesDurableFrom[index] <= point)
        {
            return outcome == Kept ? "kept" : "its length extension lost";
        }

        var sectors = Operations[index].Sectors;
        return outcome switch
        {
            Lost => "lost",
            Kept => "kept",
            ZeroExtended => "zero-extended",
            _ when outcome - FirstTear < sectors - 1 => string.Create(CultureInfo.InvariantCulture, $"torn to its first {outcome - FirstTear + 1} sectors"),
            _ when outcome - FirstTear == sectors - 1 => "torn to its last sector",
            _ => "torn to seeded sectors",
        };
    }

    private int Options(int point, int index) =>
        Operations[index].Kind == FileSystemOperationKind.Write && _bytesDurableFrom[index] > point
            ? FirstTear + Tears(Operations[index].Sectors)
            : 2;

    private IEnumerable<(CrashState State, int Rank)> StatesAt(int point)
    {
        var unsynced = Unsynced(point);
        var options = unsynced.Select(index => Options(point, index)).ToArray();
        var combinations = 1L;
        foreach (var count in options)
        {
            combinations = Math.Min(combinations * count, PointLimit + 1L);
        }

        if (combinations <= PointLimit)
        {
            var outcomes = new int[unsynced.Count];
            for (var combination = 0L; combination < combinations; combination++)
            {
                var remaining = combination;
                for (var position = 0; position < outcomes.Length; position++)
                {
                    outcomes[position] = (int)(remaining % options[position]);
                    remaining /= options[position];
                }

                yield return (new CrashState(point, unsynced, [.. outcomes]), Rank(outcomes));
            }

            yield break;
        }

        // Too many combinations to take every one: keep the strictest and most complete states and sample the rest
        // with the seed.
        yield return (new CrashState(point, unsynced, new int[unsynced.Count]) { Sampled = true }, Strictest);
        yield return (new CrashState(point, unsynced, [.. Enumerable.Repeat(Kept, unsynced.Count)]) { Sampled = true }, MostComplete);
        var random = new Random(Mix(_seed, point, -1));
        for (var sample = 2; sample < PointLimit; sample++)
        {
            yield return (new CrashState(point, unsynced, [.. options.Select(count => random.Next(count))]) { Sampled = true }, Sampled);
        }

        static int Rank(int[] outcomes)
        {
            if (outcomes.All(static outcome => outcome == Lost))
            {
                return Strictest;
            }

            return outcomes.All(static outcome => outcome == Kept) ? MostComplete : Sampled;
        }
    }

    /// <summary>
    /// Returns the operations before <paramref name="point"/> which are neither durable nor unreachable.
    /// </summary>
    private List<int> Unsynced(int point)
    {
        // A directory whose removal is durable, or which lies in such a directory, is unreachable in every state.
        var removed = new HashSet<int>();
        for (var index = point - 1; index >= 0; index--)
        {
            var operation = Operations[index];
            if (operation.Kind == FileSystemOperationKind.RemoveDirectory && (_durableFrom[index] <= point || removed.Contains(operation.Directory)))
            {
                _ = removed.Add(operation.Node);
            }
        }

        var unsynced = new List<int>();
        for (var index = 0; index < point; index++)
        {
            var operation = Operations[index];
            if (!operation.IsEntry && !operation.IsContent || _durableFrom[index] <= point)
            {
                continue;
            }

            var directory = operation.IsEntry ? operation.Directory : _fileDirectory.GetValueOrDefault(operation.Node, -1);
            if (!removed.Contains(directory))
            {
                unsynced.Add(index);
            }
        }

        return unsynced;
    }

    /// <summary>
    /// Returns which sectors tear <paramref name="tear"/> of a write persists: a prefix, the last sector alone, or a
    /// proper, non-empty subset chosen by the seed.
    /// </summary>
    private Func<long, bool> TornSectors(int point, int index, int tear)
    {
        var operation = Operations[index];
        var first = operation.Offset / SectorSize;
        var count = (int)operation.Sectors;
        if (tear < count - 1)
        {
            return sector => sector - first <= tear;
        }

        if (tear == count - 1)
        {
            return sector => sector - first == count - 1;
        }

        var random = new Random(Mix(_seed, point, index));
        bool[] persisted;
        do
        {
            persisted = [.. Enumerable.Range(0, count).Select(_ => random.Next(2) == 1)];
        }
        while (persisted.All(static sector => sector) || !persisted.Any(static sector => sector));

        return sector => persisted[(int)(sector - first)];
    }
}

/// <summary>
/// One crash state: the crash point and the outcome of each unsynced operation.
/// </summary>
/// <param name="Point">The number of operations before the crash.</param>
/// <param name="Unsynced">The unsynced operations at the crash, in issue order.</param>
/// <param name="Outcomes">For each unsynced operation: 0 lost, 1 kept, 2 zero-extended, and from 3 a tear; for a completed
/// write-through write, 0 without and 1 with the length it extends.</param>
internal sealed record CrashState(int Point, IReadOnlyList<int> Unsynced, IReadOnlyList<int> Outcomes)
{
    /// <summary>
    /// Gets a value indicating whether the state was sampled from a point with more than
    /// <see cref="CrashStateEnumerator.PointLimit"/> combinations of outcomes.
    /// </summary>
    public bool Sampled { get; init; }
}

/// <summary>
/// The crash states an enumeration selected, with its counts.
/// </summary>
/// <param name="Points">The crash points enumerated.</param>
/// <param name="SampledPoints">The points with more combinations of outcomes than one point enumerates, whose states the
/// seed sampled.</param>
/// <param name="Enumerated">The states enumerated, before deduplication.</param>
/// <param name="Distinct">The distinct images among them, counted per crash point.</param>
/// <param name="States">The selected states, one per distinct image of a point, in crash-point order.</param>
internal sealed record CrashStateSet(int Points, int SampledPoints, int Enumerated, int Distinct, IReadOnlyList<CrashState> States)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Points} crash points ({SampledPoints} sampled), {Enumerated} crash states, {Distinct} distinct images, {States.Count} verified");
}
