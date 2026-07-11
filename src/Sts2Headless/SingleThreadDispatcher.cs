namespace Sts2Headless;

/// <summary>
/// FIFO synchronization context owned by the engine thread. Post never invokes a
/// callback inline. Once poisoned, queued work is cancelled and no callback may
/// start; the CLI must terminate so the Python worker can create a fresh process.
/// </summary>
internal sealed class SingleThreadDispatcher : SynchronizationContext, IDisposable
{
    private const int DefaultSendTimeoutMilliseconds = 5000;

    private sealed class WorkItem
    {
        private int _state; // 0 queued, 1 running, 2 completed, 3 cancelled
        public required SendOrPostCallback Callback;
        public object? State;
        public Action? OnCancelled;

        public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public void Complete() => Interlocked.Exchange(ref _state, 2);

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 3, 0) == 0)
                OnCancelled?.Invoke();
        }
    }

    private readonly object _gate = new();
    private readonly Queue<WorkItem> _queue = new();
    private readonly AutoResetEvent _available = new(false);
    private readonly int _sendTimeoutMilliseconds;
    private volatile int _ownerThreadId;
    private EngineFatalException? _poison;
    private bool _disposed;

    public SingleThreadDispatcher(int sendTimeoutMilliseconds = DefaultSendTimeoutMilliseconds)
    {
        if (sendTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sendTimeoutMilliseconds));
        _sendTimeoutMilliseconds = sendTimeoutMilliseconds;
    }

    public void BindToCurrentThread()
    {
        ThrowIfPoisoned();
        if (_ownerThreadId != 0 && _ownerThreadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("dispatcher is already bound to another thread");
        _ownerThreadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(this);
    }

    public bool HasPending
    {
        get { lock (_gate) return _queue.Count != 0; }
    }

    public bool IsPoisoned
    {
        get { lock (_gate) return _poison != null; }
    }

    public EngineFatalException Poison(string code, string message, Exception? inner = null)
    {
        List<WorkItem> cancelled;
        EngineFatalException fatal;
        bool signal;
        lock (_gate)
        {
            fatal = _poison ??= new EngineFatalException(code, message, inner);
            cancelled = _queue.ToList();
            _queue.Clear();
            signal = !_disposed;
        }
        foreach (var item in cancelled) item.Cancel();
        if (signal) _available.Set();
        return fatal;
    }

    public void ThrowIfPoisoned()
    {
        EngineFatalException? fatal;
        lock (_gate) fatal = _poison;
        if (fatal != null) throw fatal;
    }

    public override void Post(SendOrPostCallback callback, object? state)
    {
        Enqueue(new WorkItem { Callback = callback, State = state });
    }

    private void Enqueue(WorkItem item)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_poison != null) throw _poison;
            _queue.Enqueue(item);
        }
        _available.Set();
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        ThrowIfPoisoned();
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            try { callback(state); }
            catch (EngineFatalException) { throw; }
            catch (Exception ex)
            {
                throw Poison(
                    "dispatcher_send_fault",
                    $"engine-thread Send callback failed: {ex.GetType().Name}: {ex.Message}",
                    ex);
            }
            return;
        }

        Exception? error = null;
        var completed = new ManualResetEventSlim(false);
        var item = new WorkItem
        {
            Callback = value =>
            {
                try { callback(value); }
                catch (Exception ex) { error = ex; }
                finally { completed.Set(); }
            },
            State = state,
            OnCancelled = completed.Set,
        };
        Enqueue(item);

        if (!completed.Wait(_sendTimeoutMilliseconds))
        {
            // Do not dispose the event on this fatal path: a callback that started just
            // before the deadline may still signal it. The poisoned process is about to
            // terminate, so retaining this small object is safer than a late Set() race.
            throw Poison(
                "dispatcher_send_timeout",
                $"engine thread did not run a posted Send within {_sendTimeoutMilliseconds}ms");
        }

        completed.Dispose();
        ThrowIfPoisoned();
        if (error != null)
            throw Poison(
                "dispatcher_send_fault",
                $"posted Send callback failed: {error.GetType().Name}: {error.Message}",
                error);
    }

    public bool RunOne(int waitMilliseconds = 0)
    {
        EnsureOwner();
        WorkItem? item = null;
        while (item == null)
        {
            lock (_gate)
            {
                if (_poison != null) throw _poison;
                if (_queue.Count != 0) item = _queue.Dequeue();
            }
            if (item != null) break;
            if (waitMilliseconds <= 0 || !_available.WaitOne(waitMilliseconds)) return false;
            waitMilliseconds = 0;
        }

        if (!item.TryStart()) return false;
        ThrowIfPoisoned();
        try { item.Callback(item.State); }
        catch (EngineFatalException) { throw; }
        catch (Exception ex)
        {
            throw Poison(
                "dispatcher_callback_fault",
                $"engine continuation failed: {ex.GetType().Name}: {ex.Message}",
                ex);
        }
        finally { item.Complete(); }
        return true;
    }

    public int Drain()
    {
        var count = 0;
        while (RunOne()) count++;
        return count;
    }

    public int Pump() => Drain();

    private void EnsureOwner()
    {
        ThrowIfPoisoned();
        if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("only the engine thread may drain the dispatcher");
    }

    public void Dispose()
    {
        List<WorkItem> cancelled;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _poison ??= new EngineFatalException(
                "dispatcher_disposed",
                "dispatcher was disposed before queued work completed");
            cancelled = _queue.ToList();
            _queue.Clear();
        }
        foreach (var item in cancelled) item.Cancel();
        _available.Set();
        _available.Dispose();
    }
}
