using WindowsMemo.Infrastructure;
using WindowsMemo.Models;

namespace WindowsMemo.Services;

internal sealed class SettingsService
{
    private readonly FileSettingsStore _store;
    internal AppSettings Current { get; private set; } = new();
    internal bool LoadFailed { get; private set; }
    internal SettingsService(string root)
    {
        _store = new(root);
        try { Current = _store.Load(); }
        catch (Exception ex) when (FileNoteRepository.IsStorageError(ex)) { LoadFailed = true; }
    }
    internal async Task SaveAsync(AppSettings settings)
    {
        await Task.Run(() => _store.Save(settings)); // Publish only after the atomic write succeeds.
        Current = settings;
        LoadFailed = false;
    }
}
