namespace WindowsMemo.Services;

internal enum SaveState { Saved, Dirty, Saving, Failed, Disposed }
internal enum SessionEndState { Saved, Dirty, Saving, Failed, Preparing, Busy, Disposed }
internal enum SessionPreparationResult { Ready, Failed, Busy }
internal readonly record struct SessionEndDecision(SessionEndState State)
{
    internal bool Cancel => State != SessionEndState.Saved;
}

// UI-dispatcher owned. A cancelled OS query is never an instruction to quit the app.
internal sealed class SessionEndingCoordinator(
    Func<SessionEndState> state, Func<Task<bool>> flush, Action<Action> enqueue,
    Action<SessionPreparationResult> notify) : IDisposable
{
    private bool _pending, _disposed;
    internal bool IsPending => _pending;
    internal Task Preparation { get; private set; } = Task.CompletedTask;
    internal SessionEndDecision Decide() => new(_disposed ? SessionEndState.Disposed : state());

    // Called only after e.Cancel has been set. Production enqueue posts to the dispatcher,
    // so no serialization, I/O or modal UI occurs inside the synchronous OS query.
    internal void PrepareAfterCancellation()
    {
        if (_disposed || _pending) return;
        _pending = true;
        try { enqueue(() => Preparation = PrepareAsync()); }
        catch (Exception) { _pending = false; } // Dispatcher rejection must not approve or fault the OS query.
    }
    private async Task PrepareAsync()
    {
        try
        {
            if (_disposed) return;
            SessionPreparationResult result;
            try
            {
                var current = state();
                result = current switch
                {
                    SessionEndState.Saved => SessionPreparationResult.Ready,
                    SessionEndState.Preparing or SessionEndState.Busy or SessionEndState.Disposed => SessionPreparationResult.Busy,
                    _ => await flush() && state() == SessionEndState.Saved
                        ? SessionPreparationResult.Ready : SessionPreparationResult.Failed
                };
            }
            // Detached preparation observes failures and never approves an unsafe exit.
            catch (Exception) { result = SessionPreparationResult.Failed; }
            if (!_disposed)
            {
                try { notify(result); }
                catch (Exception) { } // A best-effort tray notice cannot turn preparation into an unobserved fault.
            }
        }
        finally { _pending = false; }
    }
    public void Dispose() => _disposed = true;
}
