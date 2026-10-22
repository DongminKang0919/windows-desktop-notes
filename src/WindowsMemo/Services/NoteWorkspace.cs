using System.Windows.Threading;
using WindowsMemo.Infrastructure;
using WindowsMemo.Models;
using WindowsMemo.Views;

namespace WindowsMemo.Services;

internal sealed record NoteListItem(Guid Id, bool Hidden, string? Error, bool Trashed = false, bool Archived = false, string Title = "메모")
{
    public string StatusLabel => $"{(Error is not null ? "처리 실패" : Trashed ? "휴지통" : Archived ? "보관됨" : Hidden ? "숨김" : "표시 중")} · {Id.ToString("N")[..8]}";
    public string Label => $"{Title} · {StatusLabel}";
}

internal sealed class NoteWorkspace : IDisposable
{
    private sealed class Entry(FileNoteRepository repository)
    {
        public FileNoteRepository Repository { get; } = repository;
        public bool Hidden { get; set; }
        public bool Trashed { get; set; }
        public bool Archived { get; set; }
        public string? Error { get; set; }
        public NoteSession? Session { get; set; }
        public string Title { get; set; } = "메모";
    }
    private readonly string _root;
    private readonly Dispatcher _dispatcher;
    private readonly Func<NoteView, NoteBounds?, INoteSurface> _createSurface;
    private readonly Dictionary<Guid, Entry> _entries = new();
    private bool _busy, _disposed;
    private bool _initialized;
    internal SessionEndState SessionEndState
    {
        get
        {
            if (_disposed) return SessionEndState.Disposed;
            if (!_initialized) return SessionEndState.Preparing;
            if (_busy) return SessionEndState.Busy;
            var states = _entries.Values.Select(entry => entry.Session?.SaveState).ToArray();
            if (states.Contains(SaveState.Saving)) return SessionEndState.Saving;
            if (states.Contains(SaveState.Failed)) return SessionEndState.Failed;
            if (states.Contains(SaveState.Dirty)) return SessionEndState.Dirty;
            if (states.Contains(SaveState.Disposed)) return SessionEndState.Disposed;
            return SessionEndState.Saved;
        }
    }
    public bool IsBusy => _busy || _disposed;
    internal TemporaryFileResult TemporaryFiles { get; private set; } = new(0, 0, 0, 0, false);
    public event EventHandler? Changed;
    public event EventHandler? ListRequested;
    public event EventHandler? ArchiveListRequested;
    public event EventHandler<string>? Notice;
    public IReadOnlyList<NoteListItem> Items => List(false);
    public IReadOnlyList<NoteListItem> TrashItems => List(true);
    public IReadOnlyList<NoteListItem> ArchiveItems => List(false, true);
    private Guid? _lastArchived;
    public bool CanUndoArchive => _lastArchived is { } id && _entries.TryGetValue(id, out var entry) && entry.Archived;
    private IReadOnlyList<NoteListItem> List(bool trashed, bool archived = false) => _entries.Where(pair => pair.Value.Trashed == trashed && pair.Value.Archived == archived)
        .Select(pair => new NoteListItem(pair.Key, pair.Value.Hidden, pair.Value.Error, pair.Value.Trashed, pair.Value.Archived, pair.Value.Title)).ToArray();

    public NoteWorkspace(string root, Func<NoteView, NoteBounds?, INoteSurface> createSurface)
    { _root = root; _createSurface = createSurface; _dispatcher = Dispatcher.CurrentDispatcher; }

