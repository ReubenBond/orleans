using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using Azure;
using CsCheck;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// Racing writers of one key's chain (archive-logs.md §8, decision 18; LOG-I1, LOG-I2): two or three writers, each a
/// drain without state, write the chain's writes in the order their scripts give, re-issuing earlier ones as stragglers
/// and racing drains do, abandoning some calls in flight, through a store whose requests fail before and after they land
/// and whose retries meet their own landing. The oracle: every <c>Held</c> answers a log which holds the write when it is
/// answered; no write is ever corrupt; no generation is created twice; and a last writer, with no faults, finds the whole
/// chain held or lands what is missing, after which the log reads as the fold of the chain, and stays so once every request
/// still in flight lands.
/// </summary>
internal static class ArchiveLogRaces
{
    /// <summary>The log the races write.</summary>
    internal static readonly ArchiveLogName Log = new("k/race", 0x7ace_0000_0000_0001);

    private static readonly AsyncLocal<bool> Checking = new();

    private static readonly LedgerSegmentId Segment = new(new LedgerId(11), Slot.Zero);

    /// <summary>
    /// A write of the chain: an opener, 0 for none, 1 a <c>Replace</c> of <paramref name="BaseLength"/> bytes, 2 an empty
    /// <c>Delete</c>; then <c>Append</c>s of <paramref name="Appends"/> bytes.
    /// </summary>
    internal sealed record ChainWrite(int Opener, int BaseLength, int[] Appends);

    /// <summary>A writer's step: the chain's write it writes, and whether it abandons the call in flight.</summary>
    internal sealed record Step(int Write, bool Abandons);

