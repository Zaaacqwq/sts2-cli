namespace Sts2Headless;

/// <summary>
/// The engine can no longer guarantee a stable state. The current process must
/// stop serving commands; recovery happens by starting a fresh worker process.
/// </summary>
internal sealed class EngineFatalException : Exception
{
    public string Code { get; }

    public EngineFatalException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}
