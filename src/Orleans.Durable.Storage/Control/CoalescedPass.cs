
namespace Orleans.Durable.Storage.Control;

/// <summary>
/// Runs an action once, in a turn of its own on the caller's scheduler, however many requests arrive before it runs: the
/// controller grain's pass after inputs made one due (orleans.md §5.6). Its one owner calls it from one logical thread, a
/// grain's turns.
/// </summary>
/// <param name="run">The action, which the owner's turn runs.</param>
internal sealed class CoalescedPass(Action run)
{
    private bool _scheduled;

    /// <summary>Gets how many times the action ran; for tests.</summary>
    internal int Runs { get; private set; }

    /// <summary>Asks for one run; a request while one is scheduled is the same request.</summary>
    public void Request()
    {
        if (!_scheduled)
        {
            _scheduled = true;
            _ = RunAsync();
        }
    }

    private async Task RunAsync()
    {
        // Task.Yield continues on the current scheduler, the owner's, after the requests of this turn.
        await Task.Yield();
        _scheduled = false;
        Runs++;
        run();
    }
}