    private async Task<(NoteRecord? Record, bool Recovered)> LoadAsync(Entry entry)
    {
        // File access/decryption stay off the UI thread; WPF content validation stays on it.
        string title = "메모";
        var loaded = await Task.Run(() =>
        {
            var record = entry.Repository.Load(value => _dispatcher.Invoke(() =>
            {
                title = NoteTitleSummary.FromDocument(RtfDocumentCodec.Load(value.Rtf));
            }), out bool recovered);
            return (record, recovered);
        });
        if (loaded.record is not null) entry.Title = title;
        return loaded;
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
            if (_disposed) return;
            TemporaryFiles = await Task.Run(() => TemporaryNoteMaintenance.Run(_root, DateTimeOffset.UtcNow,
                record => _dispatcher.Invoke(() => RtfDocumentCodec.Load(record.Rtf))));
            if (_disposed) return;
            var ids = await Task.Run(() => FileNoteRepository.DiscoverIds(_root));
            if (_disposed) return;
            foreach (Guid id in ids)
            {
                if (_disposed) return;
                var entry = new Entry(new FileNoteRepository(_root, id));
                _entries.Add(id, entry);
                try
                {
                    var loaded = await LoadAsync(entry);
                    if (_disposed) return;
                    if (loaded.Record is null) throw new System.IO.InvalidDataException("메모 파일이 없어졌습니다.");
                    entry.Hidden = loaded.Record.Hidden;
                    entry.Trashed = loaded.Record.DeletedAtUtc is not null;
                    entry.Archived = loaded.Record.Archived;
                    if (!entry.Hidden && !entry.Trashed && !entry.Archived) Open(entry, loaded.Record);
                    if (loaded.Recovered) Notice?.Invoke(this, "이전 정상 백업에서 메모를 복원했습니다. 최근 변경 일부가 없을 수 있습니다. 원본은 보존됩니다.");
                }
                catch (Exception ex) when (IsNoteError(ex)) { entry.Title = "확인할 수 없는 메모"; entry.Error = "복원하지 못했습니다. 파일은 보존됩니다."; }
            }
            if (_disposed) return;
            if (_entries.Count == 0) CreateCore(Guid.NewGuid());
            _initialized = true;
        }
        finally { _busy = false; Publish(); }
    }
    private void Open(Entry entry, NoteRecord? record)
    {
        var offset = (_entries.Count % 10) * 28;
        var session = new NoteSession(entry.Repository, record, (view, bounds) =>
            _createSurface(view, bounds ?? new NoteBounds(48 + offset, 64 + offset, NoteTheme.Width, NoteTheme.Height)));
        session.View.NewRequested += OnNew;
        session.View.ListRequested += OnList;
        session.View.CloseRequested += OnHide;
        session.View.TrashRequested += OnTrash;
        session.View.ArchiveRequested += OnArchive;
        session.View.ArchiveListRequested += OnArchiveList;
        entry.Session = session;
        entry.Title = session.View.NoteTitle;
        session.View.DocumentChanged += (_, _) =>
        {
            if (entry.Title == session.View.NoteTitle) return;
            entry.Title = session.View.NoteTitle;
            Publish();
        }; // NoteView.Dispose clears DocumentChanged when this session is hidden/disposed.
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
    private void OnArchiveList(object? sender, EventArgs e) => ArchiveListRequested?.Invoke(this, EventArgs.Empty);
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
        if (entry.Trashed || entry.Archived) return false;
        if (entry.Session is not null) return true;
        _busy = true; Publish();
        try
        {
            var loaded = await LoadAsync(entry);
            if (loaded.Record is null) throw new System.IO.InvalidDataException("메모 파일이 없습니다.");
            if (loaded.Record.DeletedAtUtc is not null) { entry.Trashed = true; entry.Hidden = true; return false; }
            if (loaded.Record.Archived) { entry.Archived = true; entry.Hidden = true; return false; }
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
    internal Task<PendingNoteScan> ScanPendingAsync() => Task.Run(() => new PendingNoteRecovery(_root).Scan());
    internal PendingNoteCursor OpenPendingCursor() => new(_root);
    internal Task<PendingNotePreview> PreviewPendingAsync(Guid token) => Task.Run(() => new PendingNoteRecovery(_root).Read(token));
    internal async Task<PendingRecoveryResult> RecoverPendingAsync(PendingNotePreview preview)
    {
        if (IsBusy) return PendingRecoveryResult.Failed(PendingRecoveryStatus.Busy);
        _busy = true; Publish();
        var failure = PendingRecoveryStatus.SourceUnavailable;
        try
        {
            // Re-read the source: the preview's mutable record is never trusted as save input.
            var verified = await Task.Run(() => new PendingNoteRecovery(_root).Read(preview.Token, preview.Fingerprint));
            string title = NoteTitleSummary.FromDocument(RtfDocumentCodec.Load(verified.Record.Rtf));
            if (_disposed) return PendingRecoveryResult.Failed(PendingRecoveryStatus.Busy);
            Guid id = PendingNoteRecovery.CopyId(verified.Token, verified.Fingerprint);
            if (id == verified.Record.Id) return PendingRecoveryResult.Failed(PendingRecoveryStatus.ExistingCopyUnavailable);
            if (_entries.TryGetValue(id, out var existing))
            {
                failure = PendingRecoveryStatus.ExistingCopyUnavailable;
                if (existing.Session is null)
                {
                    var loaded = await LoadAsync(existing);
                    if (_disposed) return PendingRecoveryResult.Failed(PendingRecoveryStatus.Busy);
                    if (loaded.Record is null) return PendingRecoveryResult.Failed(failure);
                    existing.Hidden = loaded.Record.Hidden;
                    existing.Archived = loaded.Record.Archived;
                    existing.Trashed = loaded.Record.DeletedAtUtc is not null;
                    existing.Error = null;
                }
                return new(id, PendingRecoveryStatus.AlreadyRecovered);
            }
            if (_entries.Count >= 1000) return PendingRecoveryResult.Failed(PendingRecoveryStatus.LimitReached);
            failure = PendingRecoveryStatus.SaveFailed;
            var repository = new FileNoteRepository(_root, id);
            var now = DateTimeOffset.UtcNow;
            var copy = verified.Record with { SchemaVersion = 4, Id = id, CreatedAtUtc = now, UpdatedAtUtc = now,
                Hidden = true, Archived = false, DeletedAtUtc = null, Collapsed = false };
            await Task.Run(() => repository.SaveNew(copy));
            // Durable hidden copy first: failure cannot replace an existing note or lose the source.
            if (!_disposed)
            {
                _entries.Add(id, new Entry(repository) { Hidden = true, Title = title });
            }
            return new(id, PendingRecoveryStatus.Created);
        }
        catch (Exception ex) when (IsNoteError(ex))
        {
            return failure == PendingRecoveryStatus.SourceUnavailable
                ? PendingRecoveryResult.SourceFailure(ex) : PendingRecoveryResult.Failed(failure);
        }
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
                var loaded = await LoadAsync(entry);
                if (loaded.Record is null) return false;
                var now = DateTimeOffset.UtcNow;
                await Task.Run(() => entry.Repository.Save(loaded.Record with
                    { SchemaVersion = 4, Hidden = true, Archived = false, DeletedAtUtc = now, UpdatedAtUtc = now }));
            }
            entry.Trashed = true; entry.Hidden = true; entry.Archived = false; entry.Error = null;
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
            var loaded = await LoadAsync(entry);
            if (loaded.Record?.DeletedAtUtc is null) return false;
            await Task.Run(() => entry.Repository.Save(loaded.Record with
                { SchemaVersion = 4, Hidden = true, DeletedAtUtc = null, UpdatedAtUtc = DateTimeOffset.UtcNow }));
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

    private async void OnArchive(object? sender, EventArgs e)
    {
        var pair = _entries.FirstOrDefault(pair => ReferenceEquals(pair.Value.Session?.View, sender));
        if (pair.Value is not null && !await ArchiveAsync(pair.Key))
            Notice?.Invoke(this, "저장하지 못해 보관을 취소했습니다. 최신 내용을 유지하고 있으니 다시 시도하세요.");
    }
    public async Task<bool> ArchiveAsync(Guid id)
    {
        if (IsBusy || !_entries.TryGetValue(id, out var entry) || entry.Trashed || entry.Archived) return false;
        _busy = true; Publish();
        try
        {
            if (entry.Session is not null)
            {
                if (!await entry.Session.ArchiveAsync()) return false;
                entry.Session = null;
            }
            else
            {
                var loaded = await LoadAsync(entry);
                if (loaded.Record is null) return false;
                await Task.Run(() => entry.Repository.Save(loaded.Record with
                    { SchemaVersion = 4, Hidden = true, Archived = true, UpdatedAtUtc = DateTimeOffset.UtcNow }));
            }
            entry.Archived = true; entry.Hidden = true; entry.Error = null; _lastArchived = id;
            return true;
        }
        catch (Exception ex) when (IsNoteError(ex)) { entry.Error = "보관 실패 · 내용 유지"; return false; }
        finally { _busy = false; Publish(); }
    }
    public async Task<bool> UnarchiveAsync(Guid id)
    {
        if (IsBusy || !_entries.TryGetValue(id, out var entry) || !entry.Archived || entry.Trashed) return false;
        _busy = true; Publish();
        try
        {
            var loaded = await LoadAsync(entry);
            if (loaded.Record is null) return false;
            await Task.Run(() => entry.Repository.Save(loaded.Record with
                { SchemaVersion = 4, Archived = false, Hidden = true, UpdatedAtUtc = DateTimeOffset.UtcNow }));
            entry.Archived = false; entry.Hidden = true; entry.Error = null;
            return true;
        }
        catch (Exception ex) when (IsNoteError(ex)) { entry.Error = "보관 복원 실패 · 파일 유지"; return false; }
        finally { _busy = false; Publish(); }
    }
    public async Task<bool> UndoArchiveAsync()
    {
        if (!CanUndoArchive || _lastArchived is not { } id) return false;
        if (!await UnarchiveAsync(id)) return false;
        return await ShowAsync(id);
    }

    public Task<bool> FlushAllAsync() => SaveThenAsync(null);

    // Keep editing and workspace mutations blocked until the update/exit handoff finishes.
    public Task<bool> SaveThenAsync(Action? afterSave) => FlushCoreAsync(afterSave);
    private async Task<bool> FlushCoreAsync(Action? afterSave)
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
            if (success && !_disposed) afterSave?.Invoke();
            return success && !_disposed;
        }
        finally
        {
            if (!_disposed) foreach (var session in sessions) session.SetEnabled(true);
            _busy = false; Publish();
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values) entry.Session?.Dispose();
        _entries.Clear();
        Changed = null; ListRequested = null; ArchiveListRequested = null; Notice = null;
    }
}
