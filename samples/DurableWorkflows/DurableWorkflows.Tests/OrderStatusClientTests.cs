using System.Runtime.CompilerServices;
using DurableWorkflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.DurableTasks;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Xunit;

namespace DurableWorkflows.Tests;

public sealed class OrderStatusClientTests
{
    [Fact]
    public async Task AlreadySatisfiedInitialSnapshotCompletesAndDisposes()
    {
        var workflow = new StatusWorkflow();
        var expected = new OrderStatus("order", OrderStage.Completed);
        workflow.Subscribe = token => Updates(workflow, new(expected, Guid.NewGuid(), 5, true), null, token);

        var result = await Client(workflow).WaitAsync(status => status.IsTerminal);

        Assert.Equal(expected, result);
        Assert.Equal(1, workflow.Subscriptions);
        Assert.Equal(1, workflow.Disposals);
    }

    [Theory]
    [InlineData("silo")]
    [InlineData("enumerator")]
    [InlineData("timeout")]
    [InlineData("connection")]
    public async Task TransientDisconnectDisposesThenReacquiresLowerVersionBaseline(string failure)
    {
        var workflow = new StatusWorkflow();
        var first = new OrderStatusUpdate(new("order", OrderStage.Preparing), Guid.NewGuid(), 50, true);
        var reset = new OrderStatusUpdate(new("order", OrderStage.NotStarted), Guid.NewGuid(), 0, true);
        Exception exception = failure switch
        {
            "silo" => new SiloUnavailableException(),
            "enumerator" => new EnumerationAbortedException(),
            "connection" => new ConnectionFailedException(),
            _ => new TimeoutException()
        };
        workflow.Subscribe = token =>
        {
            Assert.Equal(workflow.Subscriptions - 1, workflow.Disposals);
            return workflow.Subscriptions == 1
                ? Updates(workflow, first, exception, token)
                : Updates(workflow, reset, null, token);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var watch = Client(workflow).WatchAsync(timeout.Token).GetAsyncEnumerator();

        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(first, watch.Current);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(reset, watch.Current);
        Assert.NotEqual(first.SubscriptionId, watch.Current.SubscriptionId);
        Assert.Equal(2, workflow.Subscriptions);
        Assert.Equal(1, workflow.Disposals);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("decoding")]
    [InlineData("contract")]
    public async Task TerminalFailureSurfacesWithoutRetry(string failure)
    {
        var workflow = new StatusWorkflow();
        Exception exception = failure switch
        {
            "authorization" => new UnauthorizedAccessException(),
            "decoding" => new InvalidDataException(),
            _ => new InvalidOperationException()
        };
        workflow.Subscribe = token => Updates(workflow, null, exception, token);

        var observed = await Assert.ThrowsAnyAsync<Exception>(() => Client(workflow).WaitAsync(_ => true));

        Assert.Same(exception, observed);
        Assert.Equal(1, workflow.Subscriptions);
        Assert.Equal(1, workflow.Disposals);
    }

    [Fact]
    public async Task PredicateFailureDisposesSubscription()
    {
        var workflow = new StatusWorkflow();
        workflow.Subscribe = token => Updates(workflow,
            new(new("order", OrderStage.Preparing), Guid.NewGuid(), 1, true), null, token);
        var expected = new ArgumentException("predicate");

        var observed = await Assert.ThrowsAsync<ArgumentException>(() =>
            Client(workflow).WaitAsync(_ => throw expected));

        Assert.Same(expected, observed);
        Assert.Equal(1, workflow.Disposals);
    }

    [Fact]
    public async Task CancellationWhileBlockedDisposesSubscription()
    {
        var workflow = new StatusWorkflow();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.Subscribe = token => BlockAsync(token);
        using var cancellation = new CancellationTokenSource();
        var wait = Client(workflow).WaitAsync(_ => false, cancellation.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(1, workflow.Disposals);
        Assert.Equal(1, workflow.Subscriptions);

        async IAsyncEnumerable<OrderStatusUpdate> BlockAsync([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                blocked.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                yield break;
            }
            finally
            {
                workflow.Disposals++;
            }
        }
    }

    [Fact]
    public async Task CancellationDuringBackoffEndsReconnect()
    {
        var workflow = new StatusWorkflow();
        using var cancellation = new CancellationTokenSource();
        workflow.Subscribe = token => Updates(workflow, null, new TimeoutException(), token);
        var client = new OrderStatusClient(workflow, new CancelOnRetryLogger(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.WaitAsync(_ => true, cancellation.Token));

        Assert.Equal(1, workflow.Subscriptions);
        Assert.Equal(1, workflow.Disposals);
    }

    private static OrderStatusClient Client(StatusWorkflow workflow) => new(workflow, NullLogger<OrderStatusClient>.Instance);

    private sealed class CancelOnRetryLogger(CancellationTokenSource cancellation) : ILogger<OrderStatusClient>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            cancellation.Cancel();
    }

    private static async IAsyncEnumerable<OrderStatusUpdate> Updates(
        StatusWorkflow workflow,
        OrderStatusUpdate? update,
        Exception? failure,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            if (update is not null)
            {
                yield return update;
            }

            if (failure is not null)
            {
                throw failure;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            workflow.Disposals++;
        }
    }

    private sealed class StatusWorkflow : IWorkflowGrain
    {
        public Func<CancellationToken, IAsyncEnumerable<OrderStatusUpdate>> Subscribe { get; set; } = null!;
        public int Subscriptions { get; private set; }
        public int Disposals { get; set; }

        public IAsyncEnumerable<OrderStatusUpdate> WatchOrderStatusAsync(CancellationToken cancellationToken = default)
        {
            Subscriptions++;
            return Subscribe(cancellationToken);
        }

        public Task<OrderStatus> GetOrderStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkflowRuntimeInfo> GetRuntimeInfoAsync() => throw new NotSupportedException();
        public DurableTask<BasicWorkflowResult> RunBasicAsync(string input) => throw new NotSupportedException();
        public DurableTask<FanOutWorkflowResult> RunFanOutAsync(string[] items) => throw new NotSupportedException();
        public DurableTask<ApprovalWorkflowResult> RunApprovalAsync(ApprovalRequest request) => throw new NotSupportedException();
        public DurableTask<CancellationWorkflowResult> RunCancellationAsync(TimeSpan holdPeriod) => throw new NotSupportedException();
        public DurableTask<OrderWorkflowResult> RunOrderSagaAsync(OrderRequest request) => throw new NotSupportedException();
    }
}
