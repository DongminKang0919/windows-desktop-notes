using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Views;

namespace WindowsMemo.Services;

public sealed class AutosaveCoordinator : IDisposable
{
    private readonly NoteView _view;
    private readonly FileNoteRepository _repository;
    private readonly Func<NoteBounds> _bounds;
    private readonly DateTimeOffset _created;
    private readonly DispatcherTimer _timer;
    private Task<bool>? _saving;
    private long _version, _savedVersion;
    private DateTimeOffset _firstChange, _lastChange, _retryAfter;
    private bool _disposed;

    public AutosaveCoordinator(NoteView view, FileNoteRepository repository, Func<NoteBounds> bounds, DateTimeOffset created)
    {
        _view = view; _repository = repository; _bounds = bounds; _created = created;
        _view.DocumentChanged += OnChange;
        _view.SaveRequested += OnSave;
        _timer = new DispatcherTimer(DispatcherPriority.Background, view.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
        _timer.Start();
        _view.SetSaveStatus("저장 완료", false);
    }

    public void MarkChanged()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        if (_version == _savedVersion) _firstChange = now;
        _lastChange = now;
        _version++;
        _view.SetSaveStatus("저장 대기 중", false);
    }
    private void OnChange(object? sender, EventArgs e) => MarkChanged();
    private async void OnSave(object? sender, EventArgs e) => await FlushAsync();
    private async void OnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (_disposed || _saving is not null || _version == _savedVersion || now < _retryAfter) return;
        if (now - _lastChange >= TimeSpan.FromMilliseconds(500) || now - _firstChange >= TimeSpan.FromSeconds(5))
            await SaveAsync();
    }

    // Called on the UI dispatcher. The immutable snapshot is the only object sent to the worker.
    public async Task<bool> FlushAsync()
    {
        if (_disposed) return false;
        if (_saving is not null && !await _saving) return false;
        while (_version != _savedVersion) if (!await SaveAsync()) return false;
        return true;
    }
    private Task<bool> SaveAsync()
    {
        if (_saving is not null) return _saving;
        var task = SaveSnapshotAsync();
        _saving = task;
        return CompleteAsync(task);
    }
    private async Task<bool> CompleteAsync(Task<bool> task)
    {
        try { return await task; }
        finally { _saving = null; }
    }
    private async Task<bool> SaveSnapshotAsync()
    {
        long version = _version;
        try
        {
            _view.SetSaveStatus("저장 중…", false);
            var record = new NoteRecord(1, FileNoteRepository.NoteId, RtfDocumentCodec.Save(_view.TextEditor.Document),
                _bounds(), _created, DateTimeOffset.UtcNow);
            await Task.Run(() => _repository.Save(record));
            _savedVersion = version;
            _retryAfter = default;
            if (!_disposed) _view.SetSaveStatus(_version == version ? "저장 완료" : "저장 대기 중", false);
            return true;
        }
        catch (Exception ex) when (FileNoteRepository.IsStorageError(ex))
        {
            _retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
            if (!_disposed) _view.SetSaveStatus("저장 실패 · 내용을 유지하고 있습니다. 다시 시도하세요.", true);
            return false;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= OnTick;
        _view.DocumentChanged -= OnChange;
        _view.SaveRequested -= OnSave;
    }
}
