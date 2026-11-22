using System.Globalization;
using Microsoft.Extensions.Time.Testing;

namespace Orleans.Durable.Storage.Testing.Tests;

public sealed class HistoryRecorderTests
{
    /// <summary>
    /// Timestamps come from the time provider, and an event recorded at an instant the clock has already stamped is
    /// one tick later, so the history keeps the order of events even when virtual time stands still.
    /// </summary>
    [Fact]
    public void TimestampsFollowTheClockAndTheOrderOfEvents()
    {
        var time = new FakeTimeProvider();
        var recorder = new HistoryRecorder<RegisterInput, int>(time);
        var start = time.GetTimestamp();

        var put = recorder.Invoke(0, RegisterInput.Put(1));
        recorder.Complete(put, 0);
        var get = recorder.Invoke(1, RegisterInput.Get);
        time.Advance(TimeSpan.FromSeconds(1));
        recorder.Complete(get, 0, "stale");
        var annotation = recorder.Now();

        var history = recorder.History();
        Assert.Equal(
            [
                new HistoryOperation<RegisterInput, int>(0, RegisterInput.Put(1), start, 0, start + 1),
                new HistoryOperation<RegisterInput, int>(1, RegisterInput.Get, start + 2, 0, time.GetTimestamp(), "stale"),
            ],
            history);
        Assert.Equal(time.GetTimestamp() + 1, annotation);

        // The get started after the put returned at the same virtual instant, so it may not miss the put.
        Assert.Equal(LinearizabilityVerdict.Illegal, LinearizabilityChecker.Check(RegisterModel.Instance, history, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// An operation completed without a bound may take effect at any later time, and a pending operation is an error
    /// unless the caller supplies an output for it, which also makes it unbounded.
    /// </summary>
    [Fact]
    public void UnboundedAndPendingOperationsHaveNoResponseTime()
    {
        var time = new FakeTimeProvider();
        var recorder = new HistoryRecorder<JournalInput, JournalOutput>(time);
        var ambiguous = recorder.Invoke(0, JournalInput.Append("j", JournalModel.Origin, "a"u8.ToArray()), "first");
        recorder.CompleteUnbounded(ambiguous, JournalOutput.Ambiguous);
        var pending = recorder.Invoke(1, JournalInput.Read("j"));

        _ = Assert.Throws<InvalidOperationException>(() => recorder.History());
        var history = recorder.History(static _ => JournalOutput.Ambiguous);

        Assert.All(history, static operation => Assert.Equal(HistoryOperation.Unbounded, operation.Return));
        Assert.Equal(["first", null], history.Select(static operation => operation.Metadata));
        Assert.Equal(JournalOutput.Ambiguous, history[1].Output);
        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(JournalModel.Instance, history, TestContext.Current.CancellationToken));

        recorder.Complete(pending, JournalOutput.Read(true, "v1", "a"u8.ToArray()));
        _ = Assert.Throws<InvalidOperationException>(() => recorder.Complete(pending, JournalOutput.Read(true, "v1", "a"u8.ToArray())));
        _ = Assert.Throws<InvalidOperationException>(() => recorder.Complete(new HistoryRecorder<JournalInput, JournalOutput>.Invocation(5), JournalOutput.Accepted("v2")));
        Assert.Equal(LinearizabilityVerdict.Ok, LinearizabilityChecker.Check(JournalModel.Instance, recorder.History(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Concurrent clients of a register record a linearizable history, with every interval containing its operation.
    /// </summary>
    [Fact]
    public async Task ConcurrentClientsRecordALinearizableHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var recorder = new HistoryRecorder<JournalInput, JournalOutput>(TimeProvider.System);
        var journal = new LockedJournal();
        var clients = Enumerable.Range(0, 4).Select(client => Task.Run(
            () =>
            {
                for (var iteration = 0; iteration < 50; iteration++)
                {
                    var read = recorder.Invoke(client, JournalInput.Read("j"));
                    var (exists, version, content) = journal.Read();
                    recorder.Complete(read, JournalOutput.Read(exists, version, content));

                    var input = JournalInput.Append("j", version, new[] { (byte)client, (byte)iteration });
                    var append = recorder.Invoke(client, input);
                    recorder.Complete(append, journal.Append(input));
                }
            },
            cancellationToken)).ToArray();
        await Task.WhenAll(clients);

        var history = recorder.History();

        Assert.Equal(400, history.Length);
        Assert.All(history, static operation => Assert.True(operation.Call < operation.Return));
        LinearizabilityAssert.Linearizable(JournalModel.Instance, history, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A journal guarded by a lock: linearizable by construction. Its version after <c>n</c> appends is <c>vn</c>.
    /// </summary>
    private sealed class LockedJournal
    {
        private readonly Lock _lock = new();
        private readonly List<byte> _content = [];
        private int _appends;

        public (bool Exists, string Version, byte[] Content) Read()
        {
            lock (_lock)
            {
                return (_appends > 0, Version(_appends), [.. _content]);
            }
        }

        public JournalOutput Append(JournalInput input)
        {
            lock (_lock)
            {
                if (input.Expected != Version(_appends))
                {
                    return JournalOutput.Rejected(Version(_appends));
                }

                _content.AddRange(input.Payload.Span);
                return JournalOutput.Accepted(Version(++_appends));
            }
        }

        private static string Version(int appends) => appends == 0 ? JournalModel.Origin : string.Create(CultureInfo.InvariantCulture, $"v{appends}");
    }
}
