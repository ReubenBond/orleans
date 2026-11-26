using System.Collections.Concurrent;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Faults a network between silos injects around <see cref="ILedgerStoreGrain"/> calls, at the callee (OR-I16): each
/// ledger a case registers draws its own seeded faults, since a sample's cases run in parallel, so a call fails before its
/// request reaches the host, loses its reply after the host ran it, is delivered twice, or is delayed past the caller's
/// response timeout and run afterwards. Calls of any other ledger pass through.
/// </summary>
/// <remarks>
/// The <see cref="CalleeFaultsMutant.AnswersForTheStore"/> mutant answers a call whose request it lost as an empty replica
/// would, an answer no store gave, which the shared spec must refuse (LR-I9).
/// </remarks>
internal sealed class CalleeFaults(TimeSpan delay, CalleeFaultsMutant mutant = CalleeFaultsMutant.None)
{
    private readonly ConcurrentDictionary<ulong, LedgerFaults> _ledgers = new();
    private readonly Lock _lock = new();
    private readonly List<(Task Delivery, object Request)> _pending = [];
    private int _faults;

    private enum Fault
    {
        None,
        LostRequest,
        LostReply,
        Duplicated,
        Delayed,
    }

    /// <summary>Gets the number of faults injected.</summary>
    public int Faults => Volatile.Read(ref _faults);

    /// <summary>Seeds the faults of <paramref name="ledgers"/>' calls from a case's <paramref name="seed"/>.</summary>
    public void Seed(IReadOnlyList<LedgerId> ledgers, int seed)
    {
        for (var index = 0; index < ledgers.Count; index++)
        {
            _ledgers[ledgers[index].Value] = new LedgerFaults(new Random(unchecked(seed * 31 + index)));
        }
    }

    /// <summary>
    /// Waits for every delayed or duplicated delivery to end, each with an answer or a failure the callee's silo may give
    /// once it stops, and checks that the callee released each delivery's payload (OR-I18).
    /// </summary>
    public async Task DeliverAsync()
    {
        (Task Delivery, object Request)[] pending;
        lock (_lock)
        {
            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (var (delivery, request) in pending)
        {
            var failure = await Record.ExceptionAsync(() => delivery);
            Assert.True(failure is null or ObjectDisposedException or OperationCanceledException or TimeoutException, $"A delivery the network repeated failed: {failure}");
            if (request is LedgerReplicaWriteRequest write)
            {
                Assert.True(WireBytes.IsReleased(write.Payload), "The callee did not release a repeated delivery's payload.");
            }
        }
    }

    /// <summary>Runs a call which reached its callee, with the fault its ledger draws.</summary>
    public async Task InvokeAsync(IIncomingGrainCallContext context, ILedgerStoreGrain grain, object request)
    {
        var fault = _ledgers.TryGetValue(LedgerOf(request), out var faults) ? faults.Next() : Fault.None;
        if (fault != Fault.None)
        {
            _ = Interlocked.Increment(ref _faults);
        }

        switch (fault)
        {
            case Fault.LostRequest when mutant == CalleeFaultsMutant.AnswersForTheStore:
                if (request is LedgerReplicaWriteRequest answered)
                {
                    answered.Payload?.Dispose();
                }

                context.Result = EmptyReplicasAnswer(request);
                return;
            case Fault.LostRequest:
                if (request is LedgerReplicaWriteRequest lost)
                {
                    lost.Payload?.Dispose();
                }

                throw new TimeoutException("The request was lost.");
            case Fault.LostReply:
                await context.Invoke();
                if (context.Result is RemoteReplicaRead { Value: RemoteReplicaRead.Data lostData })
                {
                    lostData.Admission?.Dispose();
                    lostData.Bytes?.Dispose();
                }
                else if (context.Result is RemoteReplicaRangeRead { Value: RemoteReplicaRangeRead.Batch lostBatch })
                {
                    lostBatch.Admission?.Dispose();
                    lostBatch.Bytes?.Dispose();
                }

                throw new TimeoutException("The reply was lost.");
            case Fault.Duplicated:
                var duplicate = Redecoded(request);
                await context.Invoke();
                Track(Deliver(grain, duplicate), duplicate);
                return;
            case Fault.Delayed:
                // The network holds the message, so its delivery now never reaches the host, and it arrives later.
                var delayed = Redecoded(request);
                if (request is LedgerReplicaWriteRequest held)
                {
                    held.Payload.Dispose();
                }

                Track(DelayedAsync(grain, delayed), delayed);
                await Task.Delay(delay, TimeProvider.System);
                throw new TimeoutException("The request was delayed past the response timeout.");
            case Fault.None:
                await context.Invoke();
                return;
            default:
                throw new InvalidOperationException($"No fault {fault}.");
        }
    }

    private static ulong LedgerOf(object request) => request switch
    {
        LedgerReplicaWriteRequest write => write.Ledger,
        LedgerReplicaReadRequest read => read.Ledger,
        LedgerReplicaTailRequest tail => tail.Ledger,
        _ => throw new InvalidOperationException($"{request} is no replica call."),
    };

    /// <summary>Delivers <paramref name="request"/> to the grain again, as a duplicated message would, without the caller.</summary>
    private static Task Deliver(ILedgerStoreGrain grain, object request) => request switch
    {
        LedgerReplicaWriteRequest write => grain.WriteAsync(write, CancellationToken.None),
        LedgerReplicaReadRequest read => grain.ReadAsync(read, CancellationToken.None),
        LedgerReplicaTailRequest tail => grain.ReadTailAsync(tail, CancellationToken.None),
        _ => throw new InvalidOperationException($"{request} is no replica call."),
    };

    /// <summary>
    /// Returns <paramref name="request"/> as another decoding of its message would: a write's payload in a buffer of its
    /// own, which its callee releases.
    /// </summary>
    private static object Redecoded(object request) =>
        request is LedgerReplicaWriteRequest write ? write with { Payload = WireBytes.Arc(write.Payload.ToArray()) } : request;

    private static object EmptyReplicasAnswer(object request) => request switch
    {
        LedgerReplicaWriteRequest => new RemoteReplicaWriteOutcome(ReplicaWriteOutcome.Written.Instance),
        LedgerReplicaReadRequest => new RemoteReplicaRead(ReplicaRead.Missing.Instance),
        LedgerReplicaTailRequest tail => new RemoteReplicaTailRead(new RemoteReplicaTailRead.Tail(tail.ContiguousTail, IsSealed: false)),
        _ => throw new InvalidOperationException($"{request} is no replica call."),
    };

    /// <summary>Delivers the request once the caller's response timeout has passed, as a request delayed in the network is.</summary>
    private async Task DelayedAsync(ILedgerStoreGrain grain, object request)
    {
        await Task.Delay(delay, TimeProvider.System);
        await Deliver(grain, request);
    }

    private void Track(Task delivery, object request)
    {
        lock (_lock)
        {
            _pending.Add((delivery, request));
        }
    }

    /// <summary>One ledger's seeded faults: one call in eight draws a fault.</summary>
    private sealed class LedgerFaults(Random random)
    {
        private readonly Lock _lock = new();

        public Fault Next()
        {
            lock (_lock)
            {
                return random.Next(8) == 0 ? (Fault)random.Next(1, 5) : Fault.None;
            }
        }
    }
}

/// <summary>A defect of <see cref="CalleeFaults"/>, which the conformance run must catch.</summary>
internal enum CalleeFaultsMutant
{
    /// <summary>None: every lost request fails.</summary>
    None,

    /// <summary>A lost request is answered as an empty replica would answer it, an answer no store gave.</summary>
    AnswersForTheStore,
}
