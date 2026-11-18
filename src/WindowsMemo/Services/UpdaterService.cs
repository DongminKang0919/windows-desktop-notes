namespace WindowsMemo.Services;

internal interface IUpdateBackend
{
    Task<string?> PrepareAsync(CancellationToken cancellationToken);
    void Apply();
}

internal enum UpdateState { Idle, Checking, Ready, Applying, Failed }

// Owned by the tray and called on the WPF dispatcher. Preparation never restarts the app.
internal sealed class UpdaterService(IUpdateBackend backend) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    public UpdateState State { get; private set; }
    public string? Version { get; private set; }
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Applying;

    public async Task CheckAsync()
    {
        if (_disposed || IsBusy || State == UpdateState.Ready) return;
        State = UpdateState.Checking;
        try
        {
            var version = await backend.PrepareAsync(_lifetime.Token);
            if (_disposed) return;
            Version = version;
            State = version is null ? UpdateState.Idle : UpdateState.Ready;
        }
        catch (Exception)
        {
            // External feed/package errors must leave the running editor available.
            if (!_disposed) { Version = null; State = UpdateState.Failed; }
        }
    }

    public async Task<bool> ApplyAsync(Func<Action, Task<bool>> saveThenApply)
    {
        if (_disposed || State != UpdateState.Ready) return false;
        State = UpdateState.Applying;
        try
        {
            bool applied = await saveThenApply(() =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                backend.Apply();
            });
            if (!_disposed) State = UpdateState.Ready;
            return applied;
        }
        catch (Exception)
        {
            if (!_disposed) State = UpdateState.Ready; // allow a manual retry
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
