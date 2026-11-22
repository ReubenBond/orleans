using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.IO;

/// <summary>
/// The controlled stand-in for <c>Task.Factory.StartNew(body, state, cancellationToken,
/// TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)</c>, through which the base asynchronous members of
/// <see cref="global::System.IO.Stream"/>, <see cref="global::System.IO.TextReader"/>, and
/// <see cref="global::System.IO.TextWriter"/> run their synchronous counterparts on a thread-pool thread. The body runs
/// as a fresh controlled operation, in the caller's execution context, concurrently with the caller as on the thread
/// pool. The task completes as the BCL's does: canceled, without running the body, when the token is canceled by the
/// time the operation starts (the BCL cannot remove a queued work item from the thread pool's global queue, so it
/// cancels the task when a pool thread reaches it); canceled when the body throws an
/// <see cref="OperationCanceledException"/> for that canceled token; faulted for any other exception; and otherwise with
/// the body's result.
/// </summary>
internal static class IoWorker
{
    /// <summary>Starts <paramref name="body"/> as a controlled operation.</summary>
    /// <typeparam name="TResult">The body's result type.</typeparam>
    /// <param name="body">The synchronous work.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    /// <param name="cancellationToken">The token which cancels the work before it starts.</param>
    /// <returns>The task which the work completes.</returns>
    public static Task<TResult> Start<TResult>(Func<TResult> body, string apiName, CancellationToken cancellationToken)
    {
        SimulationExecutionSnapshot snapshot = SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        var completion = new TaskCompletionSource<TResult>();
        SimulationTaskRuntime.QueueCapturedWork(
            snapshot,
            ExecutionContext.Capture(),
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    SimulationCompletion.TrySetCanceled(completion, cancellationToken, apiName);
                }
                else
                {
                    try
                    {
                        TResult result = body();
                        SimulationCompletion.TrySetResult(completion, result, apiName);
                    }
                    catch (OperationCanceledException exception)
                        when (cancellationToken.IsCancellationRequested && exception.CancellationToken == cancellationToken)
                    {
                        SimulationCompletion.TrySetCanceled(completion, cancellationToken, apiName);
                    }
                    catch (Exception exception)
                    {
                        SimulationCompletion.TrySetException(completion, exception, apiName);
                    }
                }

                RaceSynchronization.Signal(completion.Task);
            },
            apiName);
        return completion.Task;
    }

    /// <summary>Starts <paramref name="body"/> as a controlled operation; see <see cref="Start{TResult}"/>.</summary>
    /// <param name="body">The synchronous work.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    /// <param name="cancellationToken">The token which cancels the work before it starts.</param>
    /// <returns>The task which the work completes.</returns>
    public static Task Start(Action body, string apiName, CancellationToken cancellationToken) =>
        Start(
            () =>
            {
                body();
                return true;
            },
            apiName,
            cancellationToken);
}
