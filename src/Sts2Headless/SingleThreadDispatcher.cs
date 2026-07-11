namespace Sts2Headless;

/// <summary>
/// FIFO synchronization context owned by the engine thread. Post never invokes a
/// callback inline: continuations are only run when the owner explicitly drains
/// the queue, which prevents nested engine continuations from re-entering the
/// operation that scheduled them.
/// </summary>
internal sealed class SingleThreadDispatcher : SynchronizationContext, IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly AutoResetEvent _available = new(false);
    private int _ownerThreadId;
    private bool _disposed;

    public void BindToCurrentThread()
    {
        if (_ownerThreadId != 0 && _ownerThreadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("dispatcher is already bound to another thread");
        _ownerThreadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(this);
    }

    public bool HasPending
    {
        get { lock (_gate) return _queue.Count != 0; }
    }

    public override void Post(SendOrPostCallback callback, object? state)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queue.Enqueue((callback, state));
        }
        _available.Set();
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            callback(state);
            return;
        }

        Exception? error = null;
        using var completed = new ManualResetEventSlim(false);
        Post(
            value =>
            {
                try { callback(value); }
                catch (Exception ex) { error = ex; }
                finally { completed.Set(); }
            },
            state);
        completed.Wait();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public bool RunOne(int waitMilliseconds = 0)
    {
        EnsureOwner();
        (SendOrPostCallback Callback, object? State) item;
        lock (_gate)
        {
            if (_queue.Count != 0)
            {
                item = _queue.Dequeue();
                goto run;
            }
        }
        if (waitMilliseconds <= 0 || !_available.WaitOne(waitMilliseconds))
            return false;
        lock (_gate)
        {
            if (_queue.Count == 0) return false;
            item = _queue.Dequeue();
        }

    run:
        item.Callback(item.State);
        return true;
    }

    public int Drain()
    {
        var count = 0;
        while (RunOne()) count++;
        return count;
    }

    // Temporary compatibility name while the old scattered drain sites are
    // migrated to the single command-boundary quiescence driver.
    public int Pump() => Drain();

    private void EnsureOwner()
    {
        if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("only the engine thread may drain the dispatcher");
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _available.Dispose();
    }
}
