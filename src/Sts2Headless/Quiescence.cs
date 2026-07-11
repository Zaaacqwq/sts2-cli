namespace Sts2Headless;

/// <summary>
/// Drives the engine-owned continuation queue to a stable command boundary.
/// A missed boundary poisons the dispatcher: returning a half-applied state is
/// never a valid recovery strategy for an RL environment.
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
        try
        {
            while (budget.ElapsedMilliseconds < budgetMilliseconds)
            {
                if (_externalInputPending()) return;
                if (_dispatcher.RunOne(1))
                {
                    lastWork = budget.ElapsedMilliseconds;
                    continue;
                }
                if (!_dispatcher.HasPending &&
                    budget.ElapsedMilliseconds - lastWork >= quietMilliseconds)
                    return;
            }
        }
        catch (EngineFatalException) { throw; }
        catch (Exception ex)
        {
            throw _dispatcher.Poison(
                "dispatcher_callback_fault",
                $"engine continuation failed: {ex.GetType().Name}: {ex.Message}",
                ex);
        }

        throw _dispatcher.Poison(
            "quiescence_timeout",
            $"engine did not reach quiescence within {budgetMilliseconds}ms " +
            $"(pending={_dispatcher.HasPending}, external_input={_externalInputPending()})");
    }

    /// <summary>
    /// Transitional bridge for deep synchronous call sites. It drains FIFO work
    /// while waiting and returns false only when the operation has deliberately
    /// suspended for external input.
    /// </summary>
    public bool RunInline(Task task, int budgetMilliseconds = 2000)
    {
        var budget = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!task.IsCompleted && budget.ElapsedMilliseconds < budgetMilliseconds)
            {
                if (_externalInputPending()) return false;
                _dispatcher.RunOne(1);
            }
            if (!task.IsCompleted)
                throw _dispatcher.Poison(
                    "engine_task_timeout",
                    $"engine task did not complete within {budgetMilliseconds}ms");
            task.GetAwaiter().GetResult();
            return true;
        }
        catch (EngineFatalException) { throw; }
        catch (Exception ex)
        {
            throw _dispatcher.Poison(
                "engine_task_fault",
                $"engine task failed: {ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }
}
