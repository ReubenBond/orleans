using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Runtime.CompilerServices;

/// <summary>
/// Awaits a <see cref="ValueTask"/> through <see cref="ControlledValueTaskAwaiter"/>, so an async method in Clockwork
/// itself, which the rewriter never sees, resumes on the simulation coordinator as rewritten code does.
/// </summary>
/// <param name="valueTask">The value task to await.</param>
internal readonly struct CoordinatedValueTaskAwaitable(ValueTask valueTask)
{
    public ControlledValueTaskAwaiter GetAwaiter() => new(valueTask);
}

/// <summary>
/// Awaits a <see cref="ValueTask{TResult}"/> through <see cref="ControlledValueTaskAwaiter{TResult}"/>, so an async
/// method in Clockwork itself, which the rewriter never sees, resumes on the simulation coordinator as rewritten code does.
/// </summary>
/// <typeparam name="TResult">The value task's result type.</typeparam>
/// <param name="valueTask">The value task to await.</param>
internal readonly struct CoordinatedValueTaskAwaitable<TResult>(ValueTask<TResult> valueTask)
{
    public ControlledValueTaskAwaiter<TResult> GetAwaiter() => new(valueTask);
}
