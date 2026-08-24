using System.Reflection;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using TestExtensions;
using Xunit;

namespace UnitTests.ConcurrencyTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class RequestSchedulerTests
{
    [Fact, TestCategory("BVT"), TestCategory("ReadOnly")]
    public void WritableRequest_WaitsUntilEveryReadOnlyRequestCompletes()
    {
        var synchronizationRoot = new object();
        var scheduler = new RequestScheduler(synchronizationRoot, new TestRequestSchedulerContext());
        var firstRead = CreateMessage(isReadOnly: true);
        var secondRead = CreateMessage(isReadOnly: true);
        var write = CreateMessage();

        lock (synchronizationRoot)
        {
            Start(scheduler, firstRead);
            Start(scheduler, secondRead);

            Assert.False(scheduler.CanRun(write));
            Assert.Same(firstRead, scheduler.GetBlockingRequest(out _));

            Assert.True(scheduler.CompleteRequest(firstRead));
            Assert.False(scheduler.CanRun(write));
            Assert.Same(secondRead, scheduler.GetBlockingRequest(out _));

            Assert.True(scheduler.CompleteRequest(secondRead));
            Assert.True(scheduler.CanRun(write));
        }
    }

    [Fact, TestCategory("BVT")]
    public void AlwaysInterleaveRequests_DoNotBlockOtherRequests()
    {
        var synchronizationRoot = new object();
        var scheduler = new RequestScheduler(synchronizationRoot, new TestRequestSchedulerContext());
        var alwaysInterleave = CreateMessage(isAlwaysInterleave: true);
        var write = CreateMessage();

        lock (synchronizationRoot)
        {
            Start(scheduler, alwaysInterleave);

            Assert.True(scheduler.CanRun(write));
            Assert.Equal(1, scheduler.RunningCount);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("ReadOnly")]
    public void FastPathAdmissions_DoNotResolvePolicyComponents()
    {
        var synchronizationRoot = new object();
        var context = new TestRequestSchedulerContext();
        var scheduler = new RequestScheduler(synchronizationRoot, context);
        var runningRead = CreateMessage(isReadOnly: true);

        lock (synchronizationRoot)
        {
            Start(scheduler, runningRead);

            Assert.True(scheduler.CanRun(CreateMessage(isReadOnly: true)));
            Assert.True(scheduler.CanRun(CreateMessage(isAlwaysInterleave: true)));
            Assert.Equal(0, context.PolicyAccessCount);
        }
    }

    [Fact, TestCategory("BVT")]
    public void MayInterleave_RequiresCompatibilityWithEveryRunningRequest()
    {
        var synchronizationRoot = new object();
        var context = new TestRequestSchedulerContext();
        var scheduler = new RequestScheduler(synchronizationRoot, context);
        var canInterleave = new GrainCanInterleave();
        canInterleave.MayInterleavePredicates.Add(new MayInterleaveStaticPredicate(
            request => Assert.IsType<TestInvokable>(request).MayInterleave));
        context.CanInterleave = canInterleave;

        var interleavable = CreateMessage(mayInterleave: true);
        var blocking = CreateMessage(mayInterleave: false);
        var incoming = CreateMessage(mayInterleave: false);

        lock (synchronizationRoot)
        {
            Start(scheduler, interleavable);
            Assert.True(scheduler.CanRun(blocking));
            Start(scheduler, blocking);

            Assert.False(scheduler.CanRun(incoming));
            Assert.Same(blocking, scheduler.GetBlockingRequest(out _));

            Assert.True(scheduler.CompleteRequest(blocking));
            Assert.True(scheduler.CanRun(incoming));
        }
    }

    [Fact, TestCategory("BVT")]
    public void MatchingCallChainReentrancy_AllowsRequest()
    {
        var synchronizationRoot = new object();
        var tracker = new ReentrantRequestTracker();
        var scheduler = new RequestScheduler(
            synchronizationRoot,
            new TestRequestSchedulerContext { ReentrantRequestTracker = tracker });
        var reentrancyId = Guid.NewGuid();
        var running = CreateMessage();
        var incoming = CreateMessage(reentrancyId: reentrancyId);

        lock (synchronizationRoot)
        {
            Start(scheduler, running);
            tracker.EnterReentrantSection(reentrancyId);

            Assert.True(scheduler.CanRun(incoming));
        }
    }

    [Fact, TestCategory("BVT")]
    public void CancelWaitingRequest_RemovesOnlyTheWaitingRequest()
    {
        var synchronizationRoot = new object();
        var scheduler = new RequestScheduler(synchronizationRoot, new TestRequestSchedulerContext());
        var sender = GrainId.Create("request-scheduler-test", "sender");
        var running = CreateMessage();
        var waiting = CreateMessage();
        waiting.SendingGrain = sender;

        lock (synchronizationRoot)
        {
            Start(scheduler, running);
            scheduler.Enqueue(waiting);

            Assert.True(scheduler.TryFindRequest(sender, waiting.Id, out var found, out var wasWaiting));
            Assert.Same(waiting, found);
            Assert.True(wasWaiting);
            Assert.Equal(0, scheduler.WaitingCount);
            Assert.Equal(1, scheduler.RunningCount);
        }
    }

    [Fact, TestCategory("BVT")]
    public void DequeueAllWaitingRequests_ReturnsReroutableRequestsAndClearsQueue()
    {
        var synchronizationRoot = new object();
        var scheduler = new RequestScheduler(synchronizationRoot, new TestRequestSchedulerContext());
        var reroutable = CreateMessage();
        var localOnly = CreateMessage();
        localOnly.IsLocalOnly = true;

        lock (synchronizationRoot)
        {
            scheduler.Enqueue(reroutable);
            scheduler.Enqueue(localOnly);

            var requests = scheduler.DequeueAllWaitingRequests();

            Assert.Equal([reroutable], requests);
            Assert.Equal(0, scheduler.WaitingCount);
        }
    }

    private static void Start(RequestScheduler scheduler, Message message)
    {
        scheduler.Enqueue(message);
        Assert.Same(message, scheduler.StartRequest(scheduler.WaitingCount - 1));
    }

    private static Message CreateMessage(
        bool isReadOnly = false,
        bool isAlwaysInterleave = false,
        bool mayInterleave = false,
        Guid reentrancyId = default)
    {
        var message = new Message
        {
            BodyObject = new TestInvokable(mayInterleave),
            IsReadOnly = isReadOnly,
            IsAlwaysInterleave = isAlwaysInterleave,
        };

        if (reentrancyId != Guid.Empty)
        {
            message.RequestContextData = new()
            {
                [RequestContext.CALL_CHAIN_REENTRANCY_HEADER] = reentrancyId,
            };
        }

        return message;
    }

    private sealed class TestInvokable(bool mayInterleave) : IInvokable
    {
        private static readonly MethodInfo Method = typeof(TestInvokable).GetMethod(nameof(Invoke))!;

        public bool MayInterleave { get; } = mayInterleave;

        public object? GetTarget() => null;

        public void SetTarget(ITargetHolder holder)
        {
        }

        public ValueTask<Response> Invoke() => throw new NotSupportedException();

        public int GetArgumentCount() => 0;

        public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

        public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

        public string GetMethodName() => nameof(Invoke);

        public string GetInterfaceName() => typeof(TestInvokable).FullName!;

        public string GetActivityName() => $"{GetInterfaceName()}/{GetMethodName()}";

        public MethodInfo GetMethod() => Method;

        public Type GetInterfaceType() => typeof(TestInvokable);

        public void Dispose()
        {
        }
    }

    private sealed class TestRequestSchedulerContext : IRequestSchedulerContext
    {
        private object? _grainInstance;
        private GrainCanInterleave? _canInterleave;
        private ReentrantRequestTracker? _reentrantRequestTracker;

        public int PolicyAccessCount { get; private set; }

        public object? GrainInstance
        {
            get
            {
                ++PolicyAccessCount;
                return _grainInstance;
            }
            set => _grainInstance = value;
        }

        public GrainCanInterleave? CanInterleave
        {
            get
            {
                ++PolicyAccessCount;
                return _canInterleave;
            }
            set => _canInterleave = value;
        }

        public ReentrantRequestTracker? ReentrantRequestTracker
        {
            get
            {
                ++PolicyAccessCount;
                return _reentrantRequestTracker;
            }
            set => _reentrantRequestTracker = value;
        }
    }
}
