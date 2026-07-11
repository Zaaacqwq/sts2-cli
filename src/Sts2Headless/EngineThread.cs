using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Sts2Headless;

/// <summary>
/// A single dedicated thread that owns all game-engine state. Every command is
/// marshalled here and executed to completion before the next one starts, so the
/// engine's async continuations always run on exactly one thread.
///
/// RunSimulator installs a non-reentrant FIFO synchronization context and drives
/// command boundaries by quiescence. Synchronous external-selection APIs use a
/// tracked blocking bridge because their interface cannot suspend asynchronously.
/// See docs/SINGLE_THREAD_DRIVER.md.
/// </summary>
internal sealed class EngineThread : IDisposable
{
    private sealed class WorkItem
    {
        public required Func<object?> Work;
        public object? Result;
        public Exception? Error;
        public readonly ManualResetEventSlim Done = new(false);
    }

    private readonly BlockingCollection<WorkItem> _inbox = new();
    private readonly Thread _thread;

    public EngineThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "sts2-engine" };
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var item in _inbox.GetConsumingEnumerable())
        {
            try { item.Result = item.Work(); }
            catch (Exception ex) { item.Error = ex; }
            finally { item.Done.Set(); }
        }
    }

    /// <summary>Run <paramref name="work"/> on the engine thread and block until it returns.
    /// Exceptions are rethrown on the caller with their original stack trace.</summary>
    public T Invoke<T>(Func<T> work)
    {
        var item = new WorkItem { Work = () => work() };
        _inbox.Add(item);
        item.Done.Wait();
        if (item.Error != null)
            ExceptionDispatchInfo.Capture(item.Error).Throw();
        return (T)item.Result!;
    }

    public void Dispose()
    {
        _inbox.CompleteAdding();
        _thread.Join(2000);
        _inbox.Dispose();
    }
}
