using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Views;

namespace WindowsMemo.Services;

// Owns one visible editor, surface and autosave timer. Hidden notes own none of these.
internal sealed class NoteSession : IDisposable
{
    private readonly INoteSurface _surface;
    private readonly AutosaveCoordinator _saves;
    private DateTimeOffset? _deleted;
    private bool _hidden, _disposed, _transitioning;
    public NoteView View { get; }
    public Guid Id { get; }

    public NoteSession(FileNoteRepository repository, NoteRecord? record,
        Func<NoteView, NoteBounds?, INoteSurface> createSurface)
    {
        Id = repository.Id;
        View = new NoteView();
        try
        {
            if (record is not null) View.TextEditor.Document = RtfDocumentCodec.Load(record.Rtf);
            _surface = createSurface(View, record?.Bounds);
            _saves = new AutosaveCoordinator(View, repository, _surface.CaptureBounds,
                record?.CreatedAtUtc ?? DateTimeOffset.UtcNow, () => _hidden, () => _deleted);
            _surface.BoundsChanged += OnBounds;
            // New/visible/fitted state must be persisted even when only the visibility changed.
            _saves.MarkChanged();
        }
        catch { _surface?.Dispose(); View.Dispose(); throw; }
    }
    private void OnBounds(object? sender, EventArgs e) => _saves.MarkChanged();
    public void SetEnabled(bool enabled) => View.IsEnabled = enabled;
    public Task<bool> FlushAsync() => _saves.FlushAsync();

    public Task<bool> HideAsync() => CloseAsync(false);
    public Task<bool> TrashAsync() => CloseAsync(true);
    private async Task<bool> CloseAsync(bool trash)
    {
        if (_disposed || _transitioning) return false;
        _transitioning = true;
        View.IsEnabled = false;
        _hidden = true;
        _deleted = trash ? DateTimeOffset.UtcNow : null;
        _saves.MarkChanged();
        try
        {
            if (await _saves.FlushAsync()) { Dispose(); return true; }
            // Keep the newest editor and surface when the hidden snapshot did not reach disk.
            _hidden = false;
            _deleted = null;
            _saves.MarkChanged();
            View.SetSaveStatus("저장하지 못했습니다. 내용을 유지하고 있습니다. 다시 시도하세요.", true);
            return false;
        }
        finally
        {
            _transitioning = false;
            if (!_disposed) View.IsEnabled = true;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _surface.BoundsChanged -= OnBounds;
        _saves.Dispose();
        _surface.Dispose();
        View.Dispose();
    }
}
