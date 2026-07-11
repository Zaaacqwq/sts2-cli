namespace Sts2Headless;

internal static class DispatcherSelfTest
{
    public static void Run()
    {
        QuiescenceTimeoutPoisonsAndCancelsQueue();
        SendTimeoutPoisonsAndCancelsQueue();
        Console.WriteLine("dispatcher_self_test=PASS");
    }

    private static void QuiescenceTimeoutPoisonsAndCancelsQueue()
    {
        using var dispatcher = new SingleThreadDispatcher();
        dispatcher.BindToCurrentThread();
        var callbacks = 0;
        SendOrPostCallback? spin = null;
        spin = _ =>
        {
            callbacks++;
            dispatcher.Post(spin!, null);
        };
        dispatcher.Post(spin, null);

        var quiescence = new Quiescence(dispatcher, () => false);
        ExpectFatal("quiescence_timeout", () => quiescence.DrainUntilQuiet(20, 1));
        var stoppedAt = callbacks;
        ExpectFatal("quiescence_timeout", () => dispatcher.RunOne());
        if (callbacks != stoppedAt || dispatcher.HasPending)
            throw new Exception("poisoned dispatcher executed or retained queued work");
    }

    private static void SendTimeoutPoisonsAndCancelsQueue()
    {
        using var dispatcher = new SingleThreadDispatcher(25);
        dispatcher.BindToCurrentThread();
        var callbackRan = false;
        var sender = Task.Run(() =>
            ExpectFatal("dispatcher_send_timeout", () =>
                dispatcher.Send(_ => callbackRan = true, null)));
        if (!sender.Wait(1000)) throw new Exception("Send timeout self-test hung");
        if (callbackRan || dispatcher.HasPending)
            throw new Exception("timed-out Send callback was not cancelled");
        ExpectFatal("dispatcher_send_timeout", () => dispatcher.Post(_ => { }, null));
    }

    private static void ExpectFatal(string code, Action action)
    {
        try { action(); }
        catch (EngineFatalException ex) when (ex.Code == code) { return; }
        throw new Exception($"expected EngineFatalException with code {code}");
    }
}
