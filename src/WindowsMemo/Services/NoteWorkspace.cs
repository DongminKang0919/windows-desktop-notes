using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Views;

namespace WindowsMemo.Services;

internal sealed record NoteListItem(Guid Id, bool Hidden, string? Error, bool Trashed = false)
{
    public string Label => $"메모 {Id.ToString("N")[..8]} · {(Error is not null ? "처리 실패" : Trashed ? "휴지통" : Hidden ? "숨김" : "표시 중")}";
}

internal sealed class NoteWorkspace : IDisposable
{
    private sealed class Entry(FileNoteRepository repository)
    {
        public FileNoteRepository Repository { get; } = repository;
        public bool Hidden { get; set; }
        public bool Trashed { get; set; }
        public string? Error { get; set; }
        public NoteSession? Session { get; set; }
    }
    private readonly string _root;
    private readonly Dispatcher _dispatcher;
    private readonly Func<NoteView, NoteBounds?, INoteSurface> _createSurface;
    private readonly Dictionary<Guid, Entry> _entries = new();
    private bool _busy, _disposed;
    public bool IsBusy => _busy || _disposed;
    public event EventHandler? Changed;
    public event EventHandler? ListRequested;
    public event EventHandler<string>? Notice;
    public IReadOnlyList<NoteListItem> Items => List(false);
    public IReadOnlyList<NoteListItem> TrashItems => List(true);
    private IReadOnlyList<NoteListItem> List(bool trashed) => _entries.Where(pair => pair.Value.Trashed == trashed)
        .Select(pair => new NoteListItem(pair.Key, pair.Value.Hidden, pair.Value.Error, pair.Value.Trashed)).ToArray();

    public NoteWorkspace(string root, Func<NoteView, NoteBounds?, INoteSurface> createSurface)
    { _root = root; _createSurface = createSurface; _dispatcher = Dispatcher.CurrentDispatcher; }

    private async Task<(NoteRecord? Record, bool Recovered)> LoadAsync(FileNoteRepository repository)
    {
        // File access/decryption stay off the UI thread; WPF content validation stays on it.
        return await Task.Run(() =>
        {
            var record = repository.Load(value => _dispatcher.Invoke(() => RtfDocumentCodec.Load(value.Rtf)), out bool recovered);
            return (record, recovered);
        });
    }
    internal static bool IsNoteError(Exception ex) => FileNoteRepository.IsStorageError(ex)
        || ex is InvalidOperationException or System.ComponentModel.Win32Exception;
    private void Publish() => Changed?.Invoke(this, EventArgs.Empty);

