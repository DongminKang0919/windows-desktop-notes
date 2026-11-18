namespace WindowsMemo.Services;

// A second launch asks the existing process to open its list without touching note data.
internal sealed class InstanceActivation : IDisposable
{
    private readonly EventWaitHandle _request;
    private readonly object _gate = new();
    private RegisteredWaitHandle? _listener;
    private bool _disposed;

    internal InstanceActivation(string name = @"Local\WindowsMemo.Prototype.Activate")
        => _request = new EventWaitHandle(false, EventResetMode.AutoReset, name);

    internal void Signal() => _request.Set();

    internal void Listen(Action activate)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) throw new InvalidOperationException("이미 실행 요청을 수신 중입니다.");
            _listener = ThreadPool.RegisterWaitForSingleObject(_request, (_, _) =>
            {
                lock (_gate) { if (!_disposed) activate(); }
            }, null, Timeout.Infinite, false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _listener?.Unregister(null);
            _request.Dispose();
        }
    }
}
