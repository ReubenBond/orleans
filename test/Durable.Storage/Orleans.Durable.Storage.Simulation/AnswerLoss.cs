namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// Loses the answer of a call which completed, as a reply lost on its way back does: the call's effect stands, and its
/// caller sees only an exception, its outcome unknown. A test's callback which the code under test awaits schedules this
/// on the simulation's scheduler, since the kit is rewritten with the code under test.
/// </summary>
public static class AnswerLoss
{
    /// <summary>
    /// Completes with <paramref name="call"/>'s answer, or, when <paramref name="lose"/>, with an
    /// <see cref="IOException"/> once the call completed.
    /// </summary>
    public static async Task<T> AfterCompletionAsync<T>(Task<T> call, bool lose)
    {
        ArgumentNullException.ThrowIfNull(call);
        var answer = await call.ConfigureAwait(false);
        return lose ? throw new IOException("The answer was lost after the call completed.") : answer;
    }
}