    /// <summary>A race: the chain, each writer's script, the writers' retries, and the seed of the store's faults.</summary>
    internal sealed record RaceCase(ChainWrite[] Chain, Step[][] Writers, int MaxRetries, int Seed)
    {
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"chain [{string.Join("; ", Chain.Select(static write => $"{write.Opener}/{write.BaseLength}/[{string.Join(",", write.Appends)}]"))}] writers [{string.Join(" | ", Writers.Select(static steps => string.Join(",", steps.Select(static step => step.Abandons ? step.Write + "~" : step.Write.ToString(CultureInfo.InvariantCulture)))))}] retries {MaxRetries} seed {Seed}");
    }

    /// <summary>
    /// A store under race: the store, the creations of each generation, and the delivery of requests still in flight.
    /// </summary>
    internal sealed record RaceStore(IArchiveLogStore Store, Func<ulong, int> Creations, Action DeliverDeferred);

    /// <summary>Gets whether the current call is the oracle's own, which a store's fault injector spares.</summary>
    internal static bool IsChecking => Checking.Value;

    /// <summary>The races: a chain of one to five writes, and two or three writers of up to twice its writes each.</summary>
    internal static Gen<RaceCase> Cases { get; } =
        from chain in Gen.Select(Gen.Int[0, 2], Gen.Int[0, 3], Gen.Int[1, 3].Array[0, 3], static (opener, length, appends) => new ChainWrite(opener, length, appends))
            .Where(static write => write.Opener != 0 || write.Appends.Length > 0)
            .Array[1, 5]
        from writers in Gen.Select(Gen.Int[0, chain.Length - 1], Gen.Int[0, 7], static (write, abandon) => new Step(write, abandon == 0)).Array[1, chain.Length * 2].Array[2, 3]
        from retries in Gen.Int[0, 1]
        from seed in Gen.Int
        select new RaceCase(chain, writers, retries, seed);

    /// <summary>
    /// Runs <paramref name="race"/> against the store <paramref name="withStore"/> gives for its seed, which owns it for the
    /// run and checks what it keeps afterwards; each writer starts through <paramref name="start"/>, on the thread pool or a
    /// simulation's scheduler.
    /// </summary>
    internal static Task CheckAsync(RaceCase race, Func<int, Func<RaceStore, Task>, Task> withStore, Func<Func<Task>, Task> start, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(race);
        ArgumentNullException.ThrowIfNull(withStore);
        return withStore(race.Seed, target => RunAsync(race, target, start, timeProvider));
    }

    private static async Task RunAsync(RaceCase race, RaceStore target, Func<Func<Task>, Task> start, TimeProvider timeProvider)
    {
        var store = target.Store;
        var (writes, tokens) = Build(store.Layout, race.Chain);
        var writer = new ArchiveLogWriter(store, timeProvider, new ArchiveLogWriterOptions { MaxRetries = race.MaxRetries, Delay = TimeSpan.Zero });
        var abandoned = new List<Task>();
        var racers = race.Writers.Select(steps => start(async () =>
        {
            foreach (var step in steps)
            {
                var call = WriteAsync(writer, writes[step.Write], race.Chain[step.Write], step.Write);
                if (step.Abandons)
                {
                    lock (abandoned)
                    {
                        abandoned.Add(call);
                    }
                }
                else
                {
                    await call;
                }
            }
        })).ToList();

        // Awaited one by one rather than through Task.WhenAll, which a simulation test assembly does not call
        // (eng/BannedSymbols.Simulation.txt): every racer ends before the first fault, in start order, is thrown.
        foreach (var racer in racers)
        {
            await racer.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }

        foreach (var racer in racers)
        {
            await racer;
        }

        foreach (var call in abandoned)
        {
            await call;
        }

        // The last writer, with no faults, finds each write held or lands it, in the chain's order.
        Checking.Value = true;
        var last = new ArchiveLogWriter(store, timeProvider, new ArchiveLogWriterOptions { MaxRetries = 0 });
        for (var index = 0; index < writes.Length; index++)
        {
            var verdict = await last.WriteAsync(Log, writes[index], Content(race.Chain[index], index), CancellationToken.None);
            Assert.True(verdict.Value is ArchiveLogConditionOutcome.Held, $"The last writer finds write {index} {verdict}, not held.");
        }

        await CheckFoldAsync(store, race.Chain, tokens[^1]);
        target.DeliverDeferred();
        await CheckFoldAsync(store, race.Chain, tokens[^1]);
        for (var generation = 1ul; generation <= ArchiveLogTokens.PositionOf(tokens[^1]).Generation; generation++)
        {
            Assert.True(target.Creations(generation) <= 1, $"Generation {generation} was created {target.Creations(generation)} times.");
        }

        Checking.Value = false;

        // A Held answers a log which holds the write: positions only grow, so one read after the answer checks it.
        async Task WriteAsync(ArchiveLogWriter racer, ArchiveWrite write, ChainWrite chainWrite, int index)
        {
            ArchiveLogConditionOutcome verdict;
            try
            {
                verdict = await racer.WriteAsync(Log, write, Content(chainWrite, index), CancellationToken.None);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                return;
            }

            if (verdict.Value is ArchiveLogConditionOutcome.Held)
            {
                Checking.Value = true;
                var read = await store.GetPropertiesAsync(Log, CancellationToken.None);
                Assert.True(
                    read is not null && ArchiveLogConditions.Holds(read.Position, write, write.Commands.Length),
                    $"Write {index} answered Held, yet the log at {read?.Position.ToString() ?? "absent"} does not hold it.");
            }
            else
            {
                Assert.True(index > 0, "A key's first write found its predecessor missing.");
            }
        }
    }

    /// <summary>Returns whether a racing call failed at its transport, so its outcome is unknown.</summary>
    internal static bool IsTransportFailure(Exception exception) => exception switch
    {
        IOException or HttpRequestException => true,
        RequestFailedException { Status: 0 or >= 500 } => true,
        AggregateException aggregate => aggregate.InnerExceptions.All(IsTransportFailure),
        _ => false,
    };

    /// <summary>Returns the chain's writes, each from the token its predecessor's plan gives, and the token after each.</summary>
    private static (ArchiveWrite[] Writes, RowToken[] Tokens) Build(ArchiveLogLayout layout, ChainWrite[] chain)
    {
        var writes = new ArchiveWrite[chain.Length];
        var tokens = new RowToken[chain.Length];
        RowToken token = default;
        for (var index = 0; index < chain.Length; index++)
        {
            writes[index] = new ArchiveWrite(Segment, token, Commands(chain[index]));
            var plan = new ArchiveLogPlan(layout, ArchiveLogTokens.PositionOf(token));
            foreach (var command in writes[index].Commands)
            {
                plan.Add(command);
            }

            token = tokens[index] = plan.End();
        }

        return (writes, tokens);
    }

    private static ImmutableArray<ArchiveCommand> Commands(ChainWrite write)
    {
        var commands = ImmutableArray.CreateBuilder<ArchiveCommand>();
        if (write.Opener != 0)
        {
            commands.Add(write.Opener == 1 ? ArchiveCommand.Replace(write.BaseLength) : ArchiveCommand.Delete(default));
        }

        commands.AddRange(write.Appends.Select(ArchiveCommand.Append));
        return commands.ToImmutable();
    }

    /// <summary>A write's bodies: each command's bytes, a distinct value per write and command, a <c>Delete</c> having none.</summary>
    private static byte[] Bytes(ChainWrite write, int index)
    {
        var lengths = (write.Opener == 1 ? [write.BaseLength] : Array.Empty<int>()).Concat(write.Appends);
        return [.. lengths.SelectMany((length, command) => Enumerable.Repeat(unchecked((byte)(index * 16 + command + 1)), length))];
    }

    private static ReadOnlySequence<byte> Content(ChainWrite write, int index) => new(Bytes(write, index));

    /// <summary>Checks that the log reads as the fold of the chain: its last opener's base and every append after it, at the chain's last token.</summary>
    private static async Task CheckFoldAsync(IArchiveLogStore store, ChainWrite[] chain, RowToken last)
    {
        var from = Array.FindLastIndex(chain, static write => write.Opener != 0);
        byte[] expected = [.. Enumerable.Range(Math.Max(from, 0), chain.Length - Math.Max(from, 0)).SelectMany(index => Bytes(chain[index], index))];
        using var read = Assert.IsType<ArchiveLogRead.Read>((await store.ReadAsync(Log, CancellationToken.None)).Value);
        Assert.Equal(ArchiveLogTokens.PositionOf(last), read.Position);
        Assert.Equal(expected, read.Content.ToArray());
    }
}
