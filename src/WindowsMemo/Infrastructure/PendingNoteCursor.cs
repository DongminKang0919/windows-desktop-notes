using System.IO;

namespace WindowsMemo.Infrastructure;

// A forward-only directory walk. Holds one lookahead path, never the entire file list.
internal sealed class PendingNoteCursor : IDisposable
{
    private readonly string _root;
    private readonly Func<IEnumerator<string>> _enumerate;
    private readonly object _gate = new();
    private IEnumerator<string>? _iterator;
    private string? _lookahead;
    private bool _started, _ended;
    private int _disposed;

    internal PendingNoteCursor(string root, Func<IEnumerator<string>>? enumerate = null)
    {
        _root = Path.GetFullPath(root);
        _enumerate = enumerate ?? (() => Directory.EnumerateFiles(_root, "*.tmp").GetEnumerator());
    }

    internal PendingNoteScan NextPage()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            try
            {
                FileNoteRepository.CheckRoot(_root);
                if (_ended) return new([], false, 0);
                if (!_started)
                {
                    _started = true;
                    if (!Directory.Exists(_root)) { Complete(); return new([], false, 0); }
                    _iterator = _enumerate();
                }
                var files = new List<PendingNoteFile>(); int skipped = 0;
                for (int i = 0; i < TemporaryNoteMaintenance.MaxFiles; i++)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    string? path = _lookahead;
                    _lookahead = null;
                    if (path is null)
                    {
                        if (!_iterator!.MoveNext()) { Complete(); break; }
                        path = _iterator.Current;
                    }
                    string name = Path.GetFileName(path);
                    if (name.Length != 36 || !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                        || !Guid.TryParseExact(name[..32], "N", out Guid token) || token == Guid.Empty) { skipped++; continue; }
                    try
                    {
                        FileNoteRepository.CheckLink(path);
                        files.Add(new(token, File.GetLastWriteTimeUtc(path)));
                    }
                    catch (Exception ex) when (FileNoteRepository.IsStorageError(ex)) { skipped++; }
                }
                if (!_ended)
                {
                    if (_iterator!.MoveNext()) _lookahead = _iterator.Current;
                    else Complete();
                }
                FileNoteRepository.CheckRoot(_root);
                return new(files.OrderByDescending(file => file.ModifiedUtc).ToArray(), !_ended, skipped);
            }
            catch { Complete(); throw; }
            finally { if (Volatile.Read(ref _disposed) != 0) Complete(); }
        }
    }
    private void Complete()
    {
        _ended = true; _lookahead = null;
        _iterator?.Dispose(); _iterator = null;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Closing a window must not wait for an in-flight filesystem call.
        // Also queue a release to cover disposal racing with the reader leaving its finally.
        if (!Monitor.TryEnter(_gate))
        {
            ThreadPool.QueueUserWorkItem(_ => { lock (_gate) Complete(); });
            return;
        }
        try { Complete(); }
        finally { Monitor.Exit(_gate); }
    }
}