    public async Task InitializeAsync(Func<Task>? prepare = null)
    {
        if (IsBusy) return;
        _busy = true; Publish();
        try
        {
            if (prepare is not null) await prepare();
            var ids = await Task.Run(() => FileNoteRepository.DiscoverIds(_root));
            foreach (Guid id in ids)
            {
                var entry = new Entry(new FileNoteRepository(_root, id));
                _entries.Add(id, entry);
                try
                {
                    var loaded = await LoadAsync(entry.Repository);
                    if (loaded.Record is null) throw new System.IO.InvalidDataException("메모 파일이 없어졌습니다.");
                    entry.Hidden = loaded.Record.Hidden;
                    entry.Trashed = loaded.Record.DeletedAtUtc is not null;
                    if (!entry.Hidden && !entry.Trashed) Open(entry, loaded.Record);
                    if (loaded.Recovered) Notice?.Invoke(this, "이전 정상 백업에서 메모를 복원했습니다. 최근 변경 일부가 없을 수 있습니다. 원본은 보존됩니다.");
                }
                catch (Exception ex) when (IsNoteError(ex)) { entry.Error = "복원하지 못했습니다. 파일은 보존됩니다."; }
            }
            if (_entries.Count == 0) CreateCore(Guid.NewGuid());
        }
        finally { _busy = false; Publish(); }
    }
    private void Open(Entry entry, NoteRecord? record)
    {
        var offset = (_entries.Count % 10) * 28;
        var session = new NoteSession(entry.Repository, record, (view, bounds) =>
            _createSurface(view, bounds ?? new NoteBounds(48 + offset, 64 + offset, 420, 480)));
        session.View.NewRequested += OnNew;
        session.View.ListRequested += OnList;
        session.View.CloseRequested += OnHide;
        session.View.TrashRequested += OnTrash;
        entry.Session = session;
        entry.Hidden = false;
        entry.Error = null;
    }
    public void Create()
    {
        if (IsBusy) return;
        CreateCore(Guid.NewGuid());
        Publish();
    }
    private void CreateCore(Guid id)
    {
        if (_entries.Count >= 1000) throw new InvalidOperationException("메모는 초기 한도 1,000개까지 만들 수 있습니다.");
        var entry = new Entry(new FileNoteRepository(_root, id));
        // Do not add an unusable empty entry if the surface cannot be opened.
        Open(entry, null);
        _entries.Add(id, entry);
    }
    private void OnNew(object? sender, EventArgs e)
    {
        try { Create(); }
        catch (Exception ex) when (IsNoteError(ex)) { Notice?.Invoke(this, "새 메모를 열지 못했습니다. 기존 내용은 유지됩니다."); }
    }
    private void OnList(object? sender, EventArgs e) => ListRequested?.Invoke(this, EventArgs.Empty);
    private async void OnHide(object? sender, EventArgs e)
    {
        var pair = _entries.FirstOrDefault(pair => ReferenceEquals(pair.Value.Session?.View, sender));
        if (IsBusy) return;
        if (pair.Value is not null && !await HideAsync(pair.Key)) Notice?.Invoke(this, "저장하지 못해 숨김을 취소했습니다. 메모를 유지하고 있으니 다시 시도하세요.");
    }
    public async Task<bool> HideAsync(Guid id)
    {
        if (IsBusy || !_entries.TryGetValue(id, out var entry) || entry.Session is null) return false;
        _busy = true; Publish();
        try
        {
            if (!await entry.Session.HideAsync()) return false;
            entry.Session = null;
            entry.Hidden = true;
            return true;
        }
        finally { _busy = false; Publish(); }
    }
    public async Task<bool> ShowAsync(Guid id)
    {
        if (IsBusy || !_entries.TryGetValue(id, out var entry)) return false;
        if (entry.Trashed) return false;
        if (entry.Session is not null) return true;
        _busy = true; Publish();
        try
        {
            var loaded = await LoadAsync(entry.Repository);
            if (loaded.Record is null) throw new System.IO.InvalidDataException("메모 파일이 없습니다.");
            if (loaded.Record.DeletedAtUtc is not null) { entry.Trashed = true; entry.Hidden = true; return false; }
            Open(entry, loaded.Record);
            // Make the visible state durable; a failure keeps the editor available for retry.
            bool saved = await entry.Session!.FlushAsync();
            if (!saved) Notice?.Invoke(this, "표시 상태를 저장하지 못했습니다. 메모는 열어 두었습니다. 저장을 다시 시도하세요.");
            if (loaded.Recovered) Notice?.Invoke(this, "이전 정상 백업에서 메모를 복원했습니다. 원본은 보존됩니다.");
            return true;
        }
        catch (Exception ex) when (IsNoteError(ex))
        { entry.Error = "복원하지 못했습니다. 파일은 보존됩니다."; return false; }
        finally { _busy = false; Publish(); }
    }
    private async void OnTrash(object? sender, EventArgs e)
    {
        if (IsBusy) return;
        var pair = _entries.FirstOrDefault(pair => ReferenceEquals(pair.Value.Session?.View, sender));
        if (pair.Value is not null && !await TrashAsync(pair.Key))
            Notice?.Invoke(this, "저장하지 못해 휴지통 이동을 취소했습니다. 내용을 유지하고 있습니다.");
    }
    public async Task<bool> TrashAsync(Guid id)
    {
        if (IsBusy || !_entries.TryGetValue(id, out var entry) || entry.Trashed) return false;
        _busy = true; Publish();
        try
        {
            if (entry.Session is not null)
            {
                if (!await entry.Session.TrashAsync()) return false;
                entry.Session = null;
            }
            else
            {
                var loaded = await LoadAsync(entry.Repository);
                if (loaded.Record is null) return false;
                var now = DateTimeOffset.UtcNow;
                await Task.Run(() => entry.Repository.Save(loaded.Record with
                    { SchemaVersion = 3, Hidden = true, DeletedAtUtc = now, UpdatedAtUtc = now }));
            }
            entry.Trashed = true; entry.Hidden = true; entry.Error = null;
            return true;
        }
        catch (Exception ex) when (IsNoteError(ex)) { entry.Error = "휴지통 이동 실패 · 파일 보존"; return false; }
        finally { _busy = false; Publish(); }
    }
    public async Task<bool> RestoreAsync(Guid id)
    {
        if (IsBusy || !_entries.TryGetValue(id, out var entry) || !entry.Trashed) return false;
        _busy = true; Publish();
        try
        {
            var loaded = await LoadAsync(entry.Repository);
            if (loaded.Record?.DeletedAtUtc is null) return false;
            await Task.Run(() => entry.Repository.Save(loaded.Record with
                { SchemaVersion = 3, Hidden = true, DeletedAtUtc = null, UpdatedAtUtc = DateTimeOffset.UtcNow }));
            // Restore to the normal list, hidden; opening is a separate action.
            entry.Trashed = false; entry.Hidden = true; entry.Error = null;
            return true;
        }
        catch (Exception ex) when (IsNoteError(ex)) { entry.Error = "복원 실패 · 파일 보존"; return false; }
        finally { _busy = false; Publish(); }
    }
    public async Task<bool> PermanentlyDeleteAsync(Guid id, bool confirmed)
    {
        if (!confirmed || IsBusy || !_entries.TryGetValue(id, out var entry) || !entry.Trashed || entry.Session is not null) return false;
        _busy = true; Publish();
        try
        {
            await Task.Run(entry.Repository.Purge);
            _entries.Remove(id);
            return true;
        }
        catch (Exception ex) when (IsNoteError(ex)) { entry.Error = "영구 삭제 실패 · 다시 시도하세요"; return false; }
        finally { _busy = false; Publish(); }
    }

    public async Task<bool> FlushAllAsync()
    {
        if (IsBusy) return false;
        _busy = true; Publish();
        var sessions = _entries.Values.Select(entry => entry.Session).OfType<NoteSession>().ToArray();
        foreach (var session in sessions) session.SetEnabled(false);
        try
        {
            bool success = true;
            // Try every visible note; one failure must not discard or skip another note.
            foreach (var session in sessions) if (!await session.FlushAsync()) success = false;
            return success;
        }
        finally
        {
            foreach (var session in sessions) session.SetEnabled(true);
            _busy = false; Publish();
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values) entry.Session?.Dispose();
        _entries.Clear();
        Changed = null; ListRequested = null; Notice = null;
    }
}
