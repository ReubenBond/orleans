using CsCheck;

namespace Orleans.Durable.Storage.Engine.Threading.Tests;

/// <summary>
/// Checks the gate against a sequential model, and its ordering of admission against closure under concurrency.
/// </summary>
public sealed class AdmissionGatePropertyTests
{
    private enum Step
    {
        EnterScoped,
        EnterUnscoped,
        ExitOne,
        Close,
    }

    /// <summary>
    /// Every sequence of entries, exits, and closes behaves as the model: an entry is admitted exactly when the gate is
    /// open, and the drain completes exactly when the gate is closed and no admission is outstanding.
    /// </summary>
    [Fact]
    public void EverySequenceFollowsTheModel() =>
        Gen.Enum<Step>().Array[0, 64].Sample(static steps =>
        {
            var gate = new AdmissionGate();
            var outstanding = new List<AdmissionGate.Admission?>();
            Task? drained = null;
            foreach (var step in steps)
            {
                switch (step)
                {
                    case Step.EnterScoped:
                        var admission = gate.TryEnter();
                        Assert.Equal(drained is null, admission.Entered);
                        if (admission.Entered)
                        {
                            outstanding.Add(admission);
                        }

                        break;
                    case Step.EnterUnscoped:
                        var entered = gate.TryEnterUnscoped();
                        Assert.Equal(drained is null, entered);
                        if (entered)
                        {
                            outstanding.Add(null);
                        }

                        break;
                    case Step.ExitOne when outstanding.Count > 0:
                        if (outstanding[0] is { } scoped)
                        {
                            scoped.Dispose();
                        }
                        else
                        {
                            gate.Exit();
                        }

                        outstanding.RemoveAt(0);
                        break;
                    case Step.ExitOne:
                        break;
                    case Step.Close:
                        var closed = gate.CloseAsync();
                        Assert.Same(drained ?? closed, closed);
                        drained = closed;
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected step {step}.");
                }

                Assert.Equal(drained is not null, gate.IsClosed);
                Assert.Equal(drained is not null && outstanding.Count == 0, drained?.IsCompletedSuccessfully ?? false);
            }
        });

    /// <summary>
    /// Under concurrent entries, no entry which starts after <see cref="AdmissionGate.CloseAsync"/> returns is admitted,
    /// and no admitted operation is still running when the drain completes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoOperationIsAdmittedAfterCloseOrRunsAfterTheDrain(bool unscoped)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const int Workers = 4;
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var gate = new AdmissionGate();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = new Probe();
            var workers = Enumerable.Range(0, Workers).Select(_ => Task.Run(
                async () =>
                {
                    await start.Task;
                    while (true)
                    {
                        var afterClose = Volatile.Read(ref probe.CloseReturned);
                        if (unscoped)
                        {
                            if (!gate.TryEnterUnscoped())
                            {
                                return;
                            }

                            try
                            {
                                probe.Run(afterClose);
                            }
                            finally
                            {
                                gate.Exit();
                            }
                        }
                        else
                        {
                            using var admission = gate.TryEnter();
                            if (!admission.Entered)
                            {
                                return;
                            }

                            probe.Run(afterClose);
                        }
                    }
                },
                cancellationToken)).ToArray();
            var closing = Task.Run(
                async () =>
                {
                    await start.Task;
                    Thread.SpinWait(Random.Shared.Next(0, 2_000));
                    var drained = gate.CloseAsync();
                    Volatile.Write(ref probe.CloseReturned, true);
                    await drained;
                    return Volatile.Read(ref probe.Inside);
                },
                cancellationToken);

            start.SetResult();
            var insideAtDrain = await closing.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Assert.Equal(0, insideAtDrain);
            Assert.Equal(0, Volatile.Read(ref probe.AdmittedAfterClose));
        }
    }

    private sealed class Probe
    {
        public int Inside;
        public int AdmittedAfterClose;
        public bool CloseReturned;

        public void Run(bool afterClose)
        {
            if (afterClose)
            {
                _ = Interlocked.Increment(ref AdmittedAfterClose);
            }

            _ = Interlocked.Increment(ref Inside);
            Thread.SpinWait(Random.Shared.Next(0, 64));
            _ = Interlocked.Decrement(ref Inside);
        }
    }
}
