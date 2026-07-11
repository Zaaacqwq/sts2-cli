namespace Sts2Headless;

/// <summary>
/// Drives the engine-owned continuation queue to a stable command boundary.
/// Completion is based on observable work (FIFO callbacks and external-input
/// prompts), never ActionExecutor.IsRunning, whose counter can remain latched.
/// </summary>
internal sealed class Quiescence
{
    private readonly SingleThreadDispatcher _dispatcher;
    private readonly Func<bool> _externalInputPending;

    public Quiescence(SingleThreadDispatcher dispatcher, Func<bool> externalInputPending)
    {
        _dispatcher = dispatcher;
        _externalInputPending = externalInputPending;
    }

    public void DrainUntilQuiet(int budgetMilliseconds = 1000, int quietMilliseconds = 5)
    {
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var lastWork = budget.ElapsedMilliseconds;
        while (budget.ElapsedMilliseconds < budgetMilliseconds)
        {
            if (_externalInputPending()) return;
            if (_dispatcher.RunOne(1))
            {
                lastWork = budget.ElapsedMilliseconds;
                continue;
            }
            if (!_dispatcher.HasPending && budget.ElapsedMilliseconds - lastWork >= quietMilliseconds)
                return;
        }
    }

    /// <summary>
    /// Transitional bridge for deep synchronous call sites. It drains at this one
    /// helper rather than blocking the engine thread on a continuation that must run
    /// on that same thread. False means the operation suspended for external input.
    /// </summary>
    public bool RunInline(Task task, int budgetMilliseconds = 2000)
    {
        var budget = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && budget.ElapsedMilliseconds < budgetMilliseconds)
        {
            if (_externalInputPending()) return false;
            _dispatcher.RunOne(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("engine task did not reach quiescence");
        task.GetAwaiter().GetResult();
        return true;
    }
}
