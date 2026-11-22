namespace Orleans.Testing.Simulation.Runtime.Tasks;

internal sealed class SimulationDelayPromise
{
    private const string Api = "System.Threading.Tasks.Task.Delay";
    private readonly TaskCompletionSource _completion = new();
    private readonly TimeSpan _delay;
    private readonly CancellationToken _cancellationToken;
    private ISimulationTimer? _deadline;
    private CancellationTokenRegistration _cancellationRegistration;
    private int _winner;

    public SimulationDelayPromise(TimeSpan delay, CancellationToken cancellationToken)
    {
        _delay = delay;
        _cancellationToken = cancellationToken;
        Initialize();
    }

    public Task Task => _completion.Task;

    private void Initialize()
    {
        if (_delay != Timeout.InfiniteTimeSpan)
        {
            _deadline = SimulationTaskRuntime.RegisterTimeout(
                _delay,
                CompleteFromDeadline,
                Api);
        }

        if (_cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = _cancellationToken.UnsafeRegister(
                static (state, token) => ((SimulationDelayPromise)state!).CompleteFromCancellation(token),
                this);
            if (Volatile.Read(ref _winner) != 0)
            {
                _cancellationRegistration.Unregister();
            }
        }
    }

    private void CompleteFromDeadline()
    {
        if (Interlocked.CompareExchange(ref _winner, 1, 0) != 0)
        {
            return;
        }

        _cancellationRegistration.Unregister();
        SimulationCompletion.TrySetResult(_completion, Api);
    }

    private void CompleteFromCancellation(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _winner, 2, 0) != 0)
        {
            return;
        }

        _deadline?.Cancel();
        SimulationCompletion.TrySetCanceled(_completion, token, Api);
    }
}

internal sealed class SimulationTaskCompletionRace
{
    private const string Api = "System.Threading.Tasks.Task.WaitAsync";
    private readonly Task _task;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _cancellationToken;
    private readonly Action _taskWinner;
    private readonly Action _timeoutWinner;
    private readonly Action _cancellationWinner;
    private ISimulationWorkRegistration? _taskRegistration;
    private ISimulationTimer? _deadline;
    private CancellationTokenRegistration _cancellationRegistration;
    private int _winner;

    public SimulationTaskCompletionRace(
        Task task,
        TimeSpan timeout,
        Action taskWinner,
        Action timeoutWinner,
        Action cancellationWinner,
        CancellationToken cancellationToken)
    {
        _task = task;
        _timeout = timeout;
        _cancellationToken = cancellationToken;
        _taskWinner = taskWinner;
        _timeoutWinner = timeoutWinner;
        _cancellationWinner = cancellationWinner;
    }

    public void Start()
    {
        _taskRegistration = SimulationTaskRuntime.ScheduleCancelableContinuation(
            _task,
            CompleteFromTask,
            Api);

        if (_timeout != Timeout.InfiniteTimeSpan)
        {
            _deadline = SimulationTaskRuntime.RegisterTimeout(_timeout, CompleteFromTimeout, Api);
        }

        if (_cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = _cancellationToken.UnsafeRegister(
                static state => ((SimulationTaskCompletionRace)state!).CompleteFromCancellation(),
                this);
            if (Volatile.Read(ref _winner) != 0)
            {
                _cancellationRegistration.Unregister();
            }
        }
    }

    private void CompleteFromTask() => Complete(1, _taskWinner);

    private void CompleteFromTimeout() => Complete(2, _timeoutWinner);

    private void CompleteFromCancellation() => Complete(3, _cancellationWinner);

    private void Complete(int winner, Action completion)
    {
        if (Interlocked.CompareExchange(ref _winner, winner, 0) != 0)
        {
            return;
        }

        if (winner != 1)
        {
            _taskRegistration?.Cancel();
        }

        if (winner != 2)
        {
            _deadline?.Cancel();
        }

        if (winner != 3)
        {
            _cancellationRegistration.Unregister();
        }

        completion();
    }
}
