namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Holds a call the code under test makes until the test answers it: the call records its request and returns a task
/// which completes with the test's answer. It lives in the kit, which is rewritten with the code under test, so its
/// completions run on the simulation's scheduler.
/// </summary>
/// <typeparam name="TRequest">What the call submits.</typeparam>
/// <typeparam name="TAnswer">What the call answers.</typeparam>
public sealed class HeldCall<TRequest, TAnswer>
{
    private readonly TaskCompletionSource<TRequest> _submitted = new();
    private readonly TaskCompletionSource<TAnswer> _answer = new();

    /// <summary>
    /// Gets a task which completes with the request once the call is made.
    /// </summary>
    public Task<TRequest> Submitted => _submitted.Task;

    /// <summary>
    /// Waits until the call is made, or <paramref name="caller"/>, the work which would make it, completes without it.
    /// </summary>
    /// <returns>Whether the call was made.</returns>
    public async Task<bool> WaitAsync(Task caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return await Task.WhenAny(_submitted.Task, caller).ConfigureAwait(false) == _submitted.Task;
    }

    /// <summary>
    /// Makes the call: records <paramref name="request"/> and returns the answer the test gives.
    /// </summary>
    public Task<TAnswer> CallAsync(TRequest request)
    {
        _ = _submitted.TrySetResult(request);
        return _answer.Task;
    }

    /// <summary>
    /// Answers the call with <paramref name="answer"/>'s outcome, once it completes.
    /// </summary>
    public async Task AnswerAsync(Task<TAnswer> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        try
        {
            _ = _answer.TrySetResult(await answer.ConfigureAwait(false));
        }
#pragma warning disable CA1031 // The answer's failure is the held call's answer.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _ = _answer.TrySetException(exception);
        }
    }
}
